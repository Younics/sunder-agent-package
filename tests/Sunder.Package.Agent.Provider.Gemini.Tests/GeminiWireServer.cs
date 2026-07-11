using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sunder.Package.Agent.Provider.Gemini.Tests;

internal sealed record GeminiWireRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

internal sealed class GeminiWireServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource<GeminiWireRequest> _request =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _serverTask;
    private readonly int _statusCode;
    private readonly string _contentType;
    private readonly string _responseBody;
    private readonly bool _hangAfterRequest;

    public GeminiWireServer(
        string responseBody = "",
        string contentType = "application/json",
        int statusCode = 200,
        bool hangAfterRequest = false)
    {
        _responseBody = responseBody;
        _contentType = contentType;
        _statusCode = statusCode;
        _hangAfterRequest = hangAfterRequest;
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BaseUrl = $"http://127.0.0.1:{endpoint.Port}";
        _serverTask = RunAsync();
    }

    public string BaseUrl { get; }

    public Task<GeminiWireRequest> Request => _request.Task;

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            await _serverTask;
        }
        catch (Exception) when (_cancellation.IsCancellationRequested)
        {
        }

        _cancellation.Dispose();
    }

    private async Task RunAsync()
    {
        using var client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
        await using var stream = client.GetStream();
        var headerBytes = await ReadHeadersAsync(stream, _cancellation.Token);
        var headerText = Encoding.ASCII.GetString(headerBytes);
        var headerLines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = headerLines[0].Split(' ', 3);
        var headers = headerLines.Skip(1)
            .Select(line => line.Split(':', 2))
            .ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var contentLength = headers.TryGetValue("Content-Length", out var length)
            ? int.Parse(length, System.Globalization.CultureInfo.InvariantCulture)
            : 0;
        var bodyBytes = new byte[contentLength];
        await ReadExactlyAsync(stream, bodyBytes, _cancellation.Token);
        _request.TrySetResult(new GeminiWireRequest(
            requestLine[0],
            requestLine[1],
            headers,
            Encoding.UTF8.GetString(bodyBytes)));

        if (_hangAfterRequest)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, _cancellation.Token);
            return;
        }

        var responseBytes = Encoding.UTF8.GetBytes(_responseBody);
        var reason = _statusCode == 200 ? "OK" : "Bad Request";
        var responseHeaders = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {_statusCode} {reason}\r\n" +
            $"Content-Type: {_contentType}\r\n" +
            $"Content-Length: {responseBytes.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(responseHeaders, _cancellation.Token);
        await stream.WriteAsync(responseBytes, _cancellation.Token);
        await stream.FlushAsync(_cancellation.Token);
    }

    private static async Task<byte[]> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        byte[] terminator = [13, 10, 13, 10];
        var buffer = new byte[1];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("Connection closed before HTTP headers completed.");
            }

            bytes.Add(buffer[0]);
            if (bytes.Count >= terminator.Length
                && bytes.TakeLast(terminator.Length).SequenceEqual(terminator))
            {
                return bytes.ToArray();
            }
        }
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("Connection closed before the HTTP body completed.");
            }

            offset += count;
        }
    }
}
