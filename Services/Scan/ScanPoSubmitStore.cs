using System.Text.Json;
using ApprovalPO.Models;

namespace ApprovalPO.Services.Scan;

public interface IScanPoSubmitStore
{
    Task<IReadOnlyList<ScanPoSubmitSummary>> GetSubmitSummariesAsync(
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default);

    Task<ScanPoSubmissionState> GetStateAsync(
        int docKey,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default);

    Task SaveDraftAsync(
        int docKey,
        string poNumber,
        IReadOnlyDictionary<string, int> scanCounts,
        ScanPoAuditActor actor,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default);

    Task MarkSubmittedAsync(
        int docKey,
        string poNumber,
        IReadOnlyDictionary<string, int> scanCounts,
        ScanPoAuditActor actor,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default);

    Task ClearSubmissionAsync(
        int docKey,
        string poNumber,
        ScanPoAuditActor actor,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default);

    Task ClearDraftAsync(
        int docKey,
        string poNumber,
        ScanPoAuditActor actor,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default);
}

/// <summary>Persists scan drafts, submissions, and audit trail per tenant (JSON under <c>Data</c>).</summary>
public sealed class ScanPoSubmitFileStore : IScanPoSubmitStore
{
    private const int MaxAuditEntries = 2000;

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

    public async Task<IReadOnlyList<ScanPoSubmitSummary>> GetSubmitSummariesAsync(
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default)
    {
        var kind = NormalizeKind(docKind);
        var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
        return file.Submissions
            .Where(s => s.DocKey > 0 && KindEquals(s.DocKind, kind))
            .OrderByDescending(s => s.SubmittedAtUtc)
            .Select(s => new ScanPoSubmitSummary
            {
                DocKey = s.DocKey,
                PoNumber = s.PoNumber,
                SubmittedAtUtc = s.SubmittedAtUtc,
                SubmittedByEmail = s.SubmittedByEmail,
                SubmittedByName = s.SubmittedByName
            })
            .ToList();
    }

    public async Task<ScanPoSubmissionState> GetStateAsync(
        int docKey,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return new ScanPoSubmissionState { DocKey = docKey };

        var kind = NormalizeKind(docKind);
        var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
        var auditForDoc = AuditForDoc(file, docKey, kind);

        var sub = file.Submissions.FirstOrDefault(s => s.DocKey == docKey && KindEquals(s.DocKind, kind));
        if (sub is not null)
        {
            return new ScanPoSubmissionState
            {
                DocKey = docKey,
                IsSubmitted = true,
                SubmittedAtUtc = sub.SubmittedAtUtc,
                PoNumber = sub.PoNumber,
                ScanCounts = NormalizeCounts(sub.ScanCounts),
                SubmittedByEmail = sub.SubmittedByEmail,
                SubmittedByName = sub.SubmittedByName,
                AuditTrail = auditForDoc
            };
        }

        var draft = file.Drafts.FirstOrDefault(d => d.DocKey == docKey && KindEquals(d.DocKind, kind));
        if (draft is not null)
        {
            return new ScanPoSubmissionState
            {
                DocKey = docKey,
                IsSubmitted = false,
                PoNumber = draft.PoNumber,
                ScanCounts = NormalizeCounts(draft.ScanCounts),
                DraftUpdatedAtUtc = draft.UpdatedAtUtc,
                DraftUpdatedByEmail = draft.UpdatedByEmail,
                DraftUpdatedByName = draft.UpdatedByName,
                AuditTrail = auditForDoc
            };
        }

        return new ScanPoSubmissionState
        {
            DocKey = docKey,
            AuditTrail = auditForDoc
        };
    }

