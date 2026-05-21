using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Sunder.Package.Agent.Provider.OpenAI;

internal static class CodexHttpClientFactory
{
    // Some networks expose broken IPv6 routes that blackhole OpenAI/ChatGPT HTTPS traffic.
    // Codex-connected traffic is first-party OpenAI/ChatGPT only, so prefer IPv4 here.
    public const string NetworkAddressFamily = "ipv4";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(30);
    private static readonly string UserAgent = $"Sunder/{typeof(CodexHttpClientFactory).Assembly.GetName().Version}";

    public static HttpClient CreateAuthClient()
    {
        var httpClient = new HttpClient(CreateHandler())
        {
            Timeout = AuthTimeout,
        };
        ApplyDefaultAuthHeaders(httpClient.DefaultRequestHeaders);
        return httpClient;
    }

    public static HttpClient CreateBackendClient()
        => new(CreateHandler())
        {
            BaseAddress = new Uri("https://chatgpt.com/backend-api/"),
        };

    private static SocketsHttpHandler CreateHandler()
        => new()
        {
            ConnectCallback = ConnectIpv4Async,
            ConnectTimeout = ConnectTimeout,
        };

    private static void ApplyDefaultAuthHeaders(HttpRequestHeaders headers)
    {
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        headers.UserAgent.ParseAdd(UserAgent);
    }

    private static async ValueTask<Stream> ConnectIpv4Async(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var ipv4Addresses = addresses
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();
        if (ipv4Addresses.Length == 0)
        {
            throw new HttpRequestException($"No IPv4 address was found for {context.DnsEndPoint.Host}.");
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(ipv4Addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
