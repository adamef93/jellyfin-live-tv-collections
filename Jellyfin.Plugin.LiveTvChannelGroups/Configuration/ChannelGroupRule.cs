using System;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Configuration;

/// <summary>
/// How a rule's keywords are matched against a channel name.
/// </summary>
public enum MatchMode
{
    /// <summary>The channel name contains the keyword as a plain substring.</summary>
    Contains,

    /// <summary>The keyword must match a whole word in the channel name.</summary>
    WholeWord,

    /// <summary>Each keyword is treated as its own .NET regular expression.</summary>
    Regex
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
    /// </summary>
    public string Keywords { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how the keywords are matched against the channel name.
    /// </summary>
    public MatchMode MatchMode { get; set; } = MatchMode.Contains;

    /// <summary>
    /// Gets or sets a value indicating whether matching is case sensitive.
    /// </summary>
    public bool CaseSensitive { get; set; }

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
