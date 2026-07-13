using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.LMStudio;

internal sealed record LMStudioConnectionOptions(
    Uri BaseUri,
    string NormalizedBaseUrl,
    string? ApiKey,
    TimeSpan Timeout)
{
    public LMStudioConnectionCacheKey CacheKey { get; } = new(
        NormalizedBaseUrl,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey ?? string.Empty))));

    public static bool TryCreate(
        string? baseUrl,
        string? apiKey,
        TimeSpan timeout,
        out LMStudioConnectionOptions options,
        out string validationError)
    {
        if (!TryNormalizeBaseUrl(baseUrl, out var baseUri, out var normalizedBaseUrl, out validationError))
        {
            options = null!;
            return false;
        }

        var normalizedApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (normalizedApiKey is not null
            && baseUri.Scheme == Uri.UriSchemeHttp
            && !baseUri.IsLoopback)
        {
            options = null!;
            validationError = "An authenticated LM Studio endpoint must use HTTPS unless it is loopback-only.";
            return false;
        }

        if (timeout <= TimeSpan.Zero)
        {
            options = null!;
            validationError = "The LM Studio timeout must be greater than zero.";
            return false;
        }

        options = new LMStudioConnectionOptions(
            baseUri,
            normalizedBaseUrl,
            normalizedApiKey,
            timeout);
        return true;
    }

    public static bool TryNormalizeBaseUrl(
        string? value,
        out Uri baseUri,
        out string normalizedBaseUrl,
        out string validationError)
    {
        var candidate = string.IsNullOrWhiteSpace(value)
            ? LMStudioProviderConfiguration.DefaultBaseUrl
            : value.Trim();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(parsedUri.Host))
        {
            baseUri = null!;
            normalizedBaseUrl = string.Empty;
            validationError = "Enter an absolute HTTP or HTTPS LM Studio base URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsedUri.Query) || !string.IsNullOrEmpty(parsedUri.Fragment))
        {
            baseUri = null!;
            normalizedBaseUrl = string.Empty;
            validationError = "The LM Studio base URL cannot contain a query string or fragment.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsedUri.UserInfo))
        {
            baseUri = null!;
            normalizedBaseUrl = string.Empty;
            validationError = "The LM Studio base URL cannot contain user information.";
            return false;
        }

        normalizedBaseUrl = parsedUri.AbsoluteUri.TrimEnd('/');
        baseUri = new Uri(normalizedBaseUrl + '/', UriKind.Absolute);
        validationError = string.Empty;
        return true;
    }
}

internal sealed record LMStudioConnectionCacheKey(string BaseUrl, string CredentialFingerprint);

internal sealed class LMStudioConnection : IDisposable
{
    private const string MissingCredentialSentinel = "sunder-lmstudio-no-credential-6e1d74ad";

    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IPackageContext _packageContext;
    private readonly ProviderCredentialAccessor _credentials;
    private readonly HttpClient _httpClient;
    private readonly HttpClientPipelineTransport _transport;
    private readonly TimeSpan _timeout;

    public LMStudioConnection(
        IPackageContext packageContext,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
        : this(
            packageContext,
            new ProviderCredentialAccessor(packageContext.Secrets, LMStudioProviderConfiguration.ApiKeyKey),
            handler,
            timeout)
    {
    }

    internal LMStudioConnection(
        IPackageContext packageContext,
        ProviderCredentialAccessor credentials,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        _packageContext = packageContext;
        _credentials = credentials;
        _timeout = timeout ?? DefaultTimeout;
        _httpClient = new HttpClient(
            new OptionalCredentialHandler(handler ?? new HttpClientHandler()),
            disposeHandler: true);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _transport = new HttpClientPipelineTransport(_httpClient);
    }

    public async Task<LMStudioConnectionValidationResult> GetOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var baseUrl = await _packageContext.Settings.GetValueAsync(
            LMStudioProviderConfiguration.BaseUrlKey,
            cancellationToken);
        var credential = await _credentials.GetCredentialAsync(cancellationToken);
        return LMStudioConnectionOptions.TryCreate(baseUrl, credential, _timeout, out var options, out var validationError)
            ? new LMStudioConnectionValidationResult(options, null)
            : new LMStudioConnectionValidationResult(null, validationError);
    }

    public async Task<LMStudioConnectionOptions> GetRequiredOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await GetOptionsAsync(cancellationToken);
        return result.Options ?? throw new InvalidOperationException(result.ValidationError);
    }

    public HttpRequestMessage CreateRequest(
        HttpMethod method,
        string relativePath,
        LMStudioConnectionOptions options)
    {
        var request = new HttpRequestMessage(method, new Uri(options.BaseUri, relativePath));
        if (options.ApiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        return request;
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        LMStudioConnectionOptions options,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.Timeout);
        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutSource.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"LM Studio did not respond within {options.Timeout.TotalSeconds:0.#} seconds.", ex);
        }
    }

    public ChatClient CreateChatClient(string modelId, LMStudioConnectionOptions options)
        => new(
            ProviderModelId.RemovePrefix(modelId, "lmstudio"),
            new ApiKeyCredential(options.ApiKey ?? MissingCredentialSentinel),
            CreateClientOptions(options));

    public EmbeddingClient CreateEmbeddingClient(string modelId, LMStudioConnectionOptions options)
        => new(
            ProviderModelId.RemovePrefix(modelId, "lmstudio"),
            new ApiKeyCredential(options.ApiKey ?? MissingCredentialSentinel),
            CreateClientOptions(options));

    public void Dispose()
    {
        _transport.Dispose();
        _httpClient.Dispose();
    }

    private OpenAIClientOptions CreateClientOptions(LMStudioConnectionOptions options)
        => new()
        {
            Endpoint = new Uri(options.NormalizedBaseUrl, UriKind.Absolute),
            NetworkTimeout = options.Timeout,
            Transport = _transport,
        };

    private sealed class OptionalCredentialHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is { Scheme: "Bearer", Parameter: MissingCredentialSentinel })
            {
                request.Headers.Authorization = null;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}

internal sealed record LMStudioConnectionValidationResult(
    LMStudioConnectionOptions? Options,
    string? ValidationError);
