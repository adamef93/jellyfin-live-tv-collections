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
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpGet("Preview")]
    [ProducesResponseType(200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IReadOnlyList<GroupPreview>>> Preview(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var channels = _engine.GetAllChannels();
        var programNames = _engine.GetUpcomingProgramNames(config.Groups);
        var sourceGroups = await _engine.GetChannelSourceGroupsAsync(channels, config.Groups, cancellationToken).ConfigureAwait(false);
        if (sourceGroups is null)
        {
            return StatusCode(503, "Channel groups could not be read from the Live TV tuners.");
        }

        var matches = ChannelGroupEngine.PreviewMatches(channels, config.Groups, programNames, sourceGroups);

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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matching channels and any invalid regex patterns.</returns>
    [HttpPost("Preview")]
    [ProducesResponseType(200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<RulePreview>> PreviewRule([FromBody] ChannelGroupRule rule, CancellationToken cancellationToken)
    {
        // Preview regardless of whether the rule is currently enabled.
        rule.Enabled = true;
        var channels = _engine.GetAllChannels();
        var programNames = _engine.GetUpcomingProgramNames(new[] { rule });
        var sourceGroups = await _engine.GetChannelSourceGroupsAsync(channels, new[] { rule }, cancellationToken).ConfigureAwait(false);
        if (sourceGroups is null)
        {
            return StatusCode(503, "Channel groups could not be read from the Live TV tuners.");
        }

        var matches = new List<ChannelMatch>();
        foreach (var channel in channels)
        {
            var reason = ChannelGroupEngine.FindMatch(channel, rule, programNames, sourceGroups);
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
    /// Lists the tuner channel groups (an M3U playlist's <c>group-title</c>,
    /// e.g. Dispatcharr's channel groups) of the current Live TV channels, for
    /// the config page's group picker.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each group with its channel count.</returns>
    [HttpGet("SourceGroups")]
    [ProducesResponseType(200)]
    [ProducesResponseType(503)]
    public async Task<ActionResult<IReadOnlyList<SourceGroupSummary>>> SourceGroups(CancellationToken cancellationToken)
    {
        var counts = await _engine.GetSourceGroupCountsAsync(_engine.GetAllChannels(), cancellationToken).ConfigureAwait(false);
        if (counts is null)
        {
            return StatusCode(503, "Channel groups could not be read from the Live TV tuners.");
        }

        return Ok(counts.Select(c => new SourceGroupSummary(c.Key, c.Value)).ToList());
    }

    /// <summary>
    /// Deletes a group and the Collection synced for it. Used by the config
    /// page's "Remove group" dialog when "delete the collection" is ticked.
    /// </summary>
    /// <param name="id">The group's id.</param>
    /// <returns>No content on success, or not found if no such group is saved.</returns>
    [HttpDelete("Groups/{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> DeleteGroup([FromRoute] string id)
    {
        var deleted = await _engine.DeleteGroupAsync(id).ConfigureAwait(false);
        return deleted ? NoContent() : NotFound();
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
