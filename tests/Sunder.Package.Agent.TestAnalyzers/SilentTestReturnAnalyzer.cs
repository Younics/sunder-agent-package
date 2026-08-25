using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Sunder.Package.Agent.TestAnalyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SilentTestReturnAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "SUNDERTEST001";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Test methods must not exit silently",
        "Test method '{0}' exits without an asserted outcome; use a discovery-time prerequisite attribute or assert the outcome",
        "Testing",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Bare and completed-task returns can turn unsupported prerequisites or incomplete setup into false-positive test passes.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        var method = (MethodDeclarationSyntax)context.Node;
        if (!method.AttributeLists.SelectMany(static list => list.Attributes).Any(IsTestAttribute))
        {
            return;
        }

        if (method.ExpressionBody is { Expression: var expression }
            && IsSilentCompletion(expression))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, expression.GetLocation(), method.Identifier.ValueText));
        }

        foreach (var statement in method.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (IsInsideNestedFunction(statement, method)
                || statement.Expression is not null && !IsSilentCompletion(statement.Expression))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(Rule, statement.GetLocation(), method.Identifier.ValueText));
        }
    }

    private static bool IsSilentCompletion(ExpressionSyntax expression)
    {
        if (expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "CompletedTask",
                Expression: var receiver,
            })
        {
            return false;
        }

        var receiverName = receiver.ToString();
        return string.Equals(receiverName, "Task", StringComparison.Ordinal)
               || string.Equals(receiverName, "ValueTask", StringComparison.Ordinal)
               || receiverName.EndsWith(".Task", StringComparison.Ordinal)
               || receiverName.EndsWith(".ValueTask", StringComparison.Ordinal);
    }

    private static bool IsTestAttribute(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => attribute.Name.ToString(),
        };
        return name.EndsWith("Fact", StringComparison.Ordinal)
               || name.EndsWith("FactAttribute", StringComparison.Ordinal)
               || name.EndsWith("Theory", StringComparison.Ordinal)
               || name.EndsWith("TheoryAttribute", StringComparison.Ordinal);
    }

    private static bool IsInsideNestedFunction(ReturnStatementSyntax statement, MethodDeclarationSyntax method)
    {
        for (var node = statement.Parent; node is not null && node != method; node = node.Parent)
        {
            if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                return true;
            }
        }

        return false;
    }
}
