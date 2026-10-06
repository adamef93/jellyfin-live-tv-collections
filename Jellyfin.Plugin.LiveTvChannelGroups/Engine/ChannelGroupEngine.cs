using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.LiveTvChannelGroups.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Engine;

/// <summary>
/// Why a channel matched a rule.
/// </summary>
/// <param name="Source">Either "name" (the channel name) or "program" (an upcoming guide program).</param>
/// <param name="Text">The channel or program name that matched.</param>
/// <param name="Keyword">The keyword or pattern that matched it.</param>
public record MatchReason(string Source, string Text, string Keyword);

/// <summary>
/// Matches Live TV channels against the configured keyword rules and applies
/// the result as Tags and, optionally, Collections.
/// </summary>
public class ChannelGroupEngine
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<ChannelGroupEngine> _logger;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelGroupEngine"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="logger">The logger.</param>
    public ChannelGroupEngine(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<ChannelGroupEngine> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Fetches every Live TV channel currently known to the library.
    /// </summary>
    /// <returns>All Live TV channel items.</returns>
    public IReadOnlyList<LiveTvChannel> GetAllChannels()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel }
        };

        return _libraryManager.GetItemList(query).OfType<LiveTvChannel>().ToList();
    }

    /// <summary>
    /// Fetches the names of guide programs airing now or starting within the
    /// configured lookahead window, grouped by channel id. Returns an empty
    /// map without querying the guide when no enabled rule matches on programs.
    /// </summary>
    /// <param name="rules">The rules that will be evaluated.</param>
    /// <returns>A map of channel id to the distinct program names on that channel.</returns>
    public IReadOnlyDictionary<Guid, string[]> GetUpcomingProgramNames(IReadOnlyList<ChannelGroupRule> rules)
    {
        if (!rules.Any(r => r.Enabled && r.MatchPrograms))
        {
            return new Dictionary<Guid, string[]>();
        }

        var now = DateTime.UtcNow;
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
            MinEndDate = now,
            MaxStartDate = now.AddDays(Config.GetProgramLookaheadDays())
        };

        return _libraryManager.GetItemList(query)
            .OfType<LiveTvProgram>()
            .Where(p => p.ChannelId != Guid.Empty && !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.ChannelId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    /// <summary>
    /// Tests whether a channel matches a rule's keywords, by channel name and,
    /// if the rule asks for it, by the names of its upcoming programs.
    /// </summary>
    /// <param name="channel">The channel to test.</param>
    /// <param name="rule">The rule to test against.</param>
    /// <param name="programNames">Upcoming program names by channel id, from <see cref="GetUpcomingProgramNames"/>.</param>
    /// <returns><c>true</c> if the channel matches.</returns>
    public static bool IsMatch(LiveTvChannel channel, ChannelGroupRule rule, IReadOnlyDictionary<Guid, string[]> programNames)
    {
        return rule.Enabled && FindMatch(channel, rule, programNames) is not null;
    }

    /// <summary>
    /// Finds why a channel matches a rule: which keyword hit, and whether it hit
    /// the channel name or a program name. Ignores <see cref="ChannelGroupRule.Enabled"/>
    /// so unsaved or disabled rules can be previewed.
    /// </summary>
    /// <param name="channel">The channel to test.</param>
    /// <param name="rule">The rule to test against.</param>
    /// <param name="programNames">Upcoming program names by channel id, from <see cref="GetUpcomingProgramNames"/>.</param>
    /// <returns>The match reason, or <c>null</c> if the channel does not match.</returns>
    public static MatchReason? FindMatch(LiveTvChannel channel, ChannelGroupRule rule, IReadOnlyDictionary<Guid, string[]> programNames)
    {
        if (string.IsNullOrWhiteSpace(rule.Keywords))
        {
            return null;
        }

        var name = channel.Name ?? string.Empty;
        var keyword = FindMatchingKeyword(name, rule);
        if (keyword is not null)
        {
            return new MatchReason("name", name, keyword);
        }

        if (rule.MatchPrograms && programNames.TryGetValue(channel.Id, out var names))
        {
            foreach (var programName in names)
            {
                keyword = FindMatchingKeyword(programName, rule);
                if (keyword is not null)
                {
                    return new MatchReason("program", programName, keyword);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Lists the keywords of a Regex-mode rule that are not valid regular
    /// expressions (and are therefore silently skipped when matching).
    /// </summary>
    /// <param name="rule">The rule to check.</param>
    /// <returns>The invalid patterns; empty for other match modes.</returns>
    public static IReadOnlyList<string> GetInvalidPatterns(ChannelGroupRule rule)
    {
        var invalid = new List<string>();
        if (rule.MatchMode != MatchMode.Regex)
        {
            return invalid;
        }

        foreach (var keyword in SplitKeywords(rule))
        {
            try
            {
                _ = new Regex(keyword);
            }
            catch (ArgumentException)
            {
                invalid.Add(keyword);
            }
        }

        return invalid;
    }

    private static string[] SplitKeywords(ChannelGroupRule rule)
        => (rule.Keywords ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? FindMatchingKeyword(string text, ChannelGroupRule rule)
    {
        var comparison = rule.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regexOptions = rule.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

        foreach (var keyword in SplitKeywords(rule))
        {
            if (keyword.Length == 0)
            {
                continue;
            }

            try
            {
                var matched = rule.MatchMode switch
                {
                    MatchMode.Contains => text.Contains(keyword, comparison),
                    MatchMode.WholeWord => Regex.IsMatch(text, $@"\b{Regex.Escape(keyword)}\b", regexOptions),
                    MatchMode.Regex => Regex.IsMatch(text, keyword, regexOptions),
                    _ => false
                };

                if (matched)
                {
                    return keyword;
                }
            }
            catch (RegexParseException)
            {
                // Invalid pattern typed into the config page: skip this keyword
                // rather than failing the whole apply pass.
            }
        }

        return null;
    }

    /// <summary>
    /// Computes, for each rule, which of the given channels currently match,
    /// without changing anything. Used by the config page's "Preview" button
    /// and the API's preview endpoint.
    /// </summary>
    /// <param name="channels">The channels to test.</param>
    /// <param name="rules">The rules to test.</param>
    /// <param name="programNames">Upcoming program names by channel id, from <see cref="GetUpcomingProgramNames"/>.</param>
    /// <returns>A map of rule id to the list of matching channels.</returns>
    public static IReadOnlyDictionary<string, List<LiveTvChannel>> PreviewMatches(
        IReadOnlyList<LiveTvChannel> channels,
        IReadOnlyList<ChannelGroupRule> rules,
        IReadOnlyDictionary<Guid, string[]> programNames)
    {
        var result = new Dictionary<string, List<LiveTvChannel>>();

        foreach (var rule in rules)
        {
            result[rule.Id] = channels.Where(c => IsMatch(c, rule, programNames)).ToList();
        }

        return result;
    }

    /// <summary>
    /// Runs a full apply pass: tags matching channels, retracts stale
    /// plugin-managed tags, and (if enabled) syncs each group's Collection.
    /// Only one pass runs at a time; concurrent callers wait for the running
    /// pass to finish rather than running a second pass in parallel.
    /// </summary>
    /// <param name="progress">Optional progress reporter, 0-100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what changed.</returns>
    public async Task<ApplyResult> ApplyAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyInternalAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private async Task<ApplyResult> ApplyInternalAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Config;
        var rules = config.Groups.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Name)).ToList();
        var channels = GetAllChannels();
        var programNames = GetUpcomingProgramNames(rules);
        var state = LoadState();
        var newState = new PluginState();
        var result = new ApplyResult
        {
            GroupsProcessed = rules.Count,
            ChannelsScanned = channels.Count
        };

        var groupChannelIds = rules.ToDictionary(r => r.Id, _ => new List<Guid>());
        var total = Math.Max(channels.Count, 1);
        var processed = 0;

        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matchedGroupNames = new List<string>();
            foreach (var rule in rules)
            {
                if (IsMatch(channel, rule, programNames))
                {
                    matchedGroupNames.Add(rule.Name);
                    groupChannelIds[rule.Id].Add(channel.Id);
                }
            }

            var channelKey = channel.Id.ToString("N");
            var previouslyAppliedTags = state.ChannelTags.TryGetValue(channelKey, out var pt)
                ? pt
                : Array.Empty<string>();
            var currentTags = (channel.Tags ?? Array.Empty<string>()).ToList();
            var changed = false;

            foreach (var groupName in matchedGroupNames)
            {
                if (!currentTags.Contains(groupName, StringComparer.OrdinalIgnoreCase))
                {
                    currentTags.Add(groupName);
                    changed = true;
                }
            }

            if (config.RemoveStaleTags)
            {
                foreach (var oldTag in previouslyAppliedTags)
                {
                    var stillMatches = matchedGroupNames.Contains(oldTag, StringComparer.OrdinalIgnoreCase);
                    if (!stillMatches && currentTags.Contains(oldTag, StringComparer.OrdinalIgnoreCase))
                    {
                        currentTags.RemoveAll(t => string.Equals(t, oldTag, StringComparison.OrdinalIgnoreCase));
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                channel.Tags = currentTags.ToArray();
                await _libraryManager
                    .UpdateItemAsync(channel, channel.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                    .ConfigureAwait(false);
                result.ChannelsUpdated++;
            }

            if (matchedGroupNames.Count > 0)
            {
                newState.ChannelTags[channelKey] = matchedGroupNames.ToArray();
            }

            processed++;
            progress?.Report(processed * 100.0 / total);
        }

        SaveState(newState);

        if (config.CreateCollections)
        {
            foreach (var rule in rules)
            {
                var synced = await SyncCollectionAsync(rule, groupChannelIds[rule.Id], cancellationToken)
                    .ConfigureAwait(false);
                if (synced)
                {
                    result.CollectionsSynced++;
                }
            }

            Plugin.Instance!.SaveConfiguration();
        }

        return result;
    }

    private async Task<bool> SyncCollectionAsync(ChannelGroupRule rule, List<Guid> matchedIds, CancellationToken cancellationToken)
    {
        BoxSet? boxSet = null;
        if (rule.CollectionId != Guid.Empty)
        {
            boxSet = _libraryManager.GetItemById(rule.CollectionId) as BoxSet;
        }

        if (boxSet is null)
        {
            if (matchedIds.Count == 0)
            {
                // Nothing matches yet; don't create an empty collection.
                return false;
            }

            _logger.LogInformation("Creating collection for Live TV channel group '{Name}'", rule.Name);

            boxSet = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = rule.Name,
                IsLocked = false,
                ItemIdList = matchedIds.Select(i => i.ToString("N")).ToList()
            }).ConfigureAwait(false);

            rule.CollectionId = boxSet.Id;
            await EnsureSortNameOrderAsync(boxSet, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var orderChanged = await EnsureSortNameOrderAsync(boxSet, cancellationToken).ConfigureAwait(false);

        var existingChildIds = (boxSet.LinkedChildren ?? Array.Empty<LinkedChild>())
            .Select(c => c.ItemId ?? Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();

        var toAdd = matchedIds.Where(id => !existingChildIds.Contains(id)).ToList();
        var toRemove = existingChildIds.Where(id => !matchedIds.Contains(id)).ToList();

        if (toAdd.Count == 0 && toRemove.Count == 0)
        {
            return orderChanged;
        }

        if (toAdd.Count > 0)
        {
            await _collectionManager.AddToCollectionAsync(boxSet.Id, toAdd).ConfigureAwait(false);
        }

        if (toRemove.Count > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(boxSet.Id, toRemove).ConfigureAwait(false);
        }

        return true;
    }

    // Channels have no premiere date, so the default PremiereDate ordering is arbitrary.
    private async Task<bool> EnsureSortNameOrderAsync(BoxSet boxSet, CancellationToken cancellationToken)
    {
        if (string.Equals(boxSet.DisplayOrder, "SortName", StringComparison.Ordinal))
        {
            return false;
        }

        boxSet.DisplayOrder = "SortName";
        await _libraryManager
            .UpdateItemAsync(boxSet, boxSet.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes a group's Collection (BoxSet), if it has one, and removes the
    /// group from the configuration. Tags already applied to channels are
    /// left in place.
    /// </summary>
    /// <param name="groupId">The rule's <see cref="ChannelGroupRule.Id"/>.</param>
    /// <returns><c>true</c> if the group existed and was deleted.</returns>
    public async Task<bool> DeleteGroupAsync(string groupId)
    {
        await _applyLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var rule = Config.Groups.FirstOrDefault(r => string.Equals(r.Id, groupId, StringComparison.Ordinal));
            if (rule is null)
            {
                return false;
            }

            if (rule.CollectionId != Guid.Empty && _libraryManager.GetItemById(rule.CollectionId) is BoxSet boxSet)
            {
                _logger.LogInformation("Deleting collection for Live TV channel group '{Name}'", rule.Name);

                // Jellyfin keeps each collection in a folder of its own; leave that
                // behind and a library scan would bring the collection back.
                _libraryManager.DeleteItem(boxSet, new DeleteOptions { DeleteFileLocation = true });
            }

            Config.Groups.Remove(rule);
            Plugin.Instance!.SaveConfiguration();
            return true;
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private string StatePath => Path.Combine(Plugin.Instance!.DataFolderPath, "state.json");

    private PluginState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new PluginState();
            }

            var json = File.ReadAllText(StatePath);
            return JsonSerializer.Deserialize<PluginState>(json) ?? new PluginState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read Live TV Channel Groups state file, starting fresh");
            return new PluginState();
        }
    }

    private void SaveState(PluginState state)
    {
        try
        {
            Directory.CreateDirectory(Plugin.Instance!.DataFolderPath);
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(StatePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write Live TV Channel Groups state file");
        }
    }
}
