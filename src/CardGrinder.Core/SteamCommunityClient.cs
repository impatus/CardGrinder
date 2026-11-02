using System.Net;
using System.Text.RegularExpressions;

namespace CardGrinder.Core;

/// <summary>Reads authenticated Badges pages, following only Steam's own profile canonicalization.</summary>
public sealed partial class SteamCommunityClient : IDisposable
{
    private const int MaxRedirects = 3;
    private readonly HttpClient http;
    private readonly Func<CancellationToken, Task<string>> getCookie;
    private readonly Func<CancellationToken, Task> beforeRequest;
    private readonly Action<DateTimeOffset> deferRequests;

    public SteamCommunityClient(Func<CancellationToken, Task<string>> getCookie,
        Func<CancellationToken, Task> beforeRequest, Action<DateTimeOffset> deferRequests,
        HttpMessageHandler? handler = null)
    {
        this.getCookie = getCookie;
        this.beforeRequest = beforeRequest;
        this.deferRequests = deferRequests;
        http = new(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("CardGrinder/0.1.1");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public async Task<string> GetBadgesAsync(ulong steamId, int page, CancellationToken ct)
    {
        if (steamId == 0 || page < 1) throw new ArgumentOutOfRangeException(nameof(page));
        var uri = new Uri($"https://steamcommunity.com/profiles/{steamId}/badges/?p={page}&l=english");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int redirects = 0; ; redirects++)
        {
            if (!visited.Add(uri.AbsoluteUri)) throw new InvalidDataException("Steam Community returned a redirect loop. Try refreshing later.");
            await beforeRequest(ct).ConfigureAwait(false);
            string cookie = await getCookie(ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Add("Cookie", $"steamLoginSecure={cookie}; Steam_Language=english");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                var retry = response.Headers.RetryAfter;
                deferRequests(retry?.Date ?? DateTimeOffset.UtcNow + (retry?.Delta ?? TimeSpan.FromMinutes(2)));
                throw new InvalidDataException("Steam asked CardGrinder to slow down. Farming is paused; wait before refreshing.");
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidDataException($"Steam Community rejected the web session (HTTP {(int)response.StatusCode}). Sign out and connect again using a new QR code.");

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects >= MaxRedirects)
                    throw new InvalidDataException("Steam Community returned too many redirects. Try refreshing later.");
                var location = response.Headers.Location;
                if (location is null || !Uri.TryCreate(uri, location, out var target))
                    throw new InvalidDataException("Steam Community returned an invalid redirect. Card status was not changed.");

                // Never follow login handoffs, other hosts, downgraded HTTP, other accounts, or non-Badges routes.
                // A numeric profile redirecting to its vanity /id/ URL is normal, not a rejected login.
                if (!IsBadgeRedirect(target, steamId))
                    throw new InvalidDataException("Steam Community redirected away from Badges. The web session may have expired; sign out and connect again using a new QR code.");
                // Keep the requested page and language, even if the canonical Location omitted its query.
                uri = new UriBuilder(target) { Query = $"p={page}&l=english", Fragment = "" }.Uri;
                continue;
            }

            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidDataException($"Steam Community returned an unexpected response (HTTP {(int)response.StatusCode}). Try refreshing later.");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool IsBadgeRedirect(Uri target, ulong steamId)
    {
        if (target.Scheme != Uri.UriSchemeHttps || !target.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
            || !target.IsDefaultPort || target.UserInfo.Length != 0) return false;
        var match = BadgePath().Match(target.AbsolutePath);
        if (!match.Success) return false;
        if (match.Groups[1].Success) return match.Groups[1].Value == steamId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string vanity = Uri.UnescapeDataString(match.Groups[2].Value);
        return vanity is not "." and not ".." && vanity.IndexOfAny(['/', '\\']) < 0;
    }

    [GeneratedRegex(@"^/(?:profiles/(\d+)|id/([^/]+))/badges/?$", RegexOptions.CultureInvariant)]
    private static partial Regex BadgePath();
    public void Dispose() => http.Dispose();
}
