namespace Allegro.Admin.Services;

public class OrderSyncWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly AllegroPublishService _allegro;
    private readonly ILogger<OrderSyncWorker> _logger;

    public OrderSyncWorker(AllegroPublishService allegro, ILogger<OrderSyncWorker> logger)
    {
        _allegro = allegro;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_allegro.Settings.IsConnected && !_allegro.IsSyncingOrders)
            {
                try
                {
                    await _allegro.SyncOrdersAsync();
                }
                catch (Exception e)
                {
                    _logger.LogWarning("Order sync failed: {Message}", e.Message);
                }
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
