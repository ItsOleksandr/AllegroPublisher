using System.Collections.Concurrent;
using Allegro.Core;

namespace Allegro.Admin.Services;

/// <summary>
/// Web-UI wrapper around the shared <see cref="AllegroPublisher"/> (which lives in
/// Allegro.Core so the console app can publish too). Adds a live log buffer, an
/// "is publishing" flag, and a background poll for the device flow so the Blazor
/// page stays responsive.
/// </summary>
public class AllegroPublishService
{
    private readonly AllegroPublisher _publisher;
    private readonly ConcurrentQueue<string> _logs = new();
    private const int MaxLogLines = 300;

    public AllegroPublishService(IHttpClientFactory httpClientFactory)
    {
        _publisher = new AllegroPublisher(httpClientFactory.CreateClient());
    }

    public AllegroSettings Settings => _publisher.Settings;
    public bool IsPublishing { get; private set; }
    public IReadOnlyCollection<string> Logs => _logs;

    public void SaveSettings() => _publisher.SaveSettings();

    public void ClearLogs() => _logs.Clear();

    private void Log(string message)
    {
        _logs.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (_logs.Count > MaxLogLines && _logs.TryDequeue(out _))
        {
        }
    }

    public record DeviceFlowInfo(string UserCode, string VerificationUri);

    /// <summary>Starts the device flow and polls for the token in the background.</summary>
    public async Task<DeviceFlowInfo> ConnectAsync()
    {
        var auth = await _publisher.StartDeviceFlowAsync(Log);
        _ = Task.Run(() => _publisher.PollForTokenAsync(auth, Log));
        return new DeviceFlowInfo(auth.UserCode, auth.VerificationUri);
    }

    public async Task<int> PublishAsync()
    {
        if (IsPublishing)
        {
            throw new InvalidOperationException("A publish is already in progress.");
        }

        IsPublishing = true;
        try
        {
            return await _publisher.PublishAsync(Log);
        }
        finally
        {
            IsPublishing = false;
        }
    }
}
