using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Configuration;

/// <summary>
/// How a rule picks its channels: by keywords matched against the channel
/// name, or by the channel's tuner channel group.
/// </summary>
public enum MatchMode
{
    /// <summary>The channel name contains the keyword as a plain substring.</summary>
    Contains,

    /// <summary>The keyword must match a whole word in the channel name.</summary>
    WholeWord,

    /// <summary>Each keyword is treated as its own .NET regular expression.</summary>
    Regex,

    /// <summary>
    /// Channels in the selected tuner channel groups join, optionally narrowed
    /// down by keywords matched with <see cref="ChannelGroupRule.KeywordMatchMode"/>.
    /// </summary>
    ChannelGroups
}

/// <summary>
/// A single named group and the keyword rule used to populate it.
/// </summary>
public class ChannelGroupRule
{
    /// <summary>
    /// Gets or sets a stable identifier for this rule (generated once, kept forever
    /// so the plugin can remember which collection belongs to which rule even if
    /// the rule is renamed).
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Gets or sets the display name of the group (e.g. "Hockey", "NFL").
    /// This is also the tag value applied to matching channels and, if
    /// collection sync is enabled, the collection's name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a comma-separated list of keywords (or, in Regex mode,
    /// comma-separated regular expressions). Example: "hockey, nhl, sportsnet".
    /// In <see cref="Configuration.MatchMode.ChannelGroups"/> rules, only used
    /// for the <see cref="FilteredChannelGroups"/>.
    /// </summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how the rule matches: by keywords against the channel name,
    /// or by tuner channel group.
    /// </summary>
    public MatchMode MatchMode { get; set; } = MatchMode.Contains;

    /// <summary>
    /// Gets or sets a value indicating whether matching is case sensitive.
    /// </summary>
    public bool CaseSensitive { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the keywords are also matched
    /// against the names of programs in the guide, so a channel joins the group
    /// while it is airing (or about to air) a matching program, e.g. CBS
    /// during an NFL game. How far ahead to look is set by
    /// <see cref="PluginConfiguration.ProgramLookaheadDays"/>.
    /// </summary>
    public bool MatchPrograms { get; set; }

    /// <summary>
    /// Gets or sets the tuner channel groups (an M3U playlist's
    /// <c>group-title</c>, e.g. a Dispatcharr channel group) whose channels
    /// join this group when <see cref="MatchMode"/> is
    /// <see cref="Configuration.MatchMode.ChannelGroups"/>. Must keep a public
    /// setter for the host's XML serializer.
    /// </summary>
    public List<string> ChannelGroups { get; set; } = new();

    /// <summary>
    /// Gets or sets the subset of <see cref="ChannelGroups"/> whose channels
    /// must also match <see cref="Keywords"/>; channels from the other selected
    /// groups join without keyword matching.
    /// </summary>
    public List<string> FilteredChannelGroups { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether every selected channel group is
    /// filtered by keyword. Only present in configs saved before filtering was
    /// set per group; the settings page converts it to
    /// <see cref="FilteredChannelGroups"/> the next time it saves.
    /// </summary>
    public bool FilterByKeywords { get; set; }

    /// <summary>
    /// Gets the channel groups whose channels must also match the keywords,
    /// treating a legacy <see cref="FilterByKeywords"/> as every selected group.
    /// </summary>
    /// <returns>The filtered channel groups.</returns>
    public IReadOnlyList<string> GetFilteredChannelGroups() =>
        FilteredChannelGroups.Count == 0 && FilterByKeywords ? ChannelGroups : FilteredChannelGroups;

    /// <summary>
    /// Gets or sets how keywords are matched in the
    /// <see cref="FilteredChannelGroups"/> of a channel-group rule.
    /// </summary>
    public MatchMode KeywordMatchMode { get; set; } = MatchMode.Contains;

    /// <summary>
    /// Gets or sets a value indicating whether this rule is active.
    /// Disabled rules are skipped entirely and any tags/collection membership
    /// they previously created are left untouched (they are not retracted).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the id of the Collection (BoxSet) this rule is synced to,
    /// once one has been created. Managed by the plugin; leave this as
    /// <see cref="Guid.Empty"/> when creating a new rule by hand.
    /// </summary>
    public Guid CollectionId { get; set; }
}
