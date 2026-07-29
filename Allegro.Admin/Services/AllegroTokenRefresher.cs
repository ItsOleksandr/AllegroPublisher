namespace Allegro.Admin.Services;

/// <summary>
/// Keeps the Allegro OAuth token alive so the account never has to be reconnected by hand.
/// Runs only in the web app (a single long-running process), refreshing the token well before
/// it expires. Because <see cref="AllegroPublishService.KeepAliveAsync"/> only refreshes when the
/// token is close to expiry and reads the latest value from disk first, this stays out of the
/// nightly console's way.
/// </summary>
public class AllegroTokenRefresher : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RefreshWithin = TimeSpan.FromHours(2);

    private readonly AllegroPublishService _allegro;
    private readonly ILogger<AllegroTokenRefresher> _logger;

    public AllegroTokenRefresher(AllegroPublishService allegro, ILogger<AllegroTokenRefresher> logger)
    {
        _allegro = allegro;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _allegro.KeepAliveAsync(RefreshWithin);
            }
            catch (Exception e)
            {
                // Never let a transient failure kill the loop - just try again next tick.
                _logger.LogWarning(e, "Allegro token keep-alive failed; will retry.");
            }

            try
            {
                await Task.Delay(CheckEvery, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}
