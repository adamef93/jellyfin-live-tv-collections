using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvChannelGroups.Engine;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvChannelGroups.HostedServices;

/// <summary>
/// Watches for Live TV channels being added or updated (e.g. after a tuner or
/// M3U rescan), and for guide programs when any rule matches on program names,
/// and debounces a single apply pass shortly afterwards, instead of waiting
/// for the next scheduled task run.
/// </summary>
public sealed class ChannelGroupHostedService : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly ChannelGroupEngine _engine;
    private readonly ILogger<ChannelGroupHostedService> _logger;
    private readonly object _timerLock = new();
    private Timer? _debounceTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelGroupHostedService"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="engine">The channel group engine.</param>
    /// <param name="logger">The logger.</param>
    public ChannelGroupHostedService(
        ILibraryManager libraryManager,
        ChannelGroupEngine engine,
        ILogger<ChannelGroupHostedService> logger)
    {
        _libraryManager = libraryManager;
        _engine = engine;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnLiveTvChannelChanged;
        _libraryManager.ItemUpdated += OnLiveTvChannelChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnLiveTvChannelChanged;
        _libraryManager.ItemUpdated -= OnLiveTvChannelChanged;

        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        return Task.CompletedTask;
    }

    private void OnLiveTvChannelChanged(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AutoApplyOnChannelChange)
        {
            return;
        }

        var relevant = e.Item switch
        {
            LiveTvChannel => true,
            LiveTvProgram => config.Groups.Any(r => r.Enabled && r.MatchPrograms),
            _ => false
        };

        if (!relevant)
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Max(1, config.DebounceSeconds));

        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async void OnDebounceElapsed(object? state)
    {
        try
        {
            _logger.LogInformation("Live TV channels or guide changed; re-applying channel groups");
            await _engine.ApplyAsync(progress: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A background timer callback must never throw: log and move on.
            _logger.LogError(ex, "Failed to auto-apply Live TV channel groups");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_timerLock)
        {
            _debounceTimer?.Dispose();
        }
    }
}
