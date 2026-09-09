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

    /// <summary>Keeps the stored token alive; called on a timer by <see cref="AllegroTokenRefresher"/>.</summary>
    public Task<bool> KeepAliveAsync(TimeSpan refreshWithin) => _publisher.KeepAliveAsync(refreshWithin, Log);

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

    /// <summary>Active offers whose product no longer passes the options. Read-only.</summary>
    public Task<List<AllegroPublisher.OrphanOffer>> FindOrphanOffersAsync() =>
        _publisher.FindOrphanOffersAsync(Log);

    public Task<int> EndOffersAsync(IEnumerable<string> offerIds) =>
        _publisher.EndOffersAsync(offerIds, Log);

    public Task<List<BundlePlan.BundleChange>> BuildBundlePlanAsync() =>
        new BundlePlan(_publisher).BuildAsync(Log);

    public async Task<BundleConverter.Result> ConvertBundlesAsync(List<BundlePlan.BundleChange> plan)
    {
        if (IsPublishing)
        {
            throw new InvalidOperationException("A publish is already in progress.");
        }

        IsPublishing = true;
        try
        {
            return await new BundleConverter(_publisher).ConvertAsync(plan, log: Log);
        }
        finally
        {
            IsPublishing = false;
        }
    }

    public Task<List<OfferCreator.Candidate>> PlanNewOffersAsync() =>
        new OfferCreator(_publisher).PlanAsync(Log);

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

    public Task<bool> RefreshToken() => _publisher.TryRefreshAsync(Log);
}
