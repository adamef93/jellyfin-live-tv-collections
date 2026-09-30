using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Engine;

/// <summary>
/// Bookkeeping the plugin keeps, separately from the main XML config, so it
/// can tell "a tag we added" apart from "a tag the user or another plugin
/// added" when deciding what to retract. Stored as a small JSON file under
/// the plugin's data folder, never shown in the config UI.
/// </summary>
public class PluginState
{
    /// <summary>
    /// Gets or sets the group names last applied by this plugin to each
    /// channel, keyed by the channel's item id in "N" format.
    /// </summary>
    public Dictionary<string, string[]> ChannelTags { get; set; } = new();
}
