using System.Net;
using System.Text.Json;
using ApprovalPO.Models;
using ApprovalPO.Options;
using Microsoft.Extensions.Options;
using WebPush;

namespace ApprovalPO.Services.Push;

/// <summary>
/// Polls for new pending purchase orders and sends Web Push payloads to all stored subscriptions.
/// </summary>
public sealed class PendingOrderWebPushWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<WebPushOptions> _webPush;
    private readonly WebPushSubscriptionFileStore _subscriptions;
    private readonly WebPushPendingCursorStore _cursor;
    private readonly ILogger<PendingOrderWebPushWorker> _logger;

    public PendingOrderWebPushWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<WebPushOptions> webPush,
        WebPushSubscriptionFileStore subscriptions,
        WebPushPendingCursorStore cursor,
        ILogger<PendingOrderWebPushWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _webPush = webPush;
        _subscriptions = subscriptions;
        _cursor = cursor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_webPush.Value.Enabled || !_webPush.Value.HasVapidKeys)
        {
            _logger.LogInformation("Web push worker skipped (WebPush:Enabled false or VAPID keys missing).");
            return;
        }

        var delay = TimeSpan.FromSeconds(_webPush.Value.PollSecondsClamped);
        _logger.LogInformation("Web push worker started; poll every {Seconds}s.", delay.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Web push poll failed.");
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var opts = _webPush.Value;
        if (!opts.Enabled || !opts.HasVapidKeys)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IPurchaseOrderCatalog>();
        var orders = await catalog.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        var pending = orders
            .Where(o => string.Equals(o.Status, "Pending", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var currentKeys = pending.Select(o => o.DocKey).Where(k => k > 0).ToHashSet();
        var previous = await _cursor.LoadAsync(cancellationToken).ConfigureAwait(false);

        if (previous.Count == 0)
        {
            await _cursor.SaveAsync(currentKeys, cancellationToken).ConfigureAwait(false);
            if (currentKeys.Count > 0)
                _logger.LogInformation("Web push baseline established ({Count} pending doc keys).", currentKeys.Count);
            return;
        }

        var newOnes = pending.Where(o => !previous.Contains(o.DocKey)).ToList();
        await _cursor.SaveAsync(currentKeys, cancellationToken).ConfigureAwait(false);

        if (newOnes.Count == 0)
            return;

        var payloadObj = new
        {
            title = newOnes.Count == 1
                ? "New order — PENDING approval"
                : $"{newOnes.Count} new orders — PENDING approval",
            body = "Status PENDING — "
                + string.Join(", ", newOnes.Take(8).Select(o => string.IsNullOrWhiteSpace(o.PoNumber) ? $"#{o.DocKey}" : o.PoNumber.Trim())),
            url = "/PurchaseOrders",
        };
        var payload = JsonSerializer.Serialize(payloadObj);

        var vapid = new VapidDetails(opts.Subject.Trim(), opts.PublicKey.Trim(), opts.PrivateKey.Trim());
        var client = new WebPushClient();
        var subs = await _subscriptions.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (subs.Count == 0)
        {
            _logger.LogDebug("Web push: new pendings detected but no subscriptions.");
            return;
        }

        foreach (var s in subs)
        {
            if (string.IsNullOrWhiteSpace(s.Endpoint) || string.IsNullOrWhiteSpace(s.P256dh) || string.IsNullOrWhiteSpace(s.Auth))
                continue;

            var subscription = new PushSubscription(s.Endpoint, s.P256dh, s.Auth);
            try
            {
                await client.SendNotificationAsync(subscription, payload, vapid, cancellationToken).ConfigureAwait(false);
            }
            catch (WebPushException ex) when (ex.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound)
            {
                await _subscriptions.RemoveByEndpointAsync(s.Endpoint, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Removed expired web push subscription for {Endpoint}", Truncate(s.Endpoint, 80));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Web push send failed for {User}", s.UserEmail);
            }
        }

        _logger.LogInformation("Web push sent for {Count} new pending order(s) to {Subscribers} subscriber(s).", newOnes.Count, subs.Count);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
