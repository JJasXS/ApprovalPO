using System.Text.Json;

namespace ApprovalPO.Services;

/// <summary>Persists browser push subscriptions (JSON file under app <c>Data</c> folder).</summary>
public sealed class WebPushSubscriptionFileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WebPushSubscriptionFileStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "Data");
        _path = Path.Combine(dir, "webpush-subscriptions.json");
    }

    public async Task<IReadOnlyList<WebPushSubscriptionRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return Array.Empty<WebPushSubscriptionRecord>();
            await using var fs = File.OpenRead(_path);
            var list = await JsonSerializer.DeserializeAsync<List<WebPushSubscriptionRecord>>(fs, Json, cancellationToken).ConfigureAwait(false);
            return list ?? (IReadOnlyList<WebPushSubscriptionRecord>)Array.Empty<WebPushSubscriptionRecord>();
        }
        catch
        {
            return Array.Empty<WebPushSubscriptionRecord>();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertAsync(WebPushSubscriptionRecord row, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            List<WebPushSubscriptionRecord> list;
            if (File.Exists(_path))
            {
                await using var read = File.OpenRead(_path);
                list = await JsonSerializer.DeserializeAsync<List<WebPushSubscriptionRecord>>(read, Json, cancellationToken).ConfigureAwait(false)
                       ?? new List<WebPushSubscriptionRecord>();
            }
            else
            {
                list = new List<WebPushSubscriptionRecord>();
            }

            list.RemoveAll(r => string.Equals(r.Endpoint, row.Endpoint, StringComparison.Ordinal));
            list.Add(row);

            await using var write = File.Create(_path);
            await JsonSerializer.SerializeAsync(write, list, Json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveByEndpointAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return;

            await using var read = File.OpenRead(_path);
            var list = await JsonSerializer.DeserializeAsync<List<WebPushSubscriptionRecord>>(read, Json, cancellationToken).ConfigureAwait(false)
                       ?? new List<WebPushSubscriptionRecord>();
            var n = list.RemoveAll(r => string.Equals(r.Endpoint, endpoint, StringComparison.Ordinal));
            if (n == 0)
                return;

            await using var write = File.Create(_path);
            await JsonSerializer.SerializeAsync(write, list, Json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>Last known pending <c>DOCKEY</c> set so we only push on genuinely new pendings after restarts.</summary>
public sealed class WebPushPendingCursorStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public WebPushPendingCursorStore(IWebHostEnvironment env)
    {
        var dir = Path.Combine(env.ContentRootPath, "Data");
        _path = Path.Combine(dir, "webpush-pending-cursor.json");
    }

    public sealed class Snapshot
    {
        public List<int> DocKeys { get; set; } = new();
    }

    public async Task<HashSet<int>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
                return new HashSet<int>();

            await using var fs = File.OpenRead(_path);
            var snap = await JsonSerializer.DeserializeAsync<Snapshot>(fs, Json, cancellationToken).ConfigureAwait(false);
            if (snap?.DocKeys == null || snap.DocKeys.Count == 0)
                return new HashSet<int>();
            return snap.DocKeys.Where(k => k > 0).ToHashSet();
        }
        catch
        {
            return new HashSet<int>();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(HashSet<int> docKeys, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var snap = new Snapshot { DocKeys = docKeys.Where(k => k > 0).OrderBy(k => k).ToList() };
            await using var write = File.Create(_path);
            await JsonSerializer.SerializeAsync(write, snap, Json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}
