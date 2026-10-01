using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvChannelGroups.Configuration;
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
        var programNames = _engine.GetUpcomingProgramNames(config.Groups);
        var matches = ChannelGroupEngine.PreviewMatches(channels, config.Groups, programNames);

        var result = config.Groups.Select(rule => new GroupPreview(
            rule.Id,
            rule.Name,
            matches.TryGetValue(rule.Id, out var matched)
                ? matched.Select(c => new ChannelSummary(c.Id, c.Name, c.Number)).ToList()
                : new List<ChannelSummary>())).ToList();

        return Ok(result);
    }

    /// <summary>
    /// Dry-runs a single rule, which need not be saved yet, and reports which
    /// channels it matches and why. Used by the config page's per-group
    /// "Preview matches" button so keywords can be tested before saving.
    /// </summary>
    /// <param name="rule">The rule to test.</param>
    /// <returns>The matching channels and any invalid regex patterns.</returns>
    [HttpPost("Preview")]
    [ProducesResponseType(200)]
    public ActionResult<RulePreview> PreviewRule([FromBody] ChannelGroupRule rule)
    {
        // Preview regardless of whether the rule is currently enabled.
        rule.Enabled = true;
        var channels = _engine.GetAllChannels();
        var programNames = _engine.GetUpcomingProgramNames(new[] { rule });

        var matches = new List<ChannelMatch>();
        foreach (var channel in channels)
        {
            var reason = ChannelGroupEngine.FindMatch(channel, rule, programNames);
            if (reason is not null)
            {
                matches.Add(new ChannelMatch(channel.Id, channel.Name, channel.Number, reason.Source, reason.Text, reason.Keyword));
            }
        }

        return Ok(new RulePreview(
            channels.Count,
            matches.OrderBy(m => m.Name, System.StringComparer.OrdinalIgnoreCase).ToList(),
            ChannelGroupEngine.GetInvalidPatterns(rule)));
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
