using ApprovalPO.Helpers;
using ApprovalPO.Options;
using Microsoft.Extensions.Options;

namespace ApprovalPO.Services;

/// <summary>
/// Periodically re-fetches AWS tenant-config so dashboard module true/false flags (and related
/// tenant payload fields) update without restarting the Windows service.
/// </summary>
public sealed class TenantConfigRefreshWorker : BackgroundService
{
    private readonly TenantDbConnectionResolver _tenants;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<ApprovalOptions> _approval;
    private readonly ILogger<TenantConfigRefreshWorker> _logger;

    public TenantConfigRefreshWorker(
        TenantDbConnectionResolver tenants,
        IConfiguration configuration,
        IOptionsMonitor<ApprovalOptions> approval,
        ILogger<TenantConfigRefreshWorker> logger)
    {
        _tenants = tenants;
        _configuration = configuration;
        _approval = approval;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = _approval.CurrentValue.TenantConfigRefreshSecondsClamped;
        if (seconds <= 0)
        {
            _logger.LogInformation("Tenant AWS config refresh worker skipped (Approval:TenantConfigRefreshSeconds is 0).");
            return;
        }

        _logger.LogInformation("Tenant AWS config refresh worker started; refresh every {Seconds}s.", seconds);

        // First refresh after one interval (startup warmup already filled the cache).
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                seconds = _approval.CurrentValue.TenantConfigRefreshSecondsClamped;
                if (seconds <= 0)
                {
                    _logger.LogInformation("Tenant AWS config refresh disabled at runtime; worker stopping.");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
                await RefreshOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tenant AWS config refresh failed; keeping previous cached config.");
            }
        }
    }

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        if (!TenantConfigurationHelper.TryGetTenantCode(_configuration, out var tenant, out _))
            return;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _tenants.RefreshTenantConfigAsync(tenant, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Refreshed AWS tenant config for {Tenant} in {ElapsedMs} ms (module flags / roles / DB / license).",
            tenant,
            sw.ElapsedMilliseconds);
    }
}
