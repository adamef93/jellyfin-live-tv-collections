using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvChannelGroups.Engine;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.LiveTvChannelGroups.ScheduledTasks;

/// <summary>
/// Dashboard-visible scheduled task that re-scans every Live TV channel and
/// (re)applies the configured keyword groups. Runs every 6 hours by default;
/// can also be run on demand from Dashboard &gt; Scheduled Tasks, or from this
/// plugin's own "Apply" API endpoint.
/// </summary>
public class ApplyChannelGroupsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ChannelGroupEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApplyChannelGroupsTask"/> class.
    /// </summary>
    /// <param name="engine">The channel group engine.</param>
    public ApplyChannelGroupsTask(ChannelGroupEngine engine)
    {
        _engine = engine;
    }

    /// <inheritdoc />
    public string Name => "Apply Live TV Channel Groups";

    /// <inheritdoc />
    public string Key => "ApplyLiveTvChannelGroups";

    /// <inheritdoc />
    public string Description => "Scans Live TV channels and applies keyword-based group tags and collections.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _engine.ApplyAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }
}
