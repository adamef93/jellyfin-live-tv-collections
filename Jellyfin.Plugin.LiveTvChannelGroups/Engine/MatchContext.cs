using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Engine;

/// <summary>
/// A guide program on a channel.
/// </summary>
/// <param name="Name">The program's title.</param>
/// <param name="StartUtc">When it starts.</param>
/// <param name="EndUtc">When it ends.</param>
public record GuideProgram(string Name, DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// Everything a matching pass needs besides the channels and rules: the
/// guide, the channels' tuner groups and the league schedules. Built once per
/// pass by <see cref="ChannelGroupEngine.BuildMatchContextAsync"/>.
/// </summary>
public sealed class MatchContext
{
    private readonly Dictionary<string, GameMatcher> _matchers = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="MatchContext"/> class.
    /// </summary>
    /// <param name="nowUtc">The time matching is done for.</param>
    /// <param name="lookaheadEndUtc">How far ahead programs count as matches.</param>
    /// <param name="programs">Guide programs by channel id.</param>
    /// <param name="sourceGroups">Tuner channel group by channel id.</param>
    /// <param name="games">Scheduled games of the leagues rules use.</param>
    public MatchContext(
        DateTime nowUtc,
        DateTime lookaheadEndUtc,
        IReadOnlyDictionary<Guid, GuideProgram[]> programs,
        IReadOnlyDictionary<Guid, string> sourceGroups,
        IReadOnlyList<ScheduledGame> games)
    {
        NowUtc = nowUtc;
        LookaheadEndUtc = lookaheadEndUtc;
        Programs = programs;
        SourceGroups = sourceGroups;
        Games = games;
    }

    /// <summary>Gets the time matching is done for.</summary>
    public DateTime NowUtc { get; }

    /// <summary>Gets how far ahead programs count as matches.</summary>
    public DateTime LookaheadEndUtc { get; }

    /// <summary>Gets guide programs by channel id. Programs outside the lookahead may be included for timing games.</summary>
    public IReadOnlyDictionary<Guid, GuideProgram[]> Programs { get; }

    /// <summary>Gets the tuner channel group by channel id.</summary>
    public IReadOnlyDictionary<Guid, string> SourceGroups { get; }

    /// <summary>Gets the scheduled games of the leagues rules use.</summary>
    public IReadOnlyList<ScheduledGame> Games { get; }

    /// <summary>
    /// Gets a matcher for the games of the given leagues, built once per set.
    /// </summary>
    /// <param name="leagues">The leagues.</param>
    /// <returns>The matcher.</returns>
    public GameMatcher GetMatcher(IReadOnlyList<League> leagues)
    {
        var key = string.Join('|', leagues.Select(l => l.Id).OrderBy(i => i, StringComparer.Ordinal));
        if (!_matchers.TryGetValue(key, out var matcher))
        {
            var ids = leagues.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            matcher = new GameMatcher(Games.Where(g => ids.Contains(g.LeagueId)));
            _matchers[key] = matcher;
        }

        return matcher;
    }
}
