using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.TVSorter.Configuration;

namespace Jellyfin.Plugin.TVSorter.Services;

public sealed class ShowNameCanonicalizer : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private readonly Func<PluginConfiguration?> _configuration;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, CachedShow>? _cache;
    private int _lookupOffset;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ShowNameCanonicalizer(HttpClient httpClient, ManagedLinkManifestStore manifestStore, Func<PluginConfiguration?> configuration)
    {
        _httpClient = httpClient;
        _cachePath = Path.Combine(manifestStore.StorageDirectory, "show-names.json");
        _configuration = configuration;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(IEnumerable<ShowNameCandidate> candidates, CancellationToken cancellationToken)
    {
        var groups = candidates.GroupBy(candidate => candidate.Identity, StringComparer.Ordinal).ToArray();
        var resolved = groups.ToDictionary(group => group.Key, group => group.First().Name, StringComparer.Ordinal);
        if (_configuration()?.UseTvmaze == false)
        {
            return resolved;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cache ??= LoadCache();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            var onlineAvailable = true;
            var emptyResults = new List<(string Identity, string Key, ShowNameCandidate Candidate)>();
            var offset = groups.Length == 0 ? 0 : _lookupOffset % groups.Length;
            for (var position = 0; position < groups.Length; position++)
            {
                var index = (offset + position) % groups.Length;
                var group = groups[index];
                cancellationToken.ThrowIfCancellationRequested();
                var years = group.Where(candidate => candidate.Year.HasValue).Select(candidate => candidate.Year).Distinct().ToArray();
                var candidate = group.First() with { Year = years.Length == 1 ? years[0] : null };
                var key = candidate.Identity + ":" + candidate.Year;
                if (_cache.TryGetValue(key, out var cached) && IsSafeName(cached.CanonicalName) && cached.TvmazeId > 0)
                {
                    resolved[group.Key] = cached.CanonicalName;
                    continue;
                }

                if (!onlineAvailable || budget.IsCancellationRequested)
                {
                    continue;
                }

                try
                {
                    _lookupOffset = (index + 1) % groups.Length;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    using var response = await _httpClient.GetAsync(
                        "https://api.tvmaze.com/search/shows?q=" + Uri.EscapeDataString(candidate.Name),
                        HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        onlineAvailable = false;
                        continue;
                    }

                    var results = await response.Content.ReadFromJsonAsync<List<SearchResult>>(JsonOptions, timeout.Token).ConfigureAwait(false);
                    if (results is { Count: 0 })
                    {
                        emptyResults.Add((group.Key, key, candidate));
                    }

                    var match = SelectMatch(candidate, results ?? []);
                    if (match is null)
                    {
                        continue;
                    }

                    resolved[group.Key] = match.Name!;
                    _cache[key] = new CachedShow(candidate.Name, match.Id, match.Name!, candidate.Year);
                    SaveCache();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    onlineAvailable = false;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or NotSupportedException or ArgumentException)
                {
                    onlineAvailable = false;
                }
            }

            foreach (var item in emptyResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = FindVerifiedSpellingMatch(item.Candidate);
                if (match is null)
                {
                    continue;
                }

                resolved[item.Identity] = match.CanonicalName;
                _cache[item.Key] = match with { CleanedLocalName = item.Candidate.Name, Year = item.Candidate.Year ?? match.Year };
                SaveCache();
            }

            return resolved;
        }
        finally
        {
            _gate.Release();
        }
    }

    private CachedShow? FindVerifiedSpellingMatch(ShowNameCandidate candidate)
    {
        var matches = _cache!.Values.Where(match => !candidate.Year.HasValue || candidate.Year == match.Year)
            .GroupBy(match => match.TvmazeId)
            .Select(group => group.First())
            .Select(match => new { Match = match, Identity = ShowNameNormalizer.Identity(match.CanonicalName) })
            .Select(item => new { item.Match, Length = Math.Max(candidate.Identity.Length, item.Identity.Length), Similarity = Similarity(candidate.Identity, item.Identity) })
            .OrderByDescending(item => item.Similarity).ToArray();
        if (matches.Length == 0)
        {
            return null;
        }

        var best = matches[0];
        return best.Length >= 8 && best.Similarity >= 0.9 && (1 - best.Similarity) * best.Length <= 1.000001
            && (matches.Length == 1 || best.Similarity - matches[1].Similarity >= 0.06)
            ? best.Match : null;
    }

    private static TvmazeShow? SelectMatch(ShowNameCandidate candidate, IEnumerable<SearchResult> results)
    {
        var ranked = results.Where(result => result is not null && result.Show is { Id: > 0 } && IsSafeName(result.Show.Name))
            .GroupBy(result => result.Show!.Id)
            .Select(group => group.OrderByDescending(result => result.Score).First())
            .Select(result =>
            {
                var similarity = Similarity(candidate.Identity, ShowNameNormalizer.Identity(result.Show!.Name!));
                var year = DateOnly.TryParseExact(result.Show.Premiered, "yyyy-MM-dd", out var premiered) ? premiered.Year : (int?)null;
                var yearMatch = candidate.Year.HasValue && candidate.Year == year;
                var yearMismatch = candidate.Year.HasValue && year.HasValue && candidate.Year != year;
                var score = double.IsFinite(result.Score) && result.Score > 0 ? result.Score : 0;
                var confidence = 0.95 * similarity + 0.05 * score / (score + 1) + (yearMatch ? 0.12 : 0) - (yearMismatch ? 0.12 : 0);
                return new { result.Show, Similarity = similarity, Score = score, Confidence = confidence, YearMismatch = yearMismatch };
            })
            .OrderByDescending(result => result.Confidence).ToArray();
        if (ranked.Length == 0)
        {
            return null;
        }

        var best = ranked[0];
        return best.Similarity >= 0.88 && !best.YearMismatch
            && best.Confidence >= 0.86 && (ranked.Length == 1 || best.Confidence - ranked[1].Confidence >= 0.06)
            ? best.Show : null;
    }

    private static double Similarity(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0 || first.Length > 256 || second.Length > 256)
        {
            return 0;
        }

        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        for (var row = 1; row <= first.Length; row++)
        {
            var current = new int[second.Length + 1];
            current[0] = row;
            for (var column = 1; column <= second.Length; column++)
            {
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1));
            }

            previous = current;
        }

        return 1 - (double)previous[second.Length] / Math.Max(first.Length, second.Length);
    }

    private static bool IsSafeName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 256
        && name == name.Trim() && !name.EndsWith('.') && name is not "." and not ".."
        && !name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character));

    private Dictionary<string, CachedShow> LoadCache()
    {
        try
        {
            if (new FileInfo(_cachePath).LinkTarget is not null)
            {
                return new(StringComparer.Ordinal);
            }

            return JsonSerializer.Deserialize<Dictionary<string, CachedShow>>(File.ReadAllText(_cachePath), JsonOptions)
                ?.Where(pair => pair.Value is not null && IsSafeName(pair.Value.CanonicalName) && pair.Value.TvmazeId > 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void SaveCache()
    {
        var temporaryPath = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            if (new FileInfo(_cachePath).LinkTarget is not null)
            {
                return;
            }

            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_cache, JsonOptions));
            File.Move(temporaryPath, _cachePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _gate.Dispose();
    }

    private sealed record CachedShow(string CleanedLocalName, int TvmazeId, string CanonicalName, int? Year);

    private sealed class SearchResult
    {
        public double Score { get; set; }
        public TvmazeShow? Show { get; set; }
    }

    private sealed class TvmazeShow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("premiered")]
        public string? Premiered { get; set; }
    }
}