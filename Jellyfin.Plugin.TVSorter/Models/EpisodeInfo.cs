namespace Jellyfin.Plugin.TVSorter.Models;

public sealed record EpisodeInfo(
    string ShowName,
    int Season,
    int? Episode,
    int? SecondEpisode,
    bool UsedSeasonFallback)
{
    public bool IsEpisodeMatched => Episode.HasValue;

    public string SeasonDirectory => $"Season {Season:00}";

    public string GetFileName(string originalFileName)
    {
        if (!Episode.HasValue)
        {
            return originalFileName;
        }

        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        var suffix = SecondEpisode.HasValue
            ? $" S{Season:00}E{Episode:00}-E{SecondEpisode:00}"
            : $" S{Season:00}E{Episode:00}";
        return ShowName + suffix + extension;
    }
}