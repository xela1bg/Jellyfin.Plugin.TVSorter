using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TVSorter.Models;

namespace Jellyfin.Plugin.TVSorter.Services;

public sealed class TvEpisodeParser
{
    private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "screens", "sample", "samples", "subs", "subtitles", "proof",
        "new upcoming releases by xclusive.txt"
    };

    private static readonly Regex EpisodeRegex = new(
        "(?:^|[\\s._-])(?:s(?<season>\\d{1,2})[\\s._-]*e(?<episode>\\d{1,3})(?:[\\s._-]*e?(?<episode2>\\d{1,3}))?|(?<season2>\\d{1,2})x(?<episode3>\\d{1,3}))(?:[\\s._-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SeasonOnlyRegex = new(
        "(?:^|[\\s._-])(?:s(?<season>\\d{1,2})|season[\\s._-]*(?<season2>\\d{1,2}))(?:[\\s._-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ShowTagRegex = new(
        @"(?:\b(?:2160p|1080p|720p|480p|360p|web-?dl|webrip|web|hdtv|bluray|bdrip|brrip|dvdrip|hddvd|x264|x265|h\.?264|h\.?265|hevc|avc|xvid|aac|ac3|eac3|ddp?5\.1|ddp?2\.0|atmos|dts|proper|repack|internal|extended|unrated|directors\.cut|multi|dual|ita|eng|ger|fre|amzn|nf|dsnp|atvp|hulu|pcok|stan|syfy|fxnow|complete|season|series)\b|\[.*?\]|\{.*?\}|\.(?:mkv|mp4|avi|m4v)$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DotUnderscoreRegex = new("[._]+", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);

    private static readonly TextInfo InvariantTextInfo = CultureInfo.InvariantCulture.TextInfo;

    public static IReadOnlySet<string> VideoExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".m2ts", ".wmv", ".mov"
    };

    public bool ShouldSkipDirectory(string name)
    {
        var normalized = name.Trim();
        return normalized.StartsWith(".", StringComparison.Ordinal) || IgnoredDirectoryNames.Contains(normalized);
    }

    public bool IsVideo(string fileName)
    {
        if (!VideoExtensions.Contains(Path.GetExtension(fileName)))
        {
            return false;
        }

        return !fileName.Contains("sample", StringComparison.OrdinalIgnoreCase)
            && !fileName.Contains("trailer", StringComparison.OrdinalIgnoreCase);
    }

    public EpisodeInfo Parse(string fileName, string parentName, string? grandparentName, string? seriesHint)
    {
        var candidates = new List<string> { fileName, parentName };
        if (grandparentName is not null)
        {
            candidates.Add(grandparentName);
        }

        int? season = null;
        int? episode = null;
        int? secondEpisode = null;
        var show = seriesHint;

        foreach (var candidate in candidates)
        {
            var parsed = ParseSeasonEpisode(candidate);
            if (!parsed.HasValue)
            {
                continue;
            }

            season = parsed.Value.Season;
            episode = parsed.Value.Episode;
            secondEpisode = parsed.Value.SecondEpisode;
            if (string.IsNullOrEmpty(show))
            {
                show = CleanShowName(candidate);
            }

            break;
        }

        var usedFallback = !season.HasValue;
        if (usedFallback)
        {
            show ??= CleanShowName(parentName);
            season = 1;
        }

        show ??= "Unknown";
        return new EpisodeInfo(show, season.GetValueOrDefault(1), episode, secondEpisode, usedFallback);
    }

    public (int Season, int? Episode, int? SecondEpisode)? ParseSeasonEpisode(string text)
    {
        var episodeMatch = EpisodeRegex.Match(text);
        if (episodeMatch.Success)
        {
            var season = ParseGroup(episodeMatch, "season", "season2");
            var episode = ParseGroup(episodeMatch, "episode", "episode3");
            var secondEpisode = ParseOptionalGroup(episodeMatch, "episode2");
            return (season, episode, secondEpisode);
        }

        var seasonMatch = SeasonOnlyRegex.Match(text);
        if (seasonMatch.Success)
        {
            return (ParseGroup(seasonMatch, "season", "season2"), null, null);
        }

        return null;
    }

    public string CleanShowName(string raw)
    {
        var text = raw;
        var marker = EpisodeRegex.Match(text);
        if (!marker.Success)
        {
            marker = SeasonOnlyRegex.Match(text);
        }

        if (marker.Success)
        {
            text = text[..marker.Index];
        }

        text = DotUnderscoreRegex.Replace(text, " ");
        text = ShowTagRegex.Replace(text, " ");
        text = WhitespaceRegex.Replace(text, " ").Trim(' ', '-', '.', '_');

        if ((text.Length > 0 && IsLower(text)) || !text.Contains('.'))
        {
            text = InvariantTextInfo.ToTitleCase(text);
        }

        return string.IsNullOrEmpty(text) ? "Unknown" : text;
    }

    private static int ParseGroup(Match match, string first, string second)
    {
        var value = match.Groups[first].Success ? match.Groups[first].Value : match.Groups[second].Value;
        return int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static int? ParseOptionalGroup(Match match, string group)
    {
        return match.Groups[group].Success
            ? int.Parse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture)
            : null;
    }

    private static bool IsLower(string text)
    {
        var hasCasedCharacter = false;
        foreach (var character in text)
        {
            if (char.IsUpper(character))
            {
                return false;
            }

            hasCasedCharacter |= char.IsLower(character);
        }

        return hasCasedCharacter;
    }
}