using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TVSorter.Services;

public sealed record ShowNameCandidate(string Name, int? Year)
{
    public string Identity => ShowNameNormalizer.Identity(Name);
}

public static class ShowNameNormalizer
{
    private static readonly Regex TrailingYear = new(@"\s+(?:\((?<year>[0-9]{4})\)|(?<year>[0-9]{4}))$", RegexOptions.CultureInvariant);

    public static ShowNameCandidate Normalize(string cleanedName)
    {
        var name = cleanedName.Trim();
        var match = TrailingYear.Match(name);
        int? year = null;
        if (match.Success)
        {
            year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
            name = name[..match.Index].Trim();
        }

        name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        return new ShowNameCandidate(name, year);
    }

    public static string Identity(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant();
}