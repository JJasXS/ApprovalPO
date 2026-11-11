using System.Text.Json;
using ApprovalPO.Models;

namespace ApprovalPO.Services.Audit;

public interface IApprovalAuditStore
{
    Task AppendAsync(ApprovalAuditEntry entry, CancellationToken cancellationToken = default);
}

public sealed record ApprovalAuditEntry(
    string DocKind,
    int DocKey,
    string? DocNumber,
    string Action,
    string? ActorEmail,
    string? Detail,
    DateTime AtUtc);

/// <summary>Append-only JSON audit for PO/SO approval status changes (per tenant).</summary>
public sealed class ApprovalAuditFileStore : IApprovalAuditStore
{
    private const int MaxEntries = 5000;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ApprovalAuditFileStore(IWebHostEnvironment env, IConfiguration configuration)
    {
        var tenant = (configuration["TenantBootstrap:TenantCode"] ?? "default").Trim();
        if (string.IsNullOrEmpty(tenant))
            tenant = "default";
        var safeTenant = string.Join("_", tenant.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var dir = Path.Combine(env.ContentRootPath, "Data");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"approval-audit-{safeTenant}.json");
    }

    public async Task AppendAsync(ApprovalAuditEntry entry, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = await ReadAsync(cancellationToken).ConfigureAwait(false);
            file.Entries.Add(new StoredEntry
            {
                DocKind = entry.DocKind,
                DocKey = entry.DocKey,
                DocNumber = entry.DocNumber,
                Action = entry.Action,
                ActorEmail = entry.ActorEmail,
                Detail = entry.Detail,
                AtUtc = entry.AtUtc == default ? DateTime.UtcNow : entry.AtUtc,
            });
            if (file.Entries.Count > MaxEntries)
                file.Entries.RemoveRange(0, file.Entries.Count - MaxEntries);

            var json = JsonSerializer.Serialize(file, Json);
            await File.WriteAllTextAsync(_path, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<AuditFile> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new AuditFile();
        try
        {
            await using var stream = File.OpenRead(_path);
            var file = await JsonSerializer.DeserializeAsync<AuditFile>(stream, Json, cancellationToken)
                .ConfigureAwait(false);
            return file ?? new AuditFile();
        }
        catch
        {
            return new AuditFile();
        }
    }

    private sealed class AuditFile
    {
        public List<StoredEntry> Entries { get; set; } = new();
    }

    private sealed class StoredEntry
    {
        public string DocKind { get; set; } = "";
        public int DocKey { get; set; }
        public string? DocNumber { get; set; }
        public string Action { get; set; } = "";
        public string? ActorEmail { get; set; }
        public string? Detail { get; set; }
        public DateTime AtUtc { get; set; }
    }
}
