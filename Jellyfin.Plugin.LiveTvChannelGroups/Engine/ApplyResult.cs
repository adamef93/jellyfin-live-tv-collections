namespace Jellyfin.Plugin.LiveTvChannelGroups.Engine;

/// <summary>
/// Summary of one apply pass, returned by the scheduled task, the hosted
/// service's debounced run, and the manual "Apply" API endpoint.
/// </summary>
public class ApplyResult
{
    /// <summary>
    /// Gets or sets the number of enabled rules that were evaluated.
    /// </summary>
    public int GroupsProcessed { get; set; }

    /// <summary>
    /// Gets or sets the total number of Live TV channels scanned.
    /// </summary>
    public int ChannelsScanned { get; set; }

    /// <summary>
    /// Gets or sets the number of channels whose Tags were actually changed
    /// (added to and/or removed from) by this pass.
    /// </summary>
    public int ChannelsUpdated { get; set; }

    /// <summary>
    /// Gets or sets the number of Collections created or updated by this pass.
    /// </summary>
    public int CollectionsSynced { get; set; }
}
