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
/// <param name="Source">"name" (the channel name), "program" (an upcoming guide program) or "group" (the channel's tuner group, for rules without keywords).</param>
/// <param name="Text">The channel name, program name or group that matched.</param>
/// <param name="Keyword">The keyword, pattern or group that matched it.</param>
public record MatchReason(string Source, string Text, string Keyword);

/// <summary>
/// Matches Live TV channels against the configured keyword rules and applies
/// the result as Tags and, optionally, Collections.
/// </summary>
public class ChannelGroupEngine
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ITunerHostManager _tunerHostManager;
    private readonly LeagueScheduleService _leagueSchedules;
    private readonly ILogger<ChannelGroupEngine> _logger;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelGroupEngine"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="collectionManager">The collection manager.</param>
    /// <param name="tunerHostManager">The tuner host manager.</param>
    /// <param name="leagueSchedules">The league schedule service.</param>
    /// <param name="logger">The logger.</param>
    public ChannelGroupEngine(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ITunerHostManager tunerHostManager,
        LeagueScheduleService leagueSchedules,
        ILogger<ChannelGroupEngine> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _tunerHostManager = tunerHostManager;
        _leagueSchedules = leagueSchedules;
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
    /// Gathers what the given rules need to be matched: the guide programs
    /// airing now or within the lookahead window, the channels' tuner groups,
    /// and the schedules of the leagues they use. Each is only fetched when an
    /// enabled rule needs it.
    /// </summary>
    /// <param name="channels">The channels that will be matched.</param>
    /// <param name="rules">The rules that will be evaluated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The context, or <c>null</c> if the tuners' groups or a league schedule could not be read.</returns>
    public async Task<MatchContext?> BuildMatchContextAsync(
        IReadOnlyList<LiveTvChannel> channels,
        IReadOnlyList<ChannelGroupRule> rules,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var lookaheadEnd = now.AddDays(Config.GetProgramLookaheadDays());
        var enabled = rules.Where(r => r.Enabled).ToList();

        // League rules time games against the guide and need a schedule, even
        // with no lookahead: look at least a day ahead and half a day back.
        var leagues = enabled.SelectMany(GetLeagues).DistinctBy(l => l.Id).ToList();
        var guideFrom = leagues.Count > 0 ? now.AddHours(-12) : now;
        var guideTo = leagues.Count > 0 ? Max(lookaheadEnd, now.AddDays(1)) : lookaheadEnd;

        var needPrograms = leagues.Count > 0
            || enabled.Any(r => r.MatchPrograms && UsesKeywords(r) && GetKeywordMatchMode(r) != MatchMode.League);
        var programs = needPrograms
            ? GetGuidePrograms(guideFrom, guideTo)
            : new Dictionary<Guid, GuideProgram[]>();

        IReadOnlyDictionary<Guid, string>? sourceGroups = new Dictionary<Guid, string>();
        if (enabled.Any(r => r.MatchMode == MatchMode.ChannelGroups))
        {
            sourceGroups = await LookupChannelSourceGroupsAsync(channels, cancellationToken).ConfigureAwait(false);
            if (sourceGroups is null)
            {
                return null;
            }
        }

        IReadOnlyList<ScheduledGame>? games = Array.Empty<ScheduledGame>();
        if (leagues.Count > 0)
        {
            games = await _leagueSchedules.GetGamesAsync(leagues, now.AddHours(-12), guideTo, cancellationToken).ConfigureAwait(false);
            if (games is null)
            {
                return null;
            }
        }

        return new MatchContext(now, lookaheadEnd, programs, sourceGroups, games);
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private Dictionary<Guid, GuideProgram[]> GetGuidePrograms(DateTime fromUtc, DateTime toUtc)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.LiveTvProgram },
            MinEndDate = fromUtc,
            MaxStartDate = toUtc
        };

        return _libraryManager.GetItemList(query)
            .OfType<LiveTvProgram>()
            .Where(p => p.ChannelId != Guid.Empty && !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.ChannelId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => new GuideProgram(p.Name, p.StartDate, p.EndDate ?? p.StartDate))
                    .Distinct()
                    .ToArray());
    }

    // The leagues a rule matches games of, if it matches by league at all.
    private static IReadOnlyList<League> GetLeagues(ChannelGroupRule rule)
        => UsesKeywords(rule) && GetKeywordMatchMode(rule) == MatchMode.League
            ? Engine.Leagues.Resolve(rule.Leagues)
            : Array.Empty<League>();

    /// <summary>
    /// Gets whether a rule matches keywords: always for keyword match modes,
    /// and for channel-group rules only when they filter a group by keyword.
    /// </summary>
    /// <param name="rule">The rule to check.</param>
    /// <returns><c>true</c> if the rule's keywords are used.</returns>
    public static bool UsesKeywords(ChannelGroupRule rule)
        => rule.MatchMode != MatchMode.ChannelGroups || CleanGroups(rule.GetFilteredChannelGroups()).Count > 0;

    // The mode a rule's keywords are matched with.
    private static MatchMode GetKeywordMatchMode(ChannelGroupRule rule)
        => rule.MatchMode == MatchMode.ChannelGroups ? rule.KeywordMatchMode : rule.MatchMode;

    /// <summary>
    /// Lists the tuner channel groups of the given channels, with how many
    /// channels are in each. Used by the config page's group picker.
    /// </summary>
    /// <param name="channels">The channels to count.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Group names and channel counts, or <c>null</c> if a tuner could not be read.</returns>
    public async Task<IReadOnlyList<KeyValuePair<string, int>>?> GetSourceGroupCountsAsync(
        IReadOnlyList<LiveTvChannel> channels,
        CancellationToken cancellationToken)
    {
        var groups = await LookupChannelSourceGroupsAsync(channels, cancellationToken).ConfigureAwait(false);
        return groups?.Values
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Jellyfin reads a channel's group (an M3U playlist's group-title) when it
    // parses the playlist but does not store it on the channel, so ask the
    // tuners for their channel lists, which they cache from the last guide
    // refresh. Returns null if a tuner could not be read.
    private async Task<IReadOnlyDictionary<Guid, string>?> LookupChannelSourceGroupsAsync(
        IReadOnlyList<LiveTvChannel> channels,
        CancellationToken cancellationToken)
    {
        // A channel's ExternalId is the tuner's ChannelInfo.Id.
        var groupByExternalId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var tunerHost in _tunerHostManager.TunerHosts)
            {
                var tunerChannels = await tunerHost.GetChannels(true, cancellationToken).ConfigureAwait(false);
                foreach (var info in tunerChannels)
                {
                    if (!string.IsNullOrEmpty(info.Id) && !string.IsNullOrWhiteSpace(info.ChannelGroup))
                    {
                        groupByExternalId[info.Id] = info.ChannelGroup.Trim();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read channel groups from the Live TV tuners");
            return null;
        }

        var result = new Dictionary<Guid, string>();
        foreach (var channel in channels)
        {
            if (!string.IsNullOrEmpty(channel.ExternalId)
                && groupByExternalId.TryGetValue(channel.ExternalId, out var group))
            {
                result[channel.Id] = group;
            }
        }

        return result;
    }

    private static HashSet<string> CleanGroups(IEnumerable<string>? groups)
        => (groups ?? Enumerable.Empty<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tests whether a channel matches a rule: by its tuner channel group for
    /// channel-group rules, by scheduled games for league rules, and by
    /// keywords against the channel name and, if the rule asks for it, the
    /// names of its upcoming programs.
    /// </summary>
    /// <param name="channel">The channel to test.</param>
    /// <param name="rule">The rule to test against.</param>
    /// <param name="context">The guide, groups and schedules, from <see cref="BuildMatchContextAsync"/>.</param>
    /// <returns><c>true</c> if the channel matches.</returns>
    public static bool IsMatch(LiveTvChannel channel, ChannelGroupRule rule, MatchContext context)
    {
        return rule.Enabled && FindMatch(channel, rule, context) is not null;
    }

    /// <summary>
    /// Finds why a channel matches a rule: which keyword, group or game hit,
    /// and whether it hit the channel name or a program name. Ignores
    /// <see cref="ChannelGroupRule.Enabled"/> so unsaved or disabled rules can
    /// be previewed.
    /// </summary>
    /// <param name="channel">The channel to test.</param>
    /// <param name="rule">The rule to test against.</param>
    /// <param name="context">The guide, groups and schedules, from <see cref="BuildMatchContextAsync"/>.</param>
    /// <returns>The match reason, or <c>null</c> if the channel does not match.</returns>
    public static MatchReason? FindMatch(LiveTvChannel channel, ChannelGroupRule rule, MatchContext context)
    {
        if (rule.MatchMode == MatchMode.ChannelGroups)
        {
            if (!context.SourceGroups.TryGetValue(channel.Id, out var sourceGroup)
                || !CleanGroups(rule.ChannelGroups).Contains(sourceGroup))
            {
                return null;
            }

            if (!CleanGroups(rule.GetFilteredChannelGroups()).Contains(sourceGroup))
            {
                return new MatchReason("group", sourceGroup, sourceGroup);
            }
        }

        var programs = context.Programs.TryGetValue(channel.Id, out var p) ? p : Array.Empty<GuideProgram>();
        if (GetKeywordMatchMode(rule) == MatchMode.League)
        {
            return FindGameMatch(channel, rule, programs, context);
        }

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

        if (rule.MatchPrograms)
        {
            foreach (var programName in UpcomingPrograms(programs, context).Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase))
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

    // Programs airing now or starting within the lookahead window.
    private static IEnumerable<GuideProgram> UpcomingPrograms(GuideProgram[] programs, MatchContext context)
        => programs.Where(x => x.EndUtc >= context.NowUtc && x.StartUtc <= context.LookaheadEndUtc);

    // A channel matches a league when its name or (with program matching) an
    // upcoming program names a scheduled game. Two schools can meet in several
    // sports the same weekend under identical titles, so a title only counts
    // when the guide has it starting near the game's scheduled start.
    private static MatchReason? FindGameMatch(LiveTvChannel channel, ChannelGroupRule rule, GuideProgram[] programs, MatchContext context)
    {
        var leagues = Engine.Leagues.Resolve(rule.Leagues);
        if (leagues.Count == 0)
        {
            return null;
        }

        var matcher = context.GetMatcher(leagues);
        var name = channel.Name ?? string.Empty;
        foreach (var game in matcher.FindGames(name))
        {
            // Event channels (e.g. ESPN+) carry the event as their program, so
            // time it by that. With no such program, settle for the game being
            // within the window.
            var timed = programs.Where(x => matcher.FindGames(x.Name).Contains(game)).ToList();
            var onTime = timed.Count > 0
                ? timed.Any(x => GameMatcher.StartsNear(game, x.StartUtc))
                : game.StartUtc >= context.NowUtc.AddHours(-6) && game.StartUtc <= Max(context.LookaheadEndUtc, context.NowUtc.AddDays(1));
            if (onTime)
            {
                return new MatchReason("game", name, game.Label);
            }
        }

        if (rule.MatchPrograms)
        {
            foreach (var program in UpcomingPrograms(programs, context))
            {
                var game = matcher.FindGames(program.Name).FirstOrDefault(g => GameMatcher.StartsNear(g, program.StartUtc));
                if (game is not null)
                {
                    return new MatchReason("game", program.Name, game.Label);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Lists the keywords of a rule matching by regular expression that are
    /// not valid regular expressions (and are therefore silently skipped when
    /// matching).
    /// </summary>
    /// <param name="rule">The rule to check.</param>
    /// <returns>The invalid patterns; empty for other match modes.</returns>
    public static IReadOnlyList<string> GetInvalidPatterns(ChannelGroupRule rule)
    {
        var invalid = new List<string>();
        if (!UsesKeywords(rule) || GetKeywordMatchMode(rule) != MatchMode.Regex)
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
                var matched = GetKeywordMatchMode(rule) switch
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
    /// <param name="context">The guide, groups and schedules, from <see cref="BuildMatchContextAsync"/>.</param>
    /// <returns>A map of rule id to the list of matching channels.</returns>
    public static IReadOnlyDictionary<string, List<LiveTvChannel>> PreviewMatches(
        IReadOnlyList<LiveTvChannel> channels,
        IReadOnlyList<ChannelGroupRule> rules,
        MatchContext context)
    {
        var result = new Dictionary<string, List<LiveTvChannel>>();

        foreach (var rule in rules)
        {
            result[rule.Id] = channels.Where(c => IsMatch(c, rule, context)).ToList();
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
        var result = new ApplyResult
        {
            GroupsProcessed = rules.Count,
            ChannelsScanned = channels.Count
        };

        var context = await BuildMatchContextAsync(channels, rules, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            // Without groups or schedules, those rules would match nothing and
            // this pass would strip their tags and empty their collections.
            // Leave everything as it is until they can be read again.
            _logger.LogWarning("Skipping this apply pass: channel groups or league schedules are unavailable");
            return result;
        }

        var state = LoadState();
        var newState = new PluginState();

        var groupChannelIds = rules.ToDictionary(r => r.Id, _ => new List<Guid>());
        var total = Math.Max(channels.Count, 1);
        var processed = 0;

        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matchedGroupNames = new List<string>();
            foreach (var rule in rules)
            {
                if (IsMatch(channel, rule, context))
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

            // Jellyfin builds a channel's default sort name from its channel number
            // plus its name; force name-only so collections sort alphabetically.
            if (config.CreateCollections
                && matchedGroupNames.Count > 0
                && !string.Equals(channel.ForcedSortName, channel.Name, StringComparison.Ordinal))
            {
                channel.ForcedSortName = channel.Name;
                changed = true;
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
    // Channel sort names are forced to the plain name in ApplyInternalAsync.
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
