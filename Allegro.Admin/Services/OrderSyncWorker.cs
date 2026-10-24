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
                var result = await _allegro.RunOrderCycleAsync();
                foreach (var error in result.Errors)
                {
                    _logger.LogWarning("Order cycle: {Message}", error);
                }
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
