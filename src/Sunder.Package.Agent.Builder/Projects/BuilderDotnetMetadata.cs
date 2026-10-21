using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderDotnetMetadata
{
    private static IEnumerable<(string Path, CompilationUnitSyntax Tree)> Trees(BuilderProjectSource source) => source.Files
        .Where(file => file.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !BuilderProjectSource.OutputFolders.Contains(file.Key.Split('/')[0]))
        .Select(file => (file.Key, CSharpSyntaxTree.ParseText(file.Value.Text).GetCompilationUnitRoot()));

    internal static BuilderSourceMetadata Inspect(BuilderProjectSource source, string entryPoint)
    {
        var project = ReadXml(source.Files[Path.GetFileName(entryPoint)].Text);
        if (project.Root?.Name.LocalName != "Project" || !project.Descendants().Any(element => element.Name.LocalName == "SunderPackageProject" && element.Value == "true"
            || element.Name.LocalName is "PackageReference" or "ProjectReference" && ((string?)element.Attribute("Include"))?.Contains("Sunder.", StringComparison.Ordinal) == true)
            && !project.Descendants().Any(element => element.Name.LocalName == "Import" && ((string?)element.Attribute("Project"))?.Contains("Sunder.", StringComparison.Ordinal) == true))
            throw new NotSunderProjectException("This is not an authored Sunder package project.");
        if (project.Descendants().Any(element => element.Name.LocalName == "SunderPackageRole" && element.Value is "app" or "worker" or "runtime"))
            throw new NotSunderProjectException("Select the canonical authored project, not a generated role project.");
        var trees = Trees(source).ToArray();
        var declarations = trees.SelectMany(file => file.Tree.AttributeLists.Where(list => list.Target?.Identifier.ValueText == "assembly")
            .SelectMany(list => list.Attributes).Where(attribute => IsAttribute(attribute, "SunderPackage", "Packaging"))).ToArray();
        if (declarations.Length == 0) throw new NotSunderProjectException("No authored assembly SunderPackage declaration was found.");
        if (declarations.Length != 1) throw new NotSunderProjectException("The source contains multiple package declarations; select an unambiguous canonical project.");
        var attribute = declarations[0];
        var id = NamedLiteral(attribute, "Id"); var name = NamedLiteral(attribute, "Name");
        string? reason = null;
        if (id is null || name is null) reason = "SunderPackage Id and Name must be direct string literals; computed constants are read-only in the source editor.";
        else
        {
            try { BuilderProjectSource.ValidateId(id); BuilderProjectSource.ValidateName(name); }
            catch (ArgumentException error) { reason = error.Message; }
        }
        if (trees.Any(file => file.Tree.ContainsDiagnostics || file.Tree.ContainsDirectives)) reason ??= "C# syntax errors or conditional directives require manual metadata editing; no evaluated build is used.";
        if (trees.Any(file => file.Tree.DescendantNodes().OfType<TypeDeclarationSyntax>().Any(type => type.Identifier.ValueText is "SunderPackage" or "SunderPackageAttribute")))
            reason ??= "A local type shadows SunderPackage; the source editor cannot establish attribute identity without compilation.";
        if (source.Files.Keys.Count(path => !path.Contains('/') && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) != 1)
            reason ??= "Multiple projects share this source folder. Move metadata to an unambiguous project before editing.";
        if (project.Descendants().Any(element => element.Name.LocalName == "Compile" || element.Name.LocalName == "EnableDefaultCompileItems" && element.Value != "true"))
            reason ??= "Custom Compile includes/excludes are outside the bounded source editor; default local C# source membership is required.";
        var imports = project.Descendants().Where(element => element.Name.LocalName == "Import").Select(element => ((string?)element.Attribute("Project"))?.Replace('\\', '/'));
        var idReason = imports.Any(path => path is not ("Views/Avalonia.PackageReferences.props" or "Stacks/Stacks.PackageReferences.props"))
            ? "Custom MSBuild imports may supply identities. Automatic ID editing supports only the standard starter reference imports." : null;
        if (source.InheritedBuildCustomization is { } inherited)
            idReason ??= $"Implicit build customization '{inherited}' is not evaluated. Automatic ID editing requires a self-contained starter project without Directory.Build imports.";
        if (project.Descendants().Any(element => element.Name.LocalName is "SunderPackageId" or "SunderPackageFamilyId" or "SunderOwnedContractsPath" or "Target" or "UsingTask" or "AssemblyAttribute"))
            idReason ??= "Project-authored identity, baseline overrides, or custom build targets require manual editing.";
        foreach (var file in trees)
        {
            if (file.Tree.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(node => node.Alias is not null && node.Name?.ToString().StartsWith("Sunder.Sdk.", StringComparison.Ordinal) == true))
                idReason ??= "Aliased SDK authoring attributes require semantic review; the bounded ID editor accepts direct starter attributes only.";
            foreach (var service in file.Tree.DescendantNodes().OfType<AttributeSyntax>().Where(node => IsAttribute(node, "SunderService", "Services")))
            {
                if (service.ArgumentList?.Arguments.Any(argument => argument.NameEquals?.Name.Identifier.ValueText != "Exposure"
                    || argument.Expression.ToString() is not ("SunderServiceExposure.Package" or "Sunder.Sdk.Services.SunderServiceExposure.Package")) == true)
                    idReason ??= "Service identity/options are customized. Automatic ID editing supports inferred private starter service identities only.";
                if (service.Parent?.Parent is not ClassDeclarationSyntax { Identifier.ValueText: "SampleService" })
                    idReason ??= "Automatic .NET ID editing is bounded to the private SampleService starter; custom service identities require manual review.";
            }
            foreach (var view in file.Tree.DescendantNodes().OfType<AttributeSyntax>().Where(node => IsAttribute(node, "SunderPackageView", "Packaging")))
                if (view.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal || literal.Token.ValueText != id + ".default")
                    idReason ??= "A custom/computed view ID is outside the starter identity editor (expected packageId.default).";
            var editable = IdentityLiteralSpans(file.Tree).ToHashSet();
            foreach (var literal in file.Tree.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(node => node.IsKind(SyntaxKind.StringLiteralExpression)))
                if (id is not null && (literal.Token.ValueText == id || literal.Token.ValueText.StartsWith(id + ".", StringComparison.Ordinal)) && !editable.Contains(literal.SpanStart))
                    idReason ??= $"Unrecognized package identity literal in {file.Path}; automatic ID editing cannot safely rewrite custom identities.";
        }
        foreach (var file in source.Files.Where(file => file.Key.EndsWith(".props", StringComparison.Ordinal) || file.Key.EndsWith(".targets", StringComparison.Ordinal)))
        {
            var imported = ReadXml(file.Value.Text);
            if (imported.Descendants().Any(element => element.Name.LocalName is "Import" or "Compile" or "SunderPackageId" or "SunderPackageFamilyId" or "SunderOwnedContractsPath" or "Target" or "UsingTask" or "AssemblyAttribute"))
                idReason ??= $"Custom source/identity declarations in '{file.Key}' require manual ID review.";
        }
        return new(name ?? Path.GetFileNameWithoutExtension(entryPoint), id ?? string.Empty, reason, idReason);
    }

    internal static Dictionary<string, byte[]> Plan(BuilderProjectSource source, BuilderProjectInspection expected, string name, string id)
    {
        var changed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Trees(source))
        {
            var edits = new List<BuilderTextEdit>();
            foreach (var attribute in file.Tree.DescendantNodes().OfType<AttributeSyntax>())
            {
                if (IsAttribute(attribute, "SunderPackage", "Packaging") && attribute.Parent is AttributeListSyntax { Target.Identifier.ValueText: "assembly" })
                {
                    ChangeNamed("Name", name); ChangeNamed("Id", id);
                }
                if (IsAttribute(attribute, "SunderPackageView", "Packaging"))
                {
                    var args = attribute.ArgumentList!.Arguments;
                    if (args[0].Expression is LiteralExpressionSyntax viewId && viewId.Token.ValueText == expected.PackageId + ".default")
                    { BuilderProjectSource.ValidateId(id + ".default"); Change(viewId, id + ".default"); }
                    if (args.Count > 1 && args[1].Expression is LiteralExpressionSyntax label && label.Token.ValueText == expected.DisplayName) Change(label, name);
                }
                void ChangeNamed(string key, string value)
                {
                    var literal = attribute.ArgumentList?.Arguments.SingleOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == key)?.Expression as LiteralExpressionSyntax;
                    if (literal is not null) Change(literal, value);
                }
            }
            if (id != expected.PackageId)
            {
                // Replace only syntax names in the three owned generated namespaces, never CLR namespaces or strings.
                foreach (var area in new[] { "Services", "Models", "Internal" })
                {
                    var before = GeneratedNamespace(expected.PackageId, area); var after = GeneratedNamespace(id, area);
                    foreach (var node in file.Tree.DescendantNodes().OfType<NameSyntax>().Where(node => node.ToString() == before || node.ToString() == "global::" + before))
                        edits.Add(new(node.SpanStart, node.Span.Length, node.ToString().StartsWith("global::", StringComparison.Ordinal) ? "global::" + after : after));
                }
                BuilderProjectSource.ValidateId(id + ".sample"); BuilderProjectSource.ValidateId(id + ".sample.provider");
            }
            var original = source.Files[file.Path]; var text = BuilderTextEdit.Apply(original.Text, edits);
            if (text != original.Text) changed.Add(file.Path, original.Encode(text));
            void Change(LiteralExpressionSyntax literal, string value)
            {
                if (literal.Token.ValueText != value) edits.Add(new(literal.SpanStart, literal.Span.Length, SyntaxFactory.Literal(value).Text));
            }
        }
        const string xaml = "Views/DefaultPackageView.axaml";
        if (name != expected.DisplayName && source.Files.TryGetValue(xaml, out var view))
        {
            var document = ReadXml(view.Text);
            var labels = document.Descendants().Where(element => element.Name.LocalName == "TextBlock" && (string?)element.Attribute("FontSize") == "24"
                && (string?)element.Attribute("FontWeight") == "SemiBold" && (string?)element.Attribute("Text") == expected.DisplayName).ToArray();
            if (labels.Length == 1)
            {
                labels[0].SetAttributeValue("Text", name);
                changed[xaml] = view.Encode(document.ToString(SaveOptions.DisableFormatting));
            }
        }
        return changed;
    }

    private static IEnumerable<int> IdentityLiteralSpans(CompilationUnitSyntax tree) => tree.DescendantNodes().OfType<AttributeSyntax>()
        .Where(node => IsAttribute(node, "SunderPackage", "Packaging") || IsAttribute(node, "SunderPackageView", "Packaging"))
        .SelectMany(node => node.ArgumentList?.Arguments.Where(argument => argument.NameEquals?.Name.Identifier.ValueText == "Id" || argument.NameEquals is null).Select(argument => argument.Expression.SpanStart) ?? []);
    internal static bool IsAttribute(AttributeSyntax attribute, string name, string area)
    {
        var text = attribute.Name.ToString();
        if (text.StartsWith("global::", StringComparison.Ordinal)) text = text["global::".Length..];
        if (text == $"Sunder.Sdk.{area}.{name}" || text == $"Sunder.Sdk.{area}.{name}Attribute") return true;
        return (text == name || text == name + "Attribute") && attribute.SyntaxTree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>().Any(node => node.Alias is null && node.Name?.ToString() == $"Sunder.Sdk.{area}");
    }
    private static string? NamedLiteral(AttributeSyntax attribute, string key) => attribute.ArgumentList?.Arguments.SingleOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == key)?.Expression is LiteralExpressionSyntax literal
        && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;
    internal static string GeneratedNamespace(string id, string area)
    {
        var stem = string.Join("_", id.Split('.').Select(segment => char.ToUpperInvariant(segment[0]) + segment[1..]));
        return "Sunder.Generated.Packages." + (char.IsDigit(stem[0]) ? "_" : "") + stem + "_" + area;
    }
    internal static XDocument ReadXml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
}

internal sealed record BuilderTextEdit(int Start, int Length, string Text)
{
    internal static string Apply(string text, IEnumerable<BuilderTextEdit> edits)
    {
        var end = text.Length;
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
        {
            if (edit.Start < 0 || edit.Start + edit.Length > end) throw new InvalidDataException("Metadata edit spans overlap.");
            text = text[..edit.Start] + edit.Text + text[(edit.Start + edit.Length)..]; end = edit.Start;
        }
        return text;
    }
}
