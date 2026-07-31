using System.Net;
using ClaudeCounter.Core;
using ClaudeCounter.Tests.Fakes;
using Xunit;

namespace ClaudeCounter.Tests;

public class UpdateCheckerTests
{
    private const string GenuineReleaseUrl = "https://github.com/6spiderman/ClaudeCounter/releases/tag/v1.3.0";

    private static string Release(string tag, string url = GenuineReleaseUrl, string body = "notes") =>
        $$"""{"tag_name":"{{tag}}","html_url":"{{url}}","body":"{{body}}"}""";

    private static async Task<UpdateCheck> Check(StubHandler handler, string current) =>
        await new UpdateChecker(handler).CheckAsync(current, default);

    [Theory]
    [InlineData("v1.3.0", "1.2.0")]
    [InlineData("1.3.0", "v1.2.0")]     // no 'v' on the tag, one on the local version
    [InlineData("v2.0.0", "1.99.99")]
    [InlineData("v1.0.0", "1.0.0-rc.1")] // release supersedes the rc
    public async Task ReportsNewerReleases(string tag, string current)
    {
        var result = await Check(new StubHandler(HttpStatusCode.OK, Release(tag)), current);
        var available = Assert.IsType<UpdateCheck.Available>(result);
        Assert.Equal(tag.TrimStart('v'), available.Version);
        Assert.Equal(GenuineReleaseUrl, available.HtmlUrl);
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]      // same
    [InlineData("v1.2.0", "1.3.0")]      // local is ahead of the feed
    [InlineData("v1.0.0-rc.1", "1.0.0")] // an rc must never look newer than the release
    public async Task StaysQuietWhenNotNewer(string tag, string current) =>
        Assert.IsType<UpdateCheck.UpToDate>(
            await Check(new StubHandler(HttpStatusCode.OK, Release(tag)), current));

    [Fact]
    public async Task SendsTheHeadersGitHubRequires()
    {
        // GitHub answers 403 to a request with no User-Agent, so this is not
        // cosmetic - without it every check silently fails.
        var handler = new StubHandler(HttpStatusCode.OK, Release("v1.0.0"));
        await Check(handler, "1.0.0");

        Assert.Equal($"ClaudeCounter/{AppInfo.Version}", handler.Header("User-Agent"));
        Assert.Equal("application/vnd.github+json", handler.Header("Accept"));
        Assert.Equal("2022-11-28", handler.Header("X-GitHub-Api-Version"));
    }

    [Fact]
    public async Task CallsTheLatestReleaseEndpointForTheConfiguredRepo()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Release("v1.0.0"));
        await new UpdateChecker(handler, "someone/something").CheckAsync("1.0.0", default);

        Assert.Equal("https://api.github.com/repos/someone/something/releases/latest",
            handler.CapturedUri!.ToString());
    }

    [Fact]
    public async Task DefaultsToThisProjectsRepo()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Release("v1.0.0"));
        await Check(handler, "1.0.0");

        Assert.Equal($"https://api.github.com/repos/{AppInfo.RepoSlug}/releases/latest",
            handler.CapturedUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]       // rate limited
    [InlineData(HttpStatusCode.NotFound)]        // repo has no releases yet
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsAreFailuresNotExceptions(HttpStatusCode status) =>
        Assert.IsType<UpdateCheck.Failed>(
            await Check(new StubHandler(status, "{}"), "1.0.0"));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]                                   // no tag_name
    [InlineData("""{"tag_name":"banana"}""")]            // unparseable tag
    [InlineData("""{"tag_name":123}""")]                 // wrong type
    public async Task MalformedFeedsAreFailures(string body) =>
        Assert.IsType<UpdateCheck.Failed>(
            await Check(new StubHandler(HttpStatusCode.OK, body), "1.0.0"));

    [Fact]
    public async Task UnparseableLocalVersionFailsWithoutCallingGitHub()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Release("v9.9.9"));
        Assert.IsType<UpdateCheck.Failed>(await Check(handler, "garbage"));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FallsBackToTheReleasesPageWhenTheFeedHasNoUrl()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"tag_name":"v2.0.0"}""");
        var available = Assert.IsType<UpdateCheck.Available>(await Check(handler, "1.0.0"));
        Assert.Equal(AppInfo.ReleasesUrl, available.HtmlUrl);
    }

    // CC-02: html_url comes from an external feed and eventually reaches
    // Process.Start via Shell.OpenUrl. Anything that is not a genuine
    // https://github.com/... link must be replaced with the known-good
    // releases page rather than passed through.

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("http://github.com/6spiderman/ClaudeCounter/releases")] // right host, wrong scheme
    [InlineData("https://evil.example/releases")]                       // right scheme, wrong host
    [InlineData("https://github.com.evil.example/releases")]            // host suffix trick
    [InlineData("not a url at all")]
    public async Task UnsafeReleaseUrlsFallBackToTheReleasesPage(string maliciousUrl)
    {
        var handler = new StubHandler(HttpStatusCode.OK, Release("v2.0.0", url: maliciousUrl));
        var available = Assert.IsType<UpdateCheck.Available>(await Check(handler, "1.0.0"));
        Assert.Equal(AppInfo.ReleasesUrl, available.HtmlUrl);
    }

    [Fact]
    public async Task GenuineGitHubReleaseUrlIsPreserved()
    {
        const string url = "https://github.com/6spiderman/ClaudeCounter/releases/tag/v2.0.0";
        var handler = new StubHandler(HttpStatusCode.OK, Release("v2.0.0", url: url));
        var available = Assert.IsType<UpdateCheck.Available>(await Check(handler, "1.0.0"));
        Assert.Equal(url, available.HtmlUrl);
    }

    [Fact]
    public async Task LongReleaseNotesAreTruncated()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Release("v2.0.0", body: new string('x', 900)));
        var available = Assert.IsType<UpdateCheck.Available>(await Check(handler, "1.0.0"));

        Assert.NotNull(available.Notes);
        Assert.True(available.Notes!.Length < 900, "notes should be truncated for the About box");
        Assert.EndsWith("...", available.Notes);
    }

    [Fact]
    public async Task EmptyReleaseNotesBecomeNull()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"tag_name":"v2.0.0","body":""}""");
        var available = Assert.IsType<UpdateCheck.Available>(await Check(handler, "1.0.0"));
        Assert.Null(available.Notes);
    }
}
