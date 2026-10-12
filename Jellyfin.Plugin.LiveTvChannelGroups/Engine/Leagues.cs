using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Engine;

/// <summary>
/// A league whose schedule can be matched, and where ESPN publishes it.
/// </summary>
/// <param name="Id">Stable id stored in rules.</param>
/// <param name="Name">Display name.</param>
/// <param name="SportPath">ESPN's sport/league path, e.g. "hockey/nhl".</param>
/// <param name="ScoreboardGroups">ESPN scoreboard <c>groups</c> values to fetch, one request each; empty for one request without it.</param>
public record League(string Id, string Name, string SportPath, IReadOnlyList<string> ScoreboardGroups);

/// <summary>
/// The league presets offered by the League match mode.
/// </summary>
public static class Leagues
{
    /// <summary>
    /// Gets every supported league, in display order.
    /// </summary>
    public static IReadOnlyList<League> All { get; } = new[]
    {
        new League("nfl", "NFL", "football/nfl", Array.Empty<string>()),

        // 80 = FBS, 81 = FCS; without groups ESPN only lists featured games.
        new League("college-football", "College Football", "football/college-football", new[] { "80", "81" }),
        new League("nba", "NBA", "basketball/nba", Array.Empty<string>()),
        new League("wnba", "WNBA", "basketball/wnba", Array.Empty<string>()),

        // 50 = all of Division I.
        new League("mens-college-basketball", "Men's College Basketball", "basketball/mens-college-basketball", new[] { "50" }),
        new League("womens-college-basketball", "Women's College Basketball", "basketball/womens-college-basketball", new[] { "50" }),
        new League("nhl", "NHL", "hockey/nhl", Array.Empty<string>()),
        new League("mens-college-hockey", "Men's College Hockey", "hockey/mens-college-hockey", Array.Empty<string>()),
        new League("womens-college-hockey", "Women's College Hockey", "hockey/womens-college-hockey", Array.Empty<string>()),
        new League("mlb", "MLB", "baseball/mlb", Array.Empty<string>()),
        new League("mls", "MLS", "soccer/usa.1", Array.Empty<string>()),
        new League("premier-league", "Premier League", "soccer/eng.1", Array.Empty<string>())
    };

    /// <summary>
    /// Looks up the known leagues among the given ids, ignoring unknown ones.
    /// </summary>
    /// <param name="ids">League ids, e.g. from a rule.</param>
    /// <returns>The matching leagues.</returns>
    public static IReadOnlyList<League> Resolve(IEnumerable<string>? ids)
    {
        var wanted = (ids ?? Enumerable.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(l => wanted.Contains(l.Id)).ToList();
    }
}
