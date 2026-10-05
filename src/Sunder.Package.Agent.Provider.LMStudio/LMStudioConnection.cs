using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Provider.LMStudio;

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

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal LMStudioConnection(IPackageContext packageContext, ProviderSettings settings)
        : this(packageContext, settings.Credentials) { }

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
