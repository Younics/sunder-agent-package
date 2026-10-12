using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Outermost current-source inverse. Spans are authenticated insertions from
    // the owner's annotation-only audit, not replacement historical captures.
    private static string BeforeApprovedFiniteConstraints(string relative, string current)
    {
        current = BeforeApprovedIdentityConstraints(relative, current);
        return ApprovedFiniteConstraintHistory.Value.TryGetValue(relative, out var source)
            ? RemoveApprovedFiniteConstraints(source, current)
            : current;
    }

    private static string RemoveApprovedFiniteConstraints(ApprovedFiniteConstraint source, string current)
    {
        AssertProjectHash(source.AfterSha256, current, source.Path + " (approved finite constraints current boundary)");
        var previousOffset = current.Length;
        foreach (var edit in source.Edits)
        {
            Assert.Equal(2, edit.Length);
            var offset = edit[0];
            var length = edit[1];
            Assert.InRange(offset, 0, previousOffset);
            Assert.InRange(length, 1, previousOffset - offset);
            // Offsets are UTF-16 code units in LF-normalized source, descending.
            current = current.Remove(offset, length);
            previousOffset = offset;
        }
        AssertProjectHash(source.BeforeSha256, current, source.Path + " (authenticated owner pre-annotation bytes)");
        return current;
    }

    private static IReadOnlyDictionary<string, ApprovedFiniteConstraint> ReadApprovedFiniteConstraints()
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        const string fixture = "NaturalServices.approved-finite-constraints.json";
        var text = ReadNormalizedProjectText(Path.Combine(root, "tests/Sunder.Package.Agent.Tests/Fixtures", fixture));
        AssertProjectHash("9ffc2572d7f14b7a5a1543357415d8b6922c73b31dd2e7c9e9a089e3565133c7", text, fixture);
        var history = JsonSerializer.Deserialize<ApprovedFiniteConstraintBundle>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(107, history.AnnotationCount);
        Assert.Equal("f818ecf8943d8fbc7fe4c799bb9820a29a0ff57f89c3fff05186dc4984dc419b", history.OwnerBeforeSha256);
        Assert.Equal("1971c6b2893a4bbf5cff34540cbdfa0de84848822cab926165414ace3be74a53", history.OwnerAfterSha256);
        Assert.Equal("ba389562bdd130461373297d092d28589ae5f55d8e0535df812970e14d70ef9b", history.ImportedClosureSha256);
        Assert.Equal(39, history.Sources.Length);
        foreach (var source in history.Sources)
        {
            Assert.NotEmpty(source.Edits);
            _ = RemoveApprovedFiniteConstraints(source,
                BeforeApprovedIdentityConstraints(source.Path, ReadOwnedPublicHistoryInput(root, Path.Combine(root, source.Path))));
        }
        return history.Sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
    }

    private sealed record ApprovedFiniteConstraintBundle(int SchemaVersion, string OwnerBeforeSha256,
        string OwnerAfterSha256, string ImportedClosureSha256, int AnnotationCount, ApprovedFiniteConstraint[] Sources);
    private sealed record ApprovedFiniteConstraint(string Path, string BeforeSha256, string AfterSha256, int[][] Edits);
    private static class ApprovedFiniteConstraintHistory
    {
        internal static readonly IReadOnlyDictionary<string, ApprovedFiniteConstraint> Value = ReadApprovedFiniteConstraints();
    }
}
