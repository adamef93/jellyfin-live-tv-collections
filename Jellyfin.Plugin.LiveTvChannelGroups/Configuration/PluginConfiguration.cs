using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Configuration;

/// <summary>
/// Plugin configuration, edited from the dashboard config page and persisted
/// by the Jellyfin host as XML.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        Groups = new List<ChannelGroupRule>();
        CreateCollections = true;
        RemoveStaleTags = true;
        AutoApplyOnChannelChange = true;
        DebounceSeconds = 15;
        ProgramLookaheadHours = 168;
    }

    /// <summary>
    /// Gets or sets the configured groups. Must keep a public setter: the host
    /// persists plugin config with an XML serializer, which needs a setter to
    /// populate the list.
    /// </summary>
    public List<ChannelGroupRule> Groups { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether each group should also be kept
    /// in sync as a Jellyfin Collection (BoxSet), in addition to being applied
    /// as a Tag. Collections are the part that is actually browsable, as a
    /// named group, from third-party clients such as Moonfin, since those
    /// clients read Collections generically but do not read arbitrary Live TV
    /// tags for their Live TV guide/filter UI.
    /// </summary>
    public bool CreateCollections { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether tags previously added by this
    /// plugin to a channel should be removed once that channel no longer
    /// matches the rule that added them (e.g. after you edit a rule's
    /// keywords). Tags not added by this plugin are never touched either way.
    /// </summary>
    public bool RemoveStaleTags { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether groups are re-applied
    /// automatically whenever a Live TV channel is added or updated (for
    /// example after a tuner/M3U rescan), in addition to the scheduled task.
    /// </summary>
    public bool AutoApplyOnChannelChange { get; set; }

    /// <summary>
    /// Gets or sets how long to wait, after the last channel add/update
    /// event, before running the apply pass. A tuner rescan can raise
    /// hundreds of events in a burst; this batches them into a single pass
    /// instead of re-scanning the whole channel list per event.
    /// </summary>
    public int DebounceSeconds { get; set; }

    /// <summary>
    /// Gets or sets how far ahead, in hours, to look in the guide when a rule
    /// matches on program names. Programs currently airing always count; a
    /// program further out than this is ignored until it comes into range.
    /// </summary>
    public int ProgramLookaheadHours { get; set; }
}
