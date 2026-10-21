using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderJobValidation
{
    internal static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static BuilderJobRequest Normalize(BuilderJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        BuilderContent.ValidateRequest(request);
        if (request.Kind is not (BuilderProjectKind.Dotnet or BuilderProjectKind.Node)) throw new ArgumentException("Unknown project kind.");
        if (request.Operation is not ("ensure-sdk" or "create" or "setup" or "build" or "archive" or "watch" or "save-metadata" or "repair-sdk" or "remove-sdk"))
            throw new ArgumentException("Unknown Builder job operation.");
        var id = request.Id ?? Guid.NewGuid().ToString("N");
        ValidateId(id);
        var entry = request.EntryPoint is null ? request.Expected?.EntryPoint : request.EntryPoint;
        var folder = request.ProjectFolder ?? request.Expected?.ProjectFolder;
        if (entry is not null)
        {
            entry = Absolute(entry, "Project entry point");
            var parent = Path.GetDirectoryName(entry)!;
            if (folder is not null && !Same(Absolute(folder, "Project folder"), parent)) throw new ArgumentException("Project folder must contain the selected entry point directly.");
            folder = parent;
        }
        if (folder is not null) folder = Absolute(folder, "Project folder");
        if (request.Operation == "create")
        {
            if (folder is null || entry is not null || request.Expected is not null) throw new ArgumentException("Creation requires a destination folder without an existing entry point. Use setup to retry an existing project.");
            BuilderProjectSource.ValidateName(request.DisplayName!);
            BuilderProjectSource.ValidateId(request.PackageId!);
            BuilderProjectSource.ValidateId(request.PackageId + (request.Kind == BuilderProjectKind.Node ? ".messages.provider" : ".sample.provider"));
        }
        if (request.Operation is "setup" or "build" or "archive" or "watch" or "save-metadata")
        {
            if (entry is null) throw new ArgumentException("Select an explicit project entry point before starting this operation.");
            if (request.Kind == BuilderProjectKind.Node ? Path.GetFileName(entry) != "sunder.package.json" : !entry.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The entry point does not match the project kind.");
        }
        if (request.Expected is { } expected && (expected.Kind != request.Kind || !Same(Absolute(expected.EntryPoint, "Expected entry point"), entry!) ||
            !Same(Absolute(expected.ProjectFolder, "Expected project folder"), folder!))) throw new ArgumentException("The expected inspection describes a different project.");
        if (request.Operation == "save-metadata")
        {
            if (request.Expected is null) throw new ArgumentException("Saving metadata requires the last source inspection.");
            BuilderProjectSource.ValidateName(request.DisplayName!);
            BuilderProjectSource.ValidateId(request.PackageId!);
        }
        if (request.Operation is "repair-sdk" or "remove-sdk" && request.Installation is null) throw new ArgumentException("Select the managed toolchain installation.");
        if (request.Installation is { } installation && (installation.Kind != request.Kind || !BuilderToolchainPolicy.IsSupported(installation.Kind, installation.Version)))
            throw new ArgumentException("The selected installation does not match the supported project toolchain.");
        return request with { Id = id, ProjectFolder = folder, EntryPoint = entry };
    }

    internal static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length is < 8 or > 80 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Job IDs must contain 8–80 ASCII letters, digits, hyphens or underscores.");
    }

    internal static string Absolute(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException(label + " must be a bounded absolute local path.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.GetPathRoot(full)) throw new ArgumentException(label + " must not be a filesystem root.");
        return full;
    }

    internal static bool Same(string left, string right) => string.Equals(left, right, PathComparison);
    internal static bool Overlaps(string left, string right) => Same(left, right) || left.StartsWith(right + Path.DirectorySeparatorChar, PathComparison)
        || right.StartsWith(left + Path.DirectorySeparatorChar, PathComparison);
    internal static string Fingerprint(BuilderJobRequest request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
}

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
