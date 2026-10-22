using System.Text.RegularExpressions;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
internal static class BuilderJobText
{
    internal static string Safe(string value, int maximum)
    {
        if (value.Length > maximum) value = "[Earlier output truncated]\n" + value[^maximum..];
        // Redact after joining output chunks, so a secret split across progress callbacks is still removed.
        value = Regex.Replace(value, @"(?i)(\b(?:authorization\s*:\s*bearer|bearer)\s+)[^\s\""']+", "$1[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        value = Regex.Replace(value, @"(?i)((?:password|passwd|secret|token|api[_-]?key|_authToken|access[_-]?key)\s*[=:]\s*[\""']?)[^\s&\""']+", "$1[redacted]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        value = Regex.Replace(value, @"(https?://)[^\s/@]+:[^\s/@]+@", "$1[redacted]@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        value = new string(value.Where(character => !char.IsControl(character) || character is '\n' or '\r' or '\t').ToArray());
        return value.Length <= maximum ? value : value[^maximum..];
    }
}
