using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Engine;

/// <summary>
/// A game on a league's schedule.
/// </summary>
/// <param name="LeagueId">The league's id.</param>
/// <param name="Label">Short description, e.g. "UGA @ ALA".</param>
/// <param name="StartUtc">Scheduled start.</param>
/// <param name="AwayNames">The away team's normalized names (city, nickname, full name, abbreviation).</param>
/// <param name="HomeNames">The home team's normalized names.</param>
public record ScheduledGame(
    string LeagueId,
    string Label,
    DateTime StartUtc,
    IReadOnlyCollection<string> AwayNames,
    IReadOnlyCollection<string> HomeNames);

/// <summary>
/// Recognises scheduled games in channel and program titles.
/// </summary>
/// <remarks>
/// A title names a game when it reads "{team} vs/at/@ {team}" with the two
/// teams of one scheduled game, in either order. The text before the first
/// team ("College Football : ", "NHL.TV: ") and after the second
/// ("(Matchday #8)", "ᴸᶦᵛᵉ") is ignored, as are rankings ("#14 Missouri").
/// Two schools can meet in several sports on the same weekend, so callers
/// check the title's guide time against the game's start as well.
/// </remarks>
public sealed class GameMatcher
{
    /// <summary>
    /// How far a guide program's start may be from the game's scheduled start.
    /// </summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromMinutes(90);

    private static readonly Regex Superscripts = new(@"[ʰ-˿ᴀ-ᶿ]+", RegexOptions.Compiled);
    private static readonly Regex Rankings = new(@"#\d+\s*", RegexOptions.Compiled);
    private static readonly Regex Punctuation = new(@"[^\p{L}\p{N}&()'@ ]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Separator = new(@"\s(?:vs|v|at|@)\s", RegexOptions.Compiled);

    // Normalized team name -> the games it appears in, and as which side.
    private readonly Dictionary<string, List<(ScheduledGame Game, bool Home)>> _byName = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="GameMatcher"/> class.
    /// </summary>
    /// <param name="games">The games to recognise.</param>
    public GameMatcher(IEnumerable<ScheduledGame> games)
    {
        foreach (var game in games)
        {
            Index(game, game.AwayNames, false);
            Index(game, game.HomeNames, true);
        }
    }

    /// <summary>
    /// Normalizes a title or team name for comparison: lower case, no
    /// accents, rankings, superscript tags (ᴸᶦᵛᵉ) or punctuation other than
    /// <c>&amp;</c>, <c>@</c>, parentheses and apostrophes; dots are dropped so
    /// "St. Louis" reads "st louis".
    /// </summary>
    /// <param name="text">The text to normalize.</param>
    /// <returns>The normalized text.</returns>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && c != '.')
            {
                sb.Append(c);
            }
        }

        var s = Superscripts.Replace(sb.ToString(), " ").ToLowerInvariant();
        s = Rankings.Replace(s, " ");
        s = Punctuation.Replace(s, " ");
        return Spaces.Replace(s, " ").Trim();
    }

    /// <summary>
    /// Finds the scheduled games a title names, ignoring time.
    /// </summary>
    /// <param name="title">A channel or program title.</param>
    /// <returns>The games named, possibly several (e.g. two sports, same schools).</returns>
    public IReadOnlyList<ScheduledGame> FindGames(string? title)
    {
        var normalized = Normalize(title);
        var games = new List<ScheduledGame>();

        // "at" can also appear in the text before the teams, so try each
        // separator in turn.
        foreach (Match separator in Separator.Matches(normalized))
        {
            var left = normalized[..separator.Index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var right = normalized[(separator.Index + separator.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // The first team ends the text before the separator; the second
            // starts the text after it.
            var leftTeams = Lookup(Enumerable.Range(0, left.Length).Select(i => string.Join(' ', left[i..])));
            if (leftTeams.Count == 0)
            {
                continue;
            }

            var rightTeams = Lookup(Enumerable.Range(1, right.Length).Select(n => string.Join(' ', right[..n])));
            games.AddRange(leftTeams
                .Where(l => rightTeams.Any(r => ReferenceEquals(r.Game, l.Game) && r.Home != l.Home))
                .Select(l => l.Game));
        }

        return games.Distinct().ToList();
    }

    /// <summary>
    /// Finds which of a league's shows (see <see cref="League.Shows"/>) a
    /// title names, as whole words.
    /// </summary>
    /// <param name="title">A channel or program title.</param>
    /// <param name="shows">The show titles.</param>
    /// <returns>The show named, or <c>null</c>.</returns>
    public static string? FindShow(string? title, IEnumerable<string> shows)
    {
        var padded = " " + Normalize(title) + " ";
        return shows.FirstOrDefault(s => padded.Contains(" " + Normalize(s) + " ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Gets whether a guide program starting at the given time could be the game.
    /// </summary>
    /// <param name="game">The game.</param>
    /// <param name="programStartUtc">The program's start.</param>
    /// <returns><c>true</c> if the start times are close enough.</returns>
    public static bool StartsNear(ScheduledGame game, DateTime programStartUtc)
        => (programStartUtc - game.StartUtc).Duration() <= StartTolerance;

    private void Index(ScheduledGame game, IEnumerable<string> names, bool home)
    {
        foreach (var name in names)
        {
            if (!_byName.TryGetValue(name, out var list))
            {
                list = new List<(ScheduledGame, bool)>();
                _byName[name] = list;
            }

            list.Add((game, home));
        }
    }

    private List<(ScheduledGame Game, bool Home)> Lookup(IEnumerable<string> candidates)
    {
        var found = new List<(ScheduledGame, bool)>();
        foreach (var candidate in candidates)
        {
            if (_byName.TryGetValue(candidate, out var list))
            {
                found.AddRange(list);
            }
        }

        return found;
    }
}
