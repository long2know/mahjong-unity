using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Mahjong.Autotable.Api.Auth;

/// <summary>
/// Phase K Wave 2 — Bishop (Backend). Background hosted service that
/// refreshes the <see cref="OAuthDiscoveryService"/> cache on a fixed
/// cadence (default 6h). Vasquez's contract test asserts that ANY
/// <see cref="BackgroundService"/> with "Discovery" in its name is
/// registered as an <see cref="IHostedService"/>.
///
/// <para>Best-effort: every transport failure inside
/// <see cref="OAuthDiscoveryService.RefreshAllAsync"/> is logged at
/// Debug and the cached doc is retained. The service never throws into
/// the host's shutdown stack.</para>
/// </summary>
public sealed class OAuthDiscoveryRefreshService : BackgroundService
{
    private readonly OAuthDiscoveryService _discovery;
    private readonly OAuthDiscoveryOptions _options;
    private readonly ILogger<OAuthDiscoveryRefreshService> _logger;

    public OAuthDiscoveryRefreshService(
        OAuthDiscoveryService discovery,
        IOptions<OAuthDiscoveryOptions> options,
        ILogger<OAuthDiscoveryRefreshService> logger)
    {
        _discovery = discovery;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Phase K Wave 3 — Bishop. Prefer the seconds-grained
        // RefreshIntervalSeconds knob when set (>0). Falls back to the
        // hours knob otherwise. Min 1s to defend against an operator
        // typo that pins the cadence to zero.
        var interval = _options.RefreshIntervalSeconds > 0
            ? TimeSpan.FromSeconds(Math.Max(1, _options.RefreshIntervalSeconds))
            : TimeSpan.FromHours(Math.Max(1, _options.RefreshIntervalHours));
        // Seed the cache once on boot so the first /health probe never
        // returns Unknown.
        try { await _discovery.RefreshAllAsync(stoppingToken); }
        catch (Exception ex) { _logger.LogDebug(ex, "OAuth discovery boot-refresh failed (cache cold)"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (TaskCanceledException) { break; }
            try { await _discovery.RefreshAllAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogDebug(ex, "OAuth discovery refresh tick failed"); }
        }
    }
}
