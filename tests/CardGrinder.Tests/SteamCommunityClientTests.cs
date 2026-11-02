using System.Net;
using System.Net.Http.Headers;
using CardGrinder.Core;
using Xunit;

namespace CardGrinder.Tests;

public sealed class SteamCommunityClientTests
{
    private const ulong SteamId = 76561198000000001;
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string Cookie)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!, string.Join(";", request.Headers.GetValues("Cookie"))));
            return Task.FromResult(respond(request, Requests.Count));
        }
    }
    private static HttpResponseMessage Redirect(string target, HttpStatusCode code = HttpStatusCode.Redirect)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
        return response;
    }
    private static SteamCommunityClient Client(Handler handler) => new(_ => Task.FromResult("test-cookie"), _ => Task.CompletedTask, _ => { }, handler);

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task NumericToVanityBadgeRedirectLoadsWithCookieAndPreservesPageAndLanguage(int status)
    {
        var handler = new Handler((_, count) => count == 1
            ? Redirect("https://steamcommunity.com/id/example/badges/", (HttpStatusCode)status)
            : new(HttpStatusCode.OK) { Content = new StringContent("<div class='badges_sheet'></div>") });
        int waits = 0;
        using var client = new SteamCommunityClient(_ => Task.FromResult("test-cookie"), _ => { waits++; return Task.CompletedTask; }, _ => { }, handler);
        var result = await client.GetBadgesAsync(SteamId, 4, default);
        Assert.Contains("badges_sheet", result);
        Assert.Equal(2, handler.Requests.Count); Assert.Equal(2, waits);
        Assert.Equal("/id/example/badges/", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal("?p=4&l=english", handler.Requests[1].Uri.Query);
        Assert.All(handler.Requests, request => Assert.Contains("steamLoginSecure=test-cookie", request.Cookie));
    }
    [Fact]
    public async Task RelativeBadgeRedirectIsSupported()
    {
        var handler = new Handler((_, count) => count == 1 ? Redirect("/id/example/badges") : new(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var client = Client(handler);
        Assert.Equal("ok", await client.GetBadgesAsync(SteamId, 1, default));
        Assert.Equal("steamcommunity.com", handler.Requests[1].Uri.Host);
    }
    [Theory]
    [InlineData("https://example.com/id/example/badges/")]
    [InlineData("http://steamcommunity.com/id/example/badges/")]
    [InlineData("https://steamcommunity.com.evil.example/id/example/badges/")]
    [InlineData("https://steamcommunity.com:444/id/example/badges/")]
    [InlineData("https://user:password@steamcommunity.com/id/example/badges/")]
    [InlineData("https://login.steampowered.com/jwt/refresh/?token=secret")]
    [InlineData("/login/home/?return=secret")]
    [InlineData("/id/example/")]
    [InlineData("/profiles/76561198000000002/badges/")]
    [InlineData("/id/encoded%2fpath/badges/")]
    public async Task UnsafeOrUnrelatedRedirectNeverReceivesCookie(string target)
    {
        var handler = new Handler((_, _) => Redirect(target)); using var client = Client(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetBadgesAsync(SteamId, 1, default));
        Assert.Single(handler.Requests);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("password", error.Message);
        Assert.DoesNotContain("test-cookie", error.Message);
    }
    [Fact]
    public async Task RedirectLoopStopsWithoutRepeatedRequests()
    {
        var handler = new Handler((_, _) => Redirect("/id/example/badges/")); using var client = Client(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetBadgesAsync(SteamId, 1, default));
        Assert.Contains("loop", error.Message); Assert.Equal(2, handler.Requests.Count);
    }
    [Fact]
    public async Task RedirectChainIsBounded()
    {
        var handler = new Handler((_, count) => Redirect($"/id/example{count}/badges/")); using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetBadgesAsync(SteamId, 1, default)); Assert.Equal(4, handler.Requests.Count);
    }
    [Fact]
    public async Task MissingLocationIsRejected()
    {
        var handler = new Handler((_, _) => new(HttpStatusCode.Redirect)); using var client = Client(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetBadgesAsync(SteamId, 1, default)); Assert.Single(handler.Requests);
    }
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task ActualAuthenticationRejectionIsReportedSeparately(int status)
    {
        var handler = new Handler((_, _) => new((HttpStatusCode)status)); using var client = Client(handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.GetBadgesAsync(SteamId, 1, default));
        Assert.Contains($"HTTP {status}", error.Message); Assert.Single(handler.Requests);
    }
    [Fact]
    public async Task RateLimitDefersRequestsWithoutFollowingOrRetrying()
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5)); return response;
        });
        DateTimeOffset? deferred = null;
        using var client = new SteamCommunityClient(_ => Task.FromResult("test-cookie"), _ => Task.CompletedTask, time => deferred = time, handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetBadgesAsync(SteamId, 1, default));
        Assert.True(deferred > DateTimeOffset.UtcNow.AddMinutes(4)); Assert.Single(handler.Requests);
    }
}
