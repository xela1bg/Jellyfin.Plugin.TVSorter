using System.Net;
using Jellyfin.Plugin.TVSorter.Configuration;
using Jellyfin.Plugin.TVSorter.Services;
using Xunit;

namespace Jellyfin.Plugin.TVSorter.Tests.Services;

public sealed class ShowNameCanonicalizerTests
{
    [Theory]
    [InlineData("Heated Rivalry (2025)", "Heated Rivalry", 2025)]
    [InlineData("The Originals 2015", "The Originals", 2015)]
    [InlineData("Shifting Gears 2025", "Shifting Gears", 2025)]
    [InlineData("Dark Matter 2024", "Dark Matter", 2024)]
    [InlineData("Foundation (2021)", "Foundation", 2021)]
    [InlineData("Foundation 2021", "Foundation", 2021)]
    [InlineData("Foundation", "Foundation", null)]
    [InlineData("1923", "1923", null)]
    [InlineData("1883", "1883", null)]
    [InlineData("3 Body Problem", "3 Body Problem", null)]
    [InlineData("Station 19", "Station 19", null)]
    [InlineData("Hawaii Five-0", "Hawaii Five-0", null)]
    [InlineData("2025 Show", "2025 Show", null)]
    public void NormalizesOnlyTrailingYears(string input, string expected, int? year)
    {
        var candidate = ShowNameNormalizer.Normalize(input);
        Assert.Equal(expected, candidate.Name);
        Assert.Equal(year, candidate.Year);
    }

    [Theory]
    [InlineData("Chicago Pd", "Chicago PD")]
    [InlineData("FROM", "From")]
    public void CaseVariantsHaveTheSameIdentity(string first, string second)
    {
        Assert.Equal(ShowNameNormalizer.Normalize(first), ShowNameNormalizer.Normalize(second));
    }

    [Theory]
    [InlineData("[{\"score\":15,\"show\":{\"id\":23,\"name\":\"Hawaii Five-0\"}}]", "Hawaii Five-0")]
    [InlineData("[{\"score\":15,\"show\":{\"id\":23,\"name\":\"Hawaii Five-0\"}},{\"score\":14,\"show\":{\"id\":24,\"name\":\"Hawaii Five-0\"}}]", "Hawaii Flve 0")]
    [InlineData("[{\"score\":100,\"show\":{\"id\":23,\"name\":\"Unrelated Title\"}}]", "Hawaii Flve 0")]
    [InlineData("[]", "Hawaii Flve 0")]
    [InlineData("invalid-json", "Hawaii Flve 0")]
    [InlineData("[null,{\"score\":15,\"show\":null}]", "Hawaii Flve 0")]
    [InlineData("[{\"score\":30,\"show\":{\"id\":1,\"name\":\"Unrelated Show\"}},{\"score\":15,\"show\":{\"id\":23,\"name\":\"Hawaii Five-0\"}}]", "Hawaii Five-0")]
    [InlineData("[{\"score\":15,\"show\":{\"id\":23,\"name\":\"../Hawaii Flve 0\"}}]", "Hawaii Flve 0")]
    public async Task AcceptsOnlyClearSafeMatches(string json, string expected)
    {
        using var fixture = new CanonicalizerFixture(json);
        var candidate = ShowNameNormalizer.Normalize("Hawaii Flve 0");
        var result = await fixture.Canonicalizer.ResolveAsync([candidate], CancellationToken.None);
        Assert.Equal(expected, result[candidate.Identity]);
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Fact]
    public async Task UsesYearToDistinguishRemakesAndDoesNotBlindlyUseFirstResult()
    {
        using var fixture = new CanonicalizerFixture("""
            [{"score":16,"show":{"id":1,"name":"Dark Matter","premiered":"2015-06-12"}},
             {"score":15,"show":{"id":2,"name":"Dark Matter","premiered":"2024-05-08"}}]
            """);
        var candidate = ShowNameNormalizer.Normalize("Dark Matter 2024");
        var result = await fixture.Canonicalizer.ResolveAsync([candidate], CancellationToken.None);
        Assert.Equal("Dark Matter", result[candidate.Identity]);
        Assert.Contains("\"tvmazeId\":2", File.ReadAllText(Path.Combine(fixture.Store.StorageDirectory, "show-names.json")));
    }

