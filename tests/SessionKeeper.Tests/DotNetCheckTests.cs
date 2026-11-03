using System.Net;
using System.Net.Http;
using SessionKeeper.Services;
using static SessionKeeper.Services.DotNetReleaseCheck;

namespace SessionKeeper.Tests;

[Collection(nameof(LaunchGuardTests))] // FETCH events go to the (overridden) launch log
public sealed class DotNetCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sk-dotnet-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    public DotNetCheckTests() => LaunchGuard.LogDirOverride = Path.Combine(_dir, "logs");

    public void Dispose()
    {
        LaunchGuard.LogDirOverride = null;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ChannelInfo Info(string latest, string eol = "2026-11-10", string phase = "maintenance", bool latestSecurity = true,
        params ReleaseInfo[] releases) => new()
    {
        Channel = "8.0", FetchedAt = Now, LatestRuntime = latest, LatestSecurity = latestSecurity, SupportPhase = phase, EolDate = eol,
        Releases = releases.Length > 0 ? releases.ToList() : null,
    };

    // ------------------------------------------------------------------ evaluation

    [Fact]
    public void UpToDateIsGrey()
    {
        var s = Evaluate(new Version(8, 0, 33), Info("8.0.33"), Now)!;
        Assert.False(s.Outdated);
        Assert.Equal("Latest .NET 8.0 patch.", s.Detail);
    }

    [Fact]
    public void NewerSecurityPatchIsRedAndSaysWhy()
    {
        var s = Evaluate(new Version(8, 0, 11), Info("8.0.14", releases: new[]
        {
            new ReleaseInfo("8.0.14", "2026-08-12", true, 3),
            new ReleaseInfo("8.0.13", "2026-07-08", false, 0),
            new ReleaseInfo("8.0.12", "2026-06-10", true, 2),
            new ReleaseInfo("8.0.11", "2026-05-13", true, 9), // the running one: not counted
        }), Now)!;
        Assert.True(s.Outdated);
        Assert.Equal(".NET 8.0.14 available \u2013 security fixes (5 CVEs) (3 newer patches).", s.Detail);
    }

    [Fact]
    public void NewerBugFixPatchAndIndexOnlyFallback()
    {
        var bugfix = Evaluate(new Version(8, 0, 13), Info("8.0.14", releases: new ReleaseInfo("8.0.14", null, false, 0)), Now)!;
        Assert.True(bugfix.Outdated);
        Assert.Equal(".NET 8.0.14 available \u2013 bug fixes.", bugfix.Detail);

        // releases.json unavailable: the index's latest-release security flag is used
        var indexOnly = Evaluate(new Version(8, 0, 13), Info("8.0.14", latestSecurity: true), Now)!;
        Assert.Equal(".NET 8.0.14 available \u2013 security fixes.", indexOnly.Detail);
    }

    [Fact]
    public void OutOfSupportIsRedEvenWhenPatched()
    {
        var afterEol = Evaluate(new Version(8, 0, 33), Info("8.0.33", eol: "2026-11-10"), new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero))!;
        Assert.True(afterEol.Outdated);
        Assert.StartsWith(".NET 8.0 is out of support since 10 Nov 2026", afterEol.Detail);

        Assert.True(Evaluate(new Version(8, 0, 33), Info("8.0.33", eol: "2030-01-01", phase: "eol"), Now)!.Outdated);
    }

    [Fact]
    public void SupportEndingSoonIsMentionedButStaysGrey()
    {
        var s = Evaluate(new Version(8, 0, 33), Info("8.0.33", eol: "2026-11-10"), new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero))!;
        Assert.False(s.Outdated);
        Assert.Contains(".NET 8.0 support ends 10 Nov 2026 (in 7 days)", s.Detail);
    }

    [Fact]
    public void UnusableMetadataGivesNoAnswer() => Assert.Null(Evaluate(new Version(8, 0, 1), Info("not-a-version"), Now));

    [Theory]
    [InlineData("https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json", true)]
    [InlineData("https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json", true)]
    [InlineData("http://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json", false)]
    [InlineData("https://example.com/releases.json", false)]
    [InlineData("file:///C:/releases.json", false)]
    public void OnlyHttpsMicrosoftHosts(string url, bool allowed) => Assert.Equal(allowed, IsAllowed(url));

    // ------------------------------------------------------------------ fetching and caching (fake network)

    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, (HttpStatusCode Code, string Body)> Responses = new();
        public int Calls;
        public bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Offline) throw new HttpRequestException("No such host is known.");
            var url = request.RequestUri!.ToString();
            var (code, body) = Responses.TryGetValue(url, out var r) ? r : (HttpStatusCode.NotFound, "");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body), RequestMessage = request });
        }
    }

    private const string RelUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json";

    private static string Index(string latest, string releasesUrl = RelUrl) =>
        $$"""{"releases-index":[{"channel-version":"9.0","latest-runtime":"9.0.9"},{"channel-version":"8.0","latest-runtime":"{{latest}}","security":true,"support-phase":"active","eol-date":"2026-11-10","releases.json":"{{releasesUrl}}"}]}""";

    private const string Releases = """
        {"releases":[
          {"release-date":"2026-08-12","runtime":{"version":"8.0.14"},"security":true,"cve-list":[{"cve-id":"a"},{"cve-id":"b"}]},
          {"release-date":"2026-07-08","runtime":{"version":"8.0.13"},"security":false,"cve-list":[]},
          {"release-date":"2026-06-10","runtime":{"version":"8.0.12"},"security":true,"cve-list":[{"cve-id":"c"}]}
        ]}
        """;

    [Fact]
    public async Task FetchesEvaluatesAndCachesFor24Hours()
    {
        var net = new FakeHandler();
        net.Responses[IndexUrls[0]] = (HttpStatusCode.OK, Index("8.0.14"));
        net.Responses[RelUrl] = (HttpStatusCode.OK, Releases);
        var now = Now;
        var check = new DotNetReleaseCheck(net, _dir, () => now);

        var s = await check.CheckAsync(new Version(8, 0, 12), CancellationToken.None);
        Assert.True(s!.Outdated);
        Assert.Equal(".NET 8.0.14 available \u2013 security fixes (2 CVEs) (2 newer patches).", s.Detail);
        Assert.Equal(2, net.Calls);
        Assert.True(File.Exists(Path.Combine(_dir, "dotnet-8.0.json")));

        now = Now.AddHours(23); // cached: no network
        Assert.True((await check.CheckAsync(new Version(8, 0, 12), CancellationToken.None))!.Outdated);
        Assert.Equal(2, net.Calls);

        now = Now.AddHours(25); // stale: fetched again
        net.Responses[IndexUrls[0]] = (HttpStatusCode.OK, Index("8.0.12"));
        Assert.False((await check.CheckAsync(new Version(8, 0, 12), CancellationToken.None))!.Outdated);
        Assert.Equal(4, net.Calls);
    }

    [Fact]
    public async Task OfflineOrFailingStaysQuiet()
    {
        var offline = new FakeHandler { Offline = true };
        Assert.Null(await new DotNetReleaseCheck(offline, _dir, () => Now).CheckAsync(new Version(8, 0, 1), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_dir, "dotnet-8.0.json"))); // nothing cached from a failure

        var broken = new FakeHandler();
        broken.Responses[IndexUrls[0]] = (HttpStatusCode.OK, "{ not json");
        broken.Responses[IndexUrls[1]] = (HttpStatusCode.InternalServerError, "");
        Assert.Null(await new DotNetReleaseCheck(broken, _dir, () => Now).CheckAsync(new Version(8, 0, 1), CancellationToken.None));
    }

    [Fact]
    public async Task FallsBackToTheSecondEndpointAndIgnoresUntrustedReleaseUrls()
    {
        var net = new FakeHandler();
        net.Responses[IndexUrls[1]] = (HttpStatusCode.OK, Index("8.0.14", releasesUrl: "http://evil.example/releases.json"));
        var s = await new DotNetReleaseCheck(net, _dir, () => Now).CheckAsync(new Version(8, 0, 13), CancellationToken.None);
        Assert.True(s!.Outdated);
        Assert.Equal(".NET 8.0.14 available \u2013 security fixes.", s.Detail); // from the index alone
        Assert.Equal(2, net.Calls); // first endpoint (404), second endpoint; the http:// URL was never requested
    }

    [Fact]
    public void RuntimeDisplayUsesTheActualRuntime()
    {
        Assert.Equal(".NET 8.0.11", DotNetRuntime.Display(new Version(8, 0, 11)));
        Assert.Equal(Environment.Version, DotNetRuntime.Current);
        Assert.Matches(@"^\.NET \d+\.\d+\.\d+$", DotNetRuntime.Display(DotNetRuntime.Current));
    }
}
