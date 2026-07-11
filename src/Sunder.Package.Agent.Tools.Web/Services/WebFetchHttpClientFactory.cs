using System.Net;
using System.Net.Sockets;

namespace Sunder.Package.Agent.Tools.Web.Services;

internal interface IWebFetchHttpClientFactory
{
    HttpClient CreateClient(WebNetworkDestination destination);
}

internal sealed class PinnedWebFetchHttpClientFactory : IWebFetchHttpClientFactory
{
    public HttpClient CreateClient(WebNetworkDestination destination)
    {
        var addresses = destination.Addresses.ToArray();
        var expectedHost = destination.Uri.IdnHost.TrimEnd('.');
        var expectedPort = destination.Uri.Port;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(
                context,
                expectedHost,
                expectedPort,
                addresses,
                cancellationToken),
        };

        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        string expectedHost,
        int expectedPort,
        IReadOnlyList<IPAddress> addresses,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(context.DnsEndPoint.Host.TrimEnd('.'), expectedHost, StringComparison.OrdinalIgnoreCase)
            || context.DnsEndPoint.Port != expectedPort)
        {
            throw new HttpRequestException("The HTTP connection destination did not match the validated URL.");
        }

        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, expectedPort), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                socket.Dispose();
                lastException = ex;
            }
        }

        throw new HttpRequestException($"Could not connect to the validated destination '{expectedHost}'.", lastException);
    }
}
