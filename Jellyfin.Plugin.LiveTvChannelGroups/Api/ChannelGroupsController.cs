using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvChannelGroups.Engine;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Api;

/// <summary>
/// Admin-only API for previewing keyword matches and triggering a manual
/// apply pass, independent of the scheduled task. Useful for testing keyword
/// lists while editing the plugin's config page, and as an integration point
/// for anything (a script, or a future client) that wants the current group
/// membership without waiting for the next scheduled run.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("LiveTvChannelGroups")]
[Produces("application/json")]
public class ChannelGroupsController : ControllerBase
{
    private readonly ChannelGroupEngine _engine;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelGroupsController"/> class.
    /// </summary>
    /// <param name="engine">The channel group engine.</param>
    public ChannelGroupsController(ChannelGroupEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Shows, for every configured group, which channels currently match its
    /// keywords. Nothing is changed on the server; this is a dry run.
    /// </summary>
    /// <returns>One <see cref="GroupPreview"/> per configured group.</returns>
    [HttpGet("Preview")]
    [ProducesResponseType(200)]
    public ActionResult<IReadOnlyList<GroupPreview>> Preview()
    {
        var config = Plugin.Instance!.Configuration;
        var channels = _engine.GetAllChannels();
        var matches = ChannelGroupEngine.PreviewMatches(channels, config.Groups);

        var result = config.Groups.Select(rule => new GroupPreview(
            rule.Id,
            rule.Name,
            matches.TryGetValue(rule.Id, out var matched)
                ? matched.Select(c => new ChannelSummary(c.Id, c.Name, c.Number)).ToList()
                : new List<ChannelSummary>())).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Runs a full apply pass immediately (equivalent to running the "Apply
    /// Live TV Channel Groups" scheduled task from Dashboard &gt; Scheduled
    /// Tasks, but synchronous and without waiting for the task queue).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what changed.</returns>
    [HttpPost("Apply")]
    [ProducesResponseType(200)]
    public async Task<ActionResult<ApplyResult>> Apply(CancellationToken cancellationToken)
    {
        var result = await _engine.ApplyAsync(progress: null, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}