    public async Task SaveDraftAsync(
        int docKey,
        string poNumber,
        IReadOnlyDictionary<string, int> scanCounts,
        ScanPoAuditActor actor,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return;

        var kind = NormalizeKind(docKind);
        var counts = NormalizeCounts(scanCounts);
        var po = poNumber?.Trim() ?? "";

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileUnlockedAsync(cancellationToken).ConfigureAwait(false);
            if (file.Submissions.Any(s => s.DocKey == docKey && KindEquals(s.DocKind, kind)))
                return;

            file.Drafts.RemoveAll(d => d.DocKey == docKey && KindEquals(d.DocKind, kind));
            file.Drafts.Add(new ScanPoDraftRecord
            {
                DocKey = docKey,
                DocKind = kind,
                PoNumber = po,
                UpdatedAtUtc = DateTime.UtcNow,
                ScanCounts = counts,
                UpdatedByEmail = actor.Email,
                UpdatedByName = actor.DisplayName
            });

            AppendAudit(file, docKey, kind, po, "draft_saved", actor, TotalScans(counts));
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
        ScanPoAuditActor actor,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return;

        var kind = NormalizeKind(docKind);
        var counts = NormalizeCounts(scanCounts);
        var po = poNumber?.Trim() ?? "";

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileUnlockedAsync(cancellationToken).ConfigureAwait(false);
            file.Drafts.RemoveAll(d => d.DocKey == docKey && KindEquals(d.DocKind, kind));
            file.Submissions.RemoveAll(s => s.DocKey == docKey && KindEquals(s.DocKind, kind));
            file.Submissions.Add(new ScanPoSubmitRecord
            {
                DocKey = docKey,
                DocKind = kind,
                PoNumber = po,
                SubmittedAtUtc = DateTime.UtcNow,
                ScanCounts = counts,
                SubmittedByEmail = actor.Email,
                SubmittedByName = actor.DisplayName
            });

            AppendAudit(file, docKey, kind, po, "submitted", actor, TotalScans(counts));
            await WriteFileUnlockedAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearSubmissionAsync(
        int docKey,
        string poNumber,
        ScanPoAuditActor actor,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return;

        var kind = NormalizeKind(docKind);
        var po = poNumber?.Trim() ?? "";

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var sub = file.Submissions.FirstOrDefault(s => s.DocKey == docKey && KindEquals(s.DocKind, kind));
            if (string.IsNullOrEmpty(po) && sub is not null)
                po = sub.PoNumber;

            file.Submissions.RemoveAll(s => s.DocKey == docKey && KindEquals(s.DocKind, kind));
            AppendAudit(file, docKey, kind, po, "reopened", actor, sub is null ? null : TotalScans(sub.ScanCounts));
            await WriteFileUnlockedAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearDraftAsync(
        int docKey,
        string poNumber,
        ScanPoAuditActor actor,
        string docKind = ScanDocKinds.PurchaseOrder,
        CancellationToken cancellationToken = default)
    {
        if (docKey <= 0)
            return;

        var kind = NormalizeKind(docKind);
        var po = poNumber?.Trim() ?? "";

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadFileUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var draft = file.Drafts.FirstOrDefault(d => d.DocKey == docKey && KindEquals(d.DocKind, kind));
            if (string.IsNullOrEmpty(po) && draft is not null)
                po = draft.PoNumber;

            if (file.Submissions.Any(s => s.DocKey == docKey && KindEquals(s.DocKind, kind)))
                return;

            file.Drafts.RemoveAll(d => d.DocKey == docKey && KindEquals(d.DocKind, kind));
            AppendAudit(file, docKey, kind, po, "scan_reset", actor, draft is null ? null : TotalScans(draft.ScanCounts));
            await WriteFileUnlockedAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string NormalizeKind(string? docKind)
    {
        var k = (docKind ?? "").Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(k) ? ScanDocKinds.PurchaseOrder : k;
    }

    private static bool KindEquals(string? stored, string kind)
    {
        var s = (stored ?? "").Trim();
        if (s.Length == 0)
            s = ScanDocKinds.PurchaseOrder;
        return s.Equals(kind, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ScanPoAuditEntry> AuditForDoc(ScanPoSubmitFile file, int docKey, string kind) =>
        file.AuditTrail
            .Where(e => e.DocKey == docKey && KindEquals(e.DocKind, kind))
            .OrderByDescending(e => e.AtUtc)
            .Take(50)
            .ToList();

    private static void AppendAudit(
        ScanPoSubmitFile file,
        int docKey,
        string docKind,
        string poNumber,
        string action,
        ScanPoAuditActor actor,
        int? totalScans)
    {
        file.AuditTrail.Add(new ScanPoAuditEntry
        {
            DocKey = docKey,
            DocKind = docKind,
            PoNumber = poNumber,
            Action = action,
            AtUtc = DateTime.UtcNow,
            UserEmail = actor.Email,
            UserDisplayName = actor.DisplayName,
            TotalScans = totalScans
        });

        if (file.AuditTrail.Count > MaxAuditEntries)
            file.AuditTrail.RemoveRange(0, file.AuditTrail.Count - MaxAuditEntries);
    }

    private static int TotalScans(IReadOnlyDictionary<string, int> counts) =>
        counts.Values.Sum();

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
        public List<ScanPoAuditEntry> AuditTrail { get; set; } = new();
    }

    private sealed class ScanPoSubmitRecord
    {
        public int DocKey { get; set; }
        public string DocKind { get; set; } = ScanDocKinds.PurchaseOrder;
        public string PoNumber { get; set; } = "";
        public DateTime SubmittedAtUtc { get; set; }
        public Dictionary<string, int> ScanCounts { get; set; } = new();
        public string SubmittedByEmail { get; set; } = "";
        public string SubmittedByName { get; set; } = "";
    }

    private sealed class ScanPoDraftRecord
    {
        public int DocKey { get; set; }
        public string DocKind { get; set; } = ScanDocKinds.PurchaseOrder;
        public string PoNumber { get; set; } = "";
        public DateTime UpdatedAtUtc { get; set; }
        public Dictionary<string, int> ScanCounts { get; set; } = new();
        public string UpdatedByEmail { get; set; } = "";
        public string UpdatedByName { get; set; } = "";
    }
}