    [Fact]
    public async Task GroupsEpisodesAndPersistsSuccessfulMatchesAcrossInstances()
    {
        using var fixture = new CanonicalizerFixture("[{\"score\":15,\"show\":{\"id\":23,\"name\":\"Hawaii Five-0\"}}]");
        var candidate = ShowNameNormalizer.Normalize("Hawaii Flve 0");
        var first = await fixture.Canonicalizer.ResolveAsync(Enumerable.Repeat(candidate, 20), CancellationToken.None);
        var second = await fixture.Canonicalizer.ResolveAsync([candidate], CancellationToken.None);
        using var reloaded = new ShowNameCanonicalizer(new HttpClient(fixture.Handler, false), fixture.Store, () => new PluginConfiguration());
        var third = await reloaded.ResolveAsync([candidate], CancellationToken.None);
        Assert.Equal("Hawaii Five-0", first[candidate.Identity]);
        Assert.Equal(first[candidate.Identity], second[candidate.Identity]);
        Assert.Equal(first[candidate.Identity], third[candidate.Identity]);
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ProviderFailureStopsFurtherRequestsInRun(HttpStatusCode status)
    {
        using var fixture = new CanonicalizerFixture("[]", status);
        var result = await fixture.Canonicalizer.ResolveAsync([ShowNameNormalizer.Normalize("Foundation (2021)"), ShowNameNormalizer.Normalize("From")], CancellationToken.None);
        Assert.Equal("Foundation", result[ShowNameNormalizer.Identity("Foundation")]);
        Assert.Equal("From", result[ShowNameNormalizer.Identity("From")]);
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportFailureOrTimeoutFallsBackWithoutCaching(bool timeout)
    {
        using var fixture = new CanonicalizerFixture("[]");
        fixture.Handler.Failure = timeout ? new OperationCanceledException("mock timeout") : new HttpRequestException("mock offline");
        var candidate = ShowNameNormalizer.Normalize("Foundation (2021)");
        var first = await fixture.Canonicalizer.ResolveAsync([candidate], CancellationToken.None);
        var second = await fixture.Canonicalizer.ResolveAsync([candidate], CancellationToken.None);
        Assert.Equal("Foundation", first[candidate.Identity]);
        Assert.Equal("Foundation", second[candidate.Identity]);
        Assert.Equal(2, fixture.Handler.Requests);
        Assert.False(File.Exists(Path.Combine(fixture.Store.StorageDirectory, "show-names.json")));
    }

    [Fact]
    public async Task DisabledLookupDoesNotRequestOrUseCache()
    {
        using var fixture = new CanonicalizerFixture("[{\"score\":15,\"show\":{\"id\":23,\"name\":\"Hawaii Five-0\"}}]");
        var candidate = ShowNameNormalizer.Normalize("Hawaii Flve 0");
        await fixture.Canonicalizer.ResolveAsync([candidate], CancellationToken.None);
        fixture.Configuration.UseTvmaze = false;
        var result = await fixture.Canonicalizer.ResolveAsync([candidate], CancellationToken.None);
        Assert.Equal("Hawaii Flve 0", result[candidate.Identity]);
        Assert.Equal(1, fixture.Handler.Requests);
    }

    private sealed class CanonicalizerFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tv-sorter-name-test-" + Guid.NewGuid().ToString("N"));
        public CanonicalizerFixture(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            Handler = new MockTvmazeHandler(json, status);
            Store = new ManagedLinkManifestStore(_root);
            Canonicalizer = new ShowNameCanonicalizer(new HttpClient(Handler), Store, () => Configuration);
        }

        public PluginConfiguration Configuration { get; } = new();
        public MockTvmazeHandler Handler { get; }
        public ManagedLinkManifestStore Store { get; }
        public ShowNameCanonicalizer Canonicalizer { get; }

        public void Dispose()
        {
            Canonicalizer.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
    }
}

internal sealed class MockTvmazeHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public int Requests { get; private set; }
    public Exception? Failure { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        Assert.Equal("api.tvmaze.com", request.RequestUri!.Host);
        if (Failure is not null)
        {
            throw Failure;
        }

        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }
}