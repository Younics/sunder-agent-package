using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Sunder.Package.Agent.Shared.Storage;

internal static class CurrentPackageStorageKey
{
    private const int MaximumKeyLength = 256;
    private const int Sha256HexLength = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string Create(string prefix, int version, string opaqueId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(opaqueId);
        if (prefix.Length > MaximumKeyLength
            || prefix.Any(static character => !char.IsAsciiLetterOrDigit(character)
                                              && character is not ('.' or '-' or '_')))
        {
            throw new ArgumentException("Package storage key prefixes must be portable ASCII tokens.", nameof(prefix));
        }
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Package storage key versions must be positive.");
        }
        if (opaqueId.Length == 0)
        {
            throw new ArgumentException("Opaque package storage identifiers must not be empty.", nameof(opaqueId));
        }

        var versionedPrefix = string.Concat(
            prefix,
            ".v",
            version.ToString(CultureInfo.InvariantCulture),
            ".");
        if (versionedPrefix.Length + Sha256HexLength > MaximumKeyLength)
        {
            throw new ArgumentException(
                $"The versioned package storage key prefix leaves insufficient room within the {MaximumKeyLength}-character key limit.",
                nameof(prefix));
        }

        byte[] opaqueBytes;
        try
        {
            opaqueBytes = StrictUtf8.GetBytes(opaqueId);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Opaque package storage identifiers must contain valid Unicode.", nameof(opaqueId), exception);
        }

        return versionedPrefix + Convert.ToHexString(SHA256.HashData(opaqueBytes)).ToLowerInvariant();
    }
}
