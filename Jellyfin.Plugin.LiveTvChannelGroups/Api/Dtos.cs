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

/// <summary>
/// A channel that matched a previewed rule, and why.
/// </summary>
/// <param name="Id">The channel's item id.</param>
/// <param name="Name">The channel's name.</param>
/// <param name="Number">The channel's number, if any.</param>
/// <param name="MatchedOn">"name" or "program".</param>
/// <param name="MatchedText">The channel or program name the keyword matched.</param>
/// <param name="Keyword">The keyword or pattern that matched.</param>
public record ChannelMatch(Guid Id, string Name, string? Number, string MatchedOn, string MatchedText, string Keyword);

/// <summary>
/// Result of previewing a single, possibly unsaved, rule.
/// </summary>
/// <param name="ChannelsScanned">How many channels were tested.</param>
/// <param name="Matches">The channels that matched, with reasons.</param>
/// <param name="InvalidPatterns">Regex patterns that failed to parse and were skipped.</param>
public record RulePreview(int ChannelsScanned, IReadOnlyList<ChannelMatch> Matches, IReadOnlyList<string> InvalidPatterns);
