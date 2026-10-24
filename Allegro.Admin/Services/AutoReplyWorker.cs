namespace Allegro.Admin.Services;

public class AutoReplyWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly AllegroPublishService _allegro;
    private readonly ILogger<AutoReplyWorker> _logger;

    public AutoReplyWorker(AllegroPublishService allegro, ILogger<AutoReplyWorker> logger)
    {
        _allegro = allegro;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_allegro.Settings.IsConnected && !_allegro.IsReplying && Allegro.Core.AutoReplyStore.Settings().Enabled)
            {
                try
                {
                    var result = await _allegro.RunAutoReplyAsync();
                    foreach (var error in result.Errors)
                    {
                        _logger.LogWarning("Auto reply: {Message}", error);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning("Auto reply failed: {Message}", e.Message);
                }
            }
            await Task.Delay(Interval, stoppingToken);
        }
    }
}
