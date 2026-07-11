using System.Net;
using System.Text;

namespace Sunder.Package.Agent.Provider.OpenAI.Auth;

internal sealed class CodexAuthCallbackServer : IDisposable
{
    private const int PreferredPort = 1455;
    private const int FallbackPort = 1457;
    private const string CallbackPath = "/auth/callback";
    private readonly HttpListener _listener;

    private CodexAuthCallbackServer(HttpListener listener, string redirectUri)
    {
        _listener = listener;
        RedirectUri = redirectUri;
    }

    public string RedirectUri { get; }

    public static CodexAuthCallbackServer Start()
    {
        if (TryStart(PreferredPort, out var preferred, out var preferredException))
        {
            return preferred!;
        }

        if (TryStart(FallbackPort, out var fallback, out var fallbackException))
        {
            return fallback!;
        }

        throw new InvalidOperationException(
            $"Sunder could not start the local OpenAI browser callback listener on {CreateRedirectUri(PreferredPort)} or {CreateRedirectUri(FallbackPort)}. Close other Codex/Sunder auth listeners or applications using ports {PreferredPort} and {FallbackPort} and retry.",
            new AggregateException(preferredException!, fallbackException!));
    }

    public async Task<CodexBrowserCallback> WaitAsync(string expectedState, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            try
            {
                ((HttpListener)state!).Stop();
            }
            catch
            {
            }
        }, _listener);

        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync();
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested
                                   && ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var returnedState = context.Request.QueryString["state"];
        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error"];
        var errorDescription = context.Request.QueryString["error_description"];
        var stateMatches = string.Equals(returnedState, expectedState, StringComparison.Ordinal);
        await WriteCompletionAsync(context.Response, stateMatches && !string.IsNullOrWhiteSpace(code), cancellationToken);
        return new CodexBrowserCallback(code, error, errorDescription, stateMatches);
    }

    public void Dispose() => _listener.Close();

    private static bool TryStart(int port, out CodexAuthCallbackServer? server, out Exception? exception)
    {
        var listener = new HttpListener();
        var redirectUri = CreateRedirectUri(port);
        listener.Prefixes.Add($"{redirectUri}/");
        try
        {
            listener.Start();
            server = new CodexAuthCallbackServer(listener, redirectUri);
            exception = null;
            return true;
        }
        catch (Exception ex)
        {
            listener.Close();
            server = null;
            exception = ex;
            return false;
        }
    }

    private static string CreateRedirectUri(int port) => $"http://localhost:{port}{CallbackPath}";

    private static async Task WriteCompletionAsync(
        HttpListenerResponse response,
        bool success,
        CancellationToken cancellationToken)
    {
        var title = WebUtility.HtmlEncode(success ? "OpenAI callback received." : "OpenAI sign-in failed.");
        var detail = WebUtility.HtmlEncode(success
            ? "Sunder is finishing authorization. You can close this window."
            : "Close this window and retry authorization from Sunder.");
        var html = $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
              <title>{{title}}</title>
              <style>
                :root{color-scheme:dark;font-family:system-ui,sans-serif}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#15171a;color:#dedad3}
                main{max-width:34rem;padding:2rem;border:1px solid #51442e;border-radius:14px;background:#1d2025;box-shadow:0 20px 70px #0008}h1{margin:0 0 .7rem;color:#e7b765;font-size:1.35rem}p{margin:0;color:#ccc7be;line-height:1.5}
              </style>
            </head>
            <body><main><h1>{{title}}</h1><p>{{detail}}</p></main></body>
            </html>
            """;
        var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.Close();
    }
}

internal sealed record CodexBrowserCallback(
    string? Code,
    string? Error,
    string? ErrorDescription,
    bool StateMatches);
