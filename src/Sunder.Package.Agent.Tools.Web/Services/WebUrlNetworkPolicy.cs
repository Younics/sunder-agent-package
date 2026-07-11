using System.Net;
using System.Net.Sockets;

namespace Sunder.Package.Agent.Tools.Web.Services;

internal interface IWebHostResolver
{
    Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken);
}

internal sealed class SystemWebHostResolver : IWebHostResolver
{
    public Task<IPAddress[]> GetHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}

internal sealed class WebUrlNetworkPolicy(IWebHostResolver hostResolver)
{
    private static readonly IpNetwork[] BlockedIpv4Networks =
    [
        IpNetwork.Parse("0.0.0.0", 8),
        IpNetwork.Parse("10.0.0.0", 8),
        IpNetwork.Parse("100.64.0.0", 10),
        IpNetwork.Parse("127.0.0.0", 8),
        IpNetwork.Parse("169.254.0.0", 16),
        IpNetwork.Parse("172.16.0.0", 12),
        IpNetwork.Parse("192.0.0.0", 24),
        IpNetwork.Parse("192.0.2.0", 24),
        IpNetwork.Parse("192.31.196.0", 24),
        IpNetwork.Parse("192.52.193.0", 24),
        IpNetwork.Parse("192.88.99.0", 24),
        IpNetwork.Parse("192.168.0.0", 16),
        IpNetwork.Parse("192.175.48.0", 24),
        IpNetwork.Parse("198.18.0.0", 15),
        IpNetwork.Parse("198.51.100.0", 24),
        IpNetwork.Parse("203.0.113.0", 24),
        IpNetwork.Parse("224.0.0.0", 4),
        IpNetwork.Parse("240.0.0.0", 4),
    ];

    private static readonly IpNetwork[] BlockedIpv6Networks =
    [
        IpNetwork.Parse("::", 128),
        IpNetwork.Parse("::1", 128),
        IpNetwork.Parse("::ffff:0:0:0", 96),
        IpNetwork.Parse("64:ff9b:1::", 48),
        IpNetwork.Parse("100::", 64),
        IpNetwork.Parse("100:0:0:1::", 64),
        IpNetwork.Parse("2001::", 23),
        IpNetwork.Parse("2001:db8::", 32),
        IpNetwork.Parse("2002::", 16),
        IpNetwork.Parse("2620:4f:8000::", 48),
        IpNetwork.Parse("3fff::", 20),
        IpNetwork.Parse("5f00::", 16),
        IpNetwork.Parse("fc00::", 7),
        IpNetwork.Parse("fe80::", 10),
        IpNetwork.Parse("fec0::", 10),
        IpNetwork.Parse("ff00::", 8),
    ];

    private static readonly IpNetwork WellKnownNat64Network = IpNetwork.Parse("64:ff9b::", 96);

    private static readonly HashSet<string> CloudMetadataHostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "instance-data",
        "instance-data.ec2.internal",
        "metadata.aws.internal",
        "metadata.azure.internal",
        "metadata.goog",
        "metadata.google.internal",
    };

    private static readonly HashSet<IPAddress> CloudMetadataAddresses =
    [
        IPAddress.Parse("100.100.100.200"),
        IPAddress.Parse("168.63.129.16"),
    ];

    private readonly IWebHostResolver _hostResolver = hostResolver;

    public async Task<WebNetworkDestination> ValidateAndResolveAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
        {
            throw new WebNetworkPolicyException("Only absolute HTTP and HTTPS URLs are allowed.");
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new WebNetworkPolicyException("The URL must include a host.");
        }

        if (CloudMetadataHostNames.Contains(host))
        {
            throw new WebNetworkPolicyException($"The destination host '{host}' is a cloud metadata endpoint and is not allowed.");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literalAddress))
        {
            addresses = [literalAddress];
        }
        else
        {
            addresses = await _hostResolver.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        if (addresses.Length == 0)
        {
            throw new WebNetworkPolicyException($"The destination host '{host}' did not resolve to an IP address.");
        }

        foreach (var address in addresses)
        {
            if (!IsGlobalUnicast(address))
            {
                throw new WebNetworkPolicyException($"The destination host '{host}' resolves to a non-public or cloud metadata address and is not allowed.");
            }
        }

        return new WebNetworkDestination(uri, addresses);
    }

    internal static bool IsGlobalUnicast(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsGlobalIpv4(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsGlobalIpv4(address);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        if (WellKnownNat64Network.Contains(address))
        {
            return IsGlobalIpv4(new IPAddress(address.GetAddressBytes()[12..]));
        }

        return !BlockedIpv6Networks.Any(network => network.Contains(address))
               && !HasEmbeddedTransitionAddress(address);
    }

    private static bool IsGlobalIpv4(IPAddress address)
        => !CloudMetadataAddresses.Contains(address)
           && !BlockedIpv4Networks.Any(network => network.Contains(address));

    private static bool HasEmbeddedTransitionAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        // IPv4-compatible, 6to4, Teredo, and ISATAP forms are transition/local
        // mechanisms rather than direct global-unicast destinations. Rejecting
        // the form also prevents an embedded private IPv4 address bypass.
        var ipv4Compatible = bytes[..12].All(value => value == 0);
        var sixToFour = bytes[0] == 0x20 && bytes[1] == 0x02;
        var teredo = bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0 && bytes[3] == 0;
        var isatap = bytes[8] is 0 or 2 && bytes[9] == 0 && bytes[10] == 0x5e && bytes[11] == 0xfe;
        return ipv4Compatible || sixToFour || teredo || isatap;
    }

    private readonly record struct IpNetwork(byte[] NetworkBytes, int PrefixLength)
    {
        public static IpNetwork Parse(string address, int prefixLength)
            => new(IPAddress.Parse(address).GetAddressBytes(), prefixLength);

        public bool Contains(IPAddress address)
        {
            var addressBytes = address.GetAddressBytes();
            if (addressBytes.Length != NetworkBytes.Length)
            {
                return false;
            }

            var fullBytes = PrefixLength / 8;
            if (!addressBytes.AsSpan(0, fullBytes).SequenceEqual(NetworkBytes.AsSpan(0, fullBytes)))
            {
                return false;
            }

            var remainingBits = PrefixLength % 8;
            if (remainingBits == 0)
            {
                return true;
            }

            var mask = (byte)(0xff << (8 - remainingBits));
            return (addressBytes[fullBytes] & mask) == (NetworkBytes[fullBytes] & mask);
        }
    }
}

internal sealed record WebNetworkDestination(Uri Uri, IReadOnlyList<IPAddress> Addresses);

internal sealed class WebNetworkPolicyException(string message) : InvalidOperationException(message);
