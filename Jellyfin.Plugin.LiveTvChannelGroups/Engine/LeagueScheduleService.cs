using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LiveTvChannelGroups.Engine;

/// <summary>
/// Fetches league schedules from ESPN's public scoreboard API and caches
/// them, so the League match mode can tell which titles are real games.
/// </summary>
public class LeagueScheduleService
{
    private const string ScoreboardUrl = "https://site.api.espn.com/apis/site/v2/sports/{0}/scoreboard?dates={1}&limit=500{2}";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LeagueScheduleService> _logger;

    // One entry per league, scoreboard group and day.
    private readonly ConcurrentDictionary<string, (DateTime FetchedUtc, IReadOnlyList<ScheduledGame> Games)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LeagueScheduleService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public LeagueScheduleService(IHttpClientFactory httpClientFactory, ILogger<LeagueScheduleService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the games of the given leagues scheduled between two times.
    /// </summary>
    /// <param name="leagues">The leagues.</param>
    /// <param name="fromUtc">Start of the window.</param>
    /// <param name="toUtc">End of the window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The games, or <c>null</c> if a schedule could not be fetched and none is cached.</returns>
    public async Task<IReadOnlyList<ScheduledGame>?> GetGamesAsync(
        IReadOnlyList<League> leagues,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken)
    {
        var games = new List<ScheduledGame>();

        // ESPN's dates are US Eastern days; a day either side covers the offset.
        for (var day = fromUtc.Date.AddDays(-1); day <= toUtc.Date.AddDays(1); day = day.AddDays(1))
        {
            foreach (var league in leagues)
            {
                var groups = league.ScoreboardGroups.Count > 0 ? league.ScoreboardGroups : new[] { string.Empty };
                foreach (var group in groups)
                {
                    var dayGames = await GetDayAsync(league, group, day, cancellationToken).ConfigureAwait(false);
                    if (dayGames is null)
                    {
                        return null;
                    }

                    games.AddRange(dayGames.Where(g => g.StartUtc >= fromUtc && g.StartUtc <= toUtc));
                }
            }
        }

        return games;
    }

    private async Task<IReadOnlyList<ScheduledGame>?> GetDayAsync(League league, string group, DateTime day, CancellationToken cancellationToken)
    {
        var date = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var key = $"{league.Id}|{group}|{date}";
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedUtc < CacheLifetime)
        {
            return cached.Games;
        }

        var url = string.Format(
            CultureInfo.InvariantCulture,
            ScoreboardUrl,
            league.SportPath,
            date,
            group.Length > 0 ? "&groups=" + group : string.Empty);

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var bytes = await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
            var games = Parse(league, Decompress(bytes));
            _cache[key] = (DateTime.UtcNow, games);
            return games;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidDataException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            if (cached.Games is not null)
            {
                _logger.LogWarning(ex, "Could not refresh the {League} schedule for {Date}; using the cached copy", league.Name, date);
                return cached.Games;
            }

            _logger.LogWarning(ex, "Could not fetch the {League} schedule for {Date}", league.Name, date);
            return null;
        }
    }

    // ESPN sends gzip whether or not it was asked for.
    private static byte[] Decompress(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x1f || bytes[1] != 0x8b)
        {
            return bytes;
        }

        using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static IReadOnlyList<ScheduledGame> Parse(League league, byte[] json)
    {
        var games = new List<ScheduledGame>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("events", out var events))
        {
            return games;
        }

        foreach (var ev in events.EnumerateArray())
        {
            if (!ev.TryGetProperty("date", out var dateEl)
                || !DateTime.TryParse(dateEl.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start)
                || !ev.TryGetProperty("competitions", out var competitions)
                || competitions.GetArrayLength() == 0
                || !competitions[0].TryGetProperty("competitors", out var competitors))
            {
                continue;
            }

            IReadOnlyCollection<string>? home = null, away = null;
            foreach (var competitor in competitors.EnumerateArray())
            {
                if (!competitor.TryGetProperty("team", out var team))
                {
                    continue;
                }

                var names = new[] { "location", "name", "displayName", "shortDisplayName", "abbreviation" }
                    .Select(p => team.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? GameMatcher.Normalize(v.GetString()) : string.Empty)
                    .Where(n => n.Length >= 2)
                    .ToHashSet(StringComparer.Ordinal);

                var side = competitor.TryGetProperty("homeAway", out var ha) ? ha.GetString() : null;
                if (string.Equals(side, "home", StringComparison.OrdinalIgnoreCase))
                {
                    home = names;
                }
                else
                {
                    away = names;
                }
            }

            if (home is { Count: > 0 } && away is { Count: > 0 })
            {
                var label = ev.TryGetProperty("shortName", out var sn) ? sn.GetString() : null;
                games.Add(new ScheduledGame(league.Id, label ?? string.Empty, start, away, home));
            }
        }

        return games;
    }
}
