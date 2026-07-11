using System.Net;
using Sunder.Package.Agent.Tools.Web.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class WebFetchSecurityTests
{
    public static TheoryData<string, bool> GlobalUnicastAddressCases => new()
    {
        { "0.255.255.255", false }, { "1.0.0.0", true },
        { "10.0.0.1", false }, { "100.63.255.255", true }, { "100.64.0.0", false }, { "100.127.255.255", false }, { "100.128.0.0", true },
        { "127.255.255.255", false }, { "169.254.1.1", false }, { "172.15.255.255", true }, { "172.16.0.0", false }, { "172.31.255.255", false }, { "172.32.0.0", true },
        { "192.0.0.9", false }, { "192.0.2.1", false }, { "192.31.196.1", false }, { "192.52.193.1", false }, { "192.88.99.1", false },
        { "192.168.1.1", false }, { "192.175.48.1", false }, { "198.18.0.1", false }, { "198.51.100.1", false }, { "203.0.113.1", false },
        { "8.8.8.8", true }, { "93.184.216.34", true }, { "223.255.255.255", true }, { "224.0.0.1", false }, { "255.255.255.255", false },
        { "::", false }, { "::1", false }, { "::ffff:127.0.0.1", false }, { "::ffff:8.8.8.8", true },
        { "64:ff9b::a00:1", false }, { "64:ff9b::808:808", true }, { "64:ff9b:1::808:808", false },
        { "100::1", false }, { "100:0:0:1::1", false }, { "2001::1", false }, { "2001:db8::1", false },
        { "2002:0808:0808::1", false }, { "2620:4f:8000::1", false }, { "3fff::1", false }, { "5f00::1", false },
        { "fc00::1", false }, { "fe80::1", false }, { "fec0::1", false }, { "ff02::1", false },
        { "2001:4860:4860::8888", true }, { "2606:2800:220:1:248:1893:25c8:1946", true },
        { "2001:0:4136:e378:8000:63bf:3fff:fdd2", false }, { "2001:4860:0:0:0:5efe:808:808", false },
    };

    [Theory]
    [MemberData(nameof(GlobalUnicastAddressCases))]
    public void AddressClassification_OnlyAcceptsGlobalUnicast(string address, bool expected)
        => Assert.Equal(expected, WebUrlNetworkPolicy.IsGlobalUnicast(IPAddress.Parse(address)));

    [Theory]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.1.1/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://100.100.100.200/")]
    [InlineData("http://168.63.129.16/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[ff02::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    public async Task Policy_RejectsNonPublicAndMetadataAddressLiterals(string url)
    {
        var resolver = new FakeWebHostResolver((_, _) => Task.FromResult(Array.Empty<IPAddress>()));
        var policy = new WebUrlNetworkPolicy(resolver);

        await Assert.ThrowsAsync<WebNetworkPolicyException>(
            () => policy.ValidateAndResolveAsync(new Uri(url), CancellationToken.None));

        Assert.Empty(resolver.ResolvedHosts);
    }

    [Fact]
    public async Task Policy_RejectsHostWhenAnyDnsAnswerIsPrivate()
    {
        var resolver = new FakeWebHostResolver((_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse("93.184.216.34"),
            IPAddress.Parse("10.0.0.2"),
        }));
        var policy = new WebUrlNetworkPolicy(resolver);

        await Assert.ThrowsAsync<WebNetworkPolicyException>(
            () => policy.ValidateAndResolveAsync(new Uri("https://mixed.example/resource"), CancellationToken.None));

        Assert.Equal(["mixed.example"], resolver.ResolvedHosts);
    }

    [Theory]
    [InlineData("http://metadata.google.internal/")]
    [InlineData("https://metadata.goog/computeMetadata/v1/")]
    [InlineData("http://instance-data/latest/meta-data/")]
    public async Task Policy_RejectsKnownCloudMetadataHostsBeforeDns(string url)
    {
        var resolver = new FakeWebHostResolver((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));
        var policy = new WebUrlNetworkPolicy(resolver);

        await Assert.ThrowsAsync<WebNetworkPolicyException>(
            () => policy.ValidateAndResolveAsync(new Uri(url), CancellationToken.None));

        Assert.Empty(resolver.ResolvedHosts);
    }

    [Fact]
    public async Task FetchAsync_FollowsPublicRedirectAndValidatesEveryRequest()
    {
        var resolver = PublicResolver();
        var clientFactory = new FakeWebFetchHttpClientFactory(requestNumber => requestNumber == 1
            ? Redirect("/final")
            : HtmlResponse("<title>Public page</title><p>Hello</p>"));
        var service = CreateService(resolver, clientFactory);

        var result = await service.FetchAsync("https://public.example/start", "text", null, CancellationToken.None);

        Assert.Equal("https://public.example/final", result.FinalUrl);
        Assert.Equal("Public page", result.Title);
        Assert.Equal(["public.example", "public.example"], resolver.ResolvedHosts);
        Assert.Equal(2, clientFactory.RequestCount);
        Assert.All(clientFactory.Destinations, destination =>
            Assert.Equal(IPAddress.Parse("93.184.216.34"), Assert.Single(destination.Addresses)));
    }

    [Fact]
    public async Task FetchAsync_BlocksRedirectWhenHostRebindsToPrivateAddress()
    {
        var resolution = 0;
        var resolver = new FakeWebHostResolver((_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse(++resolution == 1 ? "93.184.216.34" : "10.0.0.5"),
        }));
        var clientFactory = new FakeWebFetchHttpClientFactory(_ => Redirect("/admin"));
        var service = CreateService(resolver, clientFactory);

        await Assert.ThrowsAsync<WebNetworkPolicyException>(
            () => service.FetchAsync("https://rebind.example/start", "text", null, CancellationToken.None));

        Assert.Equal(["rebind.example", "rebind.example"], resolver.ResolvedHosts);
        Assert.Equal(1, clientFactory.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_BlocksRedirectToNonHttpTarget()
    {
        var resolver = PublicResolver();
        var clientFactory = new FakeWebFetchHttpClientFactory(_ => Redirect("file:///etc/passwd"));
        var service = CreateService(resolver, clientFactory);

        await Assert.ThrowsAsync<WebNetworkPolicyException>(
            () => service.FetchAsync("https://public.example/start", "text", null, CancellationToken.None));

        Assert.Equal(["public.example"], resolver.ResolvedHosts);
        Assert.Equal(1, clientFactory.RequestCount);
    }

    [Fact]
    public async Task FetchAsync_BoundsRedirectCount()
    {
        var resolver = PublicResolver();
        var clientFactory = new FakeWebFetchHttpClientFactory(requestNumber => Redirect($"/redirect/{requestNumber}"));
        var service = CreateService(resolver, clientFactory);

        var exception = await Assert.ThrowsAsync<WebNetworkPolicyException>(
            () => service.FetchAsync("https://public.example/start", "text", null, CancellationToken.None));

        Assert.Contains("maximum of 5 redirects", exception.Message, StringComparison.Ordinal);
        Assert.Equal(6, clientFactory.RequestCount);
        Assert.Equal(6, resolver.ResolvedHosts.Count);
    }

    [Fact]
    public async Task FetchAsync_PreservesCallerCancellationDuringDnsResolution()
    {
        var resolver = new FakeWebHostResolver((_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
        });
        var service = CreateService(resolver, new FakeWebFetchHttpClientFactory(_ => HtmlResponse("unused")));
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.FetchAsync("https://public.example/", "text", null, cancellationSource.Token));
    }

    private static WebFetchService CreateService(
        IWebHostResolver resolver,
        IWebFetchHttpClientFactory clientFactory)
        => new(new WebUrlNetworkPolicy(resolver), clientFactory);

    private static FakeWebHostResolver PublicResolver()
        => new((_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

    private static HttpResponseMessage Redirect(string location)
        => new(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) },
        };

    private static HttpResponseMessage HtmlResponse(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content),
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html");
        return response;
    }

    private sealed class FakeWebHostResolver(
        Func<string, CancellationToken, Task<IPAddress[]>> resolveAsync) : IWebHostResolver
    {
        private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveAsync = resolveAsync;

        public List<string> ResolvedHosts { get; } = [];

        public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken)
        {
            ResolvedHosts.Add(host);
            return _resolveAsync(host, cancellationToken);
        }
    }

    private sealed class FakeWebFetchHttpClientFactory(
        Func<int, HttpResponseMessage> createResponse) : IWebFetchHttpClientFactory
    {
        private readonly Func<int, HttpResponseMessage> _createResponse = createResponse;

        public List<WebNetworkDestination> Destinations { get; } = [];

        public int RequestCount { get; private set; }

        public HttpClient CreateClient(WebNetworkDestination destination)
        {
            Destinations.Add(destination);
            return new HttpClient(new StubHttpMessageHandler(() =>
            {
                RequestCount++;
                return _createResponse(RequestCount);
            }))
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _createResponse = createResponse;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_createResponse());
        }
    }
}
