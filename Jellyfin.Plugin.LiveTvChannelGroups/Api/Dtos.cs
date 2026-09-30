using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Api;

/// <summary>
/// A single channel, as returned by the preview endpoint.
/// </summary>
/// <param name="Id">The channel's item id.</param>
/// <param name="Name">The channel's name.</param>
/// <param name="Number">The channel's number, if any.</param>
public record ChannelSummary(Guid Id, string Name, string? Number);

/// <summary>
/// Preview result for a single configured group.
/// </summary>
/// <param name="RuleId">The rule's id.</param>
/// <param name="GroupName">The rule's display name.</param>
/// <param name="MatchedChannels">Channels that currently match this rule.</param>
public record GroupPreview(string RuleId, string GroupName, IReadOnlyList<ChannelSummary> MatchedChannels);
