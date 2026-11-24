using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace oshituner;

internal static class ThemeImageHttp
{
    private const int MaxRedirects = 5;
    private static readonly HttpClient Public = CreateClient(allowPrivate: false);
    private static readonly HttpClient Private = CreateClient(allowPrivate: true);

    internal static async Task<HttpResponseMessage> GetAsync(Uri url, bool allowPrivate, CancellationToken token)
    {
        var client = allowPrivate ? Private : Public;
        for (var redirects = 0; ; redirects++)
        {
            if (url.Scheme is not ("http" or "https") || url.UserInfo.Length != 0)
                throw new InvalidOperationException("Use an HTTP or HTTPS image URL without embedded credentials.");

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;

            using (response)
            {
                var location = response.Headers.Location;
                if (location is null || redirects >= MaxRedirects)
                    throw new InvalidOperationException("The image URL did not resolve within the redirect limit.");
                if (!Uri.TryCreate(url, location, out var next))
                    throw new InvalidOperationException("The image host returned an invalid redirect URL.");
                if (url.Scheme == "https" && next.Scheme != "https")
                    throw new InvalidOperationException("The image host redirected from HTTPS to an insecure URL.");
                url = next;
            }
        }
    }

    private static HttpClient CreateClient(bool allowPrivate) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // A proxy could resolve the destination independently of this policy.
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            if (!allowPrivate && addresses.Any(address => !IsPublicAddress(address)))
                throw new InvalidOperationException("The image URL resolves to a private or special network address.");

            SocketException? lastError = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException exception)
                {
                    socket.Dispose();
                    lastError = exception;
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            throw lastError ?? new SocketException((int)SocketError.HostNotFound);
        }
    });

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Permit globally scoped unicast and exclude special assignments, transition and documentation ranges.
            return (bytes[0] & 0xe0) == 0x20
                && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 2 || bytes[2] == 0x0d && bytes[3] == 0xb8))
                && !(bytes[0] == 0x20 && bytes[1] == 0x02)
                && !(bytes[0] == 0x3f && bytes[1] == 0xff && bytes[2] < 0x10);
        }
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && (bytes[1] == 168 || bytes[1] == 0 && bytes[2] is 0 or 2))
            && !(bytes[0] == 198 && (bytes[1] is 18 or 19 || bytes[1] == 51 && bytes[2] == 100))
            && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113);
    }
}
