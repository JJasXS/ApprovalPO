using System.Text.Json;
using ApprovalPO.Models;

namespace ApprovalPO.Services;

public interface IScanPoSubmitStore
{
    Task<IReadOnlySet<int>> GetSubmittedDocKeysAsync(CancellationToken cancellationToken = default);

    Task<ScanPoSubmissionState> GetStateAsync(int docKey, CancellationToken cancellationToken = default);

    Task SaveDraftAsync(
        int docKey,
        string poNumber,
        IReadOnlyDictionary<string, int> scanCounts,
        CancellationToken cancellationToken = default);

    Task MarkSubmittedAsync(
        int docKey,
        string poNumber,
        IReadOnlyDictionary<string, int> scanCounts,
        CancellationToken cancellationToken = default);

    Task ClearSubmissionAsync(int docKey, CancellationToken cancellationToken = default);
}

/// <summary>Persists scan drafts and submissions per tenant (JSON under <c>Data</c>).</summary>
public sealed class ScanPoSubmitFileStore : IScanPoSubmitStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ScanPoSubmitFileStore(IWebHostEnvironment env, IConfiguration configuration)
    {
        var tenant = (configuration["TenantBootstrap:TenantCode"] ?? "default").Trim();
        if (string.IsNullOrEmpty(tenant))
            tenant = "default";

        var safeTenant = string.Join("_", tenant.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var dir = Path.Combine(env.ContentRootPath, "Data");
        _path = Path.Combine(dir, $"scan-po-submits-{safeTenant}.json");
    }

    public async Task<IReadOnlySet<int>> GetSubmittedDocKeysAsync(CancellationToken cancellationToken = default)
    {
        var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
        return file.Submissions
            .Select(s => s.DocKey)
            .Where(k => k > 0)
            .ToHashSet();
    }

    public async Task<ScanPoSubmissionState> GetStateAsync(int docKey, CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return new ScanPoSubmissionState { DocKey = docKey };

        var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
        var sub = file.Submissions.FirstOrDefault(s => s.DocKey == docKey);
        if (sub is not null)
        {
            return new ScanPoSubmissionState
            {
                DocKey = docKey,
                IsSubmitted = true,
                SubmittedAtUtc = sub.SubmittedAtUtc,
                PoNumber = sub.PoNumber,
                ScanCounts = NormalizeCounts(sub.ScanCounts)
            };
        }

        var draft = file.Drafts.FirstOrDefault(d => d.DocKey == docKey);
        if (draft is not null)
        {
            return new ScanPoSubmissionState
            {
                DocKey = docKey,
                IsSubmitted = false,
                PoNumber = draft.PoNumber,
                ScanCounts = NormalizeCounts(draft.ScanCounts)
            };
        }

        return new ScanPoSubmissionState { DocKey = docKey };
    }

    public async Task SaveDraftAsync(
        int docKey,
        string poNumber,
        IReadOnlyDictionary<string, int> scanCounts,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (file.Submissions.Any(s => s.DocKey == docKey))
                return;

            file.Drafts.RemoveAll(d => d.DocKey == docKey);
            file.Drafts.Add(new ScanPoDraftRecord
            {
                DocKey = docKey,
                PoNumber = poNumber?.Trim() ?? "",
                UpdatedAtUtc = DateTime.UtcNow,
                ScanCounts = NormalizeCounts(scanCounts)
            });

            await WriteFileUnlockedAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkSubmittedAsync(
        int docKey,
        string poNumber,
        IReadOnlyDictionary<string, int> scanCounts,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileUnlockedAsync(cancellationToken).ConfigureAwait(false);
            file.Drafts.RemoveAll(d => d.DocKey == docKey);
            file.Submissions.RemoveAll(s => s.DocKey == docKey);
            file.Submissions.Add(new ScanPoSubmitRecord
            {
                DocKey = docKey,
                PoNumber = poNumber?.Trim() ?? "",
                SubmittedAtUtc = DateTime.UtcNow,
                ScanCounts = NormalizeCounts(scanCounts)
            });

            await WriteFileUnlockedAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearSubmissionAsync(int docKey, CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileUnlockedAsync(cancellationToken).ConfigureAwait(false);
            file.Submissions.RemoveAll(s => s.DocKey == docKey);
            await WriteFileUnlockedAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Dictionary<string, int> NormalizeCounts(IReadOnlyDictionary<string, int>? counts)
    {
        if (counts is null || counts.Count == 0)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, n) in counts)
        {
            if (string.IsNullOrWhiteSpace(code) || n <= 0) continue;
            d[code.Trim()] = n;
        }
        return d;
    }

    private async Task WriteFileUnlockedAsync(ScanPoSubmitFile file, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await using var write = File.Create(_path);
        await JsonSerializer.SerializeAsync(write, file, Json, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScanPoSubmitFile> ReadFileAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadFileUnlockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ScanPoSubmitFile> ReadFileUnlockedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new ScanPoSubmitFile();

        try
        {
            await using var fs = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<ScanPoSubmitFile>(fs, Json, cancellationToken).ConfigureAwait(false)
                   ?? new ScanPoSubmitFile();
        }
        catch
        {
            return new ScanPoSubmitFile();
        }
    }

    private sealed class ScanPoSubmitFile
    {
        public List<ScanPoSubmitRecord> Submissions { get; set; } = new();
        public List<ScanPoDraftRecord> Drafts { get; set; } = new();
    }

    private sealed class ScanPoSubmitRecord
    {
        public int DocKey { get; set; }
        public string PoNumber { get; set; } = "";
        public DateTime SubmittedAtUtc { get; set; }
        public Dictionary<string, int> ScanCounts { get; set; } = new();
    }

    private sealed class ScanPoDraftRecord
    {
        public int DocKey { get; set; }
        public string PoNumber { get; set; } = "";
        public DateTime UpdatedAtUtc { get; set; }
        public Dictionary<string, int> ScanCounts { get; set; } = new();
    }
}
