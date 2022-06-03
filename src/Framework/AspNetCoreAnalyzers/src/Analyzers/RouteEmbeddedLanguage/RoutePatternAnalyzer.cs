// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Analyzers.WebApplicationBuilder;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class RoutePatternAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(new[]
    {
        DiagnosticDescriptors.RoutePatternIssue
    });

    public bool IsAnyStringLiteral(int rawKind)
    {
        return rawKind == (int)SyntaxKind.StringLiteralToken ||
               rawKind == (int)SyntaxKind.SingleLineRawStringLiteralToken ||
               rawKind == (int)SyntaxKind.MultiLineRawStringLiteralToken ||
               rawKind == (int)SyntaxKind.UTF8StringLiteralToken ||
               rawKind == (int)SyntaxKind.UTF8SingleLineRawStringLiteralToken ||
               rawKind == (int)SyntaxKind.UTF8MultiLineRawStringLiteralToken;
    }

    public void Analyze(SemanticModelAnalysisContext context)
    {
        var semanticModel = context.SemanticModel;
        var syntaxTree = semanticModel.SyntaxTree;
        var cancellationToken = context.CancellationToken;

        var root = syntaxTree.GetRoot(cancellationToken);
        Analyze(context, root, cancellationToken);
    }

    private void Analyze(
        SemanticModelAnalysisContext context,
        SyntaxNode node,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsNode)
            {
                Analyze(context, child.AsNode()!, cancellationToken);
            }
            else
            {
                var token = child.AsToken();
                if (!IsAnyStringLiteral(token.RawKind))
                {
                    continue;
                }

                if (!TryGetStringFormat(token, context.SemanticModel, cancellationToken, out var identifier))
                {
                    continue;
                }

                if (identifier != "Route")
                {
                    continue;
                }

                var virtualChars = AspNetCoreCSharpVirtualCharService.Instance.TryConvertToVirtualChars(token);
                if (virtualChars.IsDefault())
                {
                    continue;
                }

                var tree = RoutePatternParser.TryParse(virtualChars);
                if (tree == null)
                {
                    continue;
                }

                foreach (var diag in tree.Diagnostics)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.RoutePatternIssue,
                        Location.Create(context.SemanticModel.SyntaxTree, diag.Span),
                        DiagnosticDescriptors.RoutePatternIssue.DefaultSeverity,
                        additionalLocations: null,
                        properties: null,
                        diag.Message));
                }
            }
        }
    }

    private bool TryGetStringFormat(SyntaxToken token, SemanticModel semanticModel, CancellationToken cancellationToken, [NotNullWhen(true)]out string identifier)
    {
        if (token.Parent is not LiteralExpressionSyntax)
        {
            identifier = null;
            return false;
        }

        var container = TryFindContainer(token);
        if (container is null)
        {
            identifier = null;
            return false;
        }

        if (container.Parent.IsKind(SyntaxKind.Argument))
        {
            if (IsArgumentToParameterWithMatchingStringSyntaxAttribute(semanticModel, container.Parent, cancellationToken, out identifier))
            {
                return true;
            }
        }
        //else if (container.Parent.IsKind(SyntaxKind.AttributeArgument))
        //{
        //    if (IsArgumentToAttributeParameterWithMatchingStringSyntaxAttribute(semanticModel, container.Parent, cancellationToken, out identifier))
        //    {
        //        return true;
        //    }
        //}

        identifier = null;
        return false;
    }

    private bool IsArgumentToParameterWithMatchingStringSyntaxAttribute(
        SemanticModel semanticModel,
        SyntaxNode argument,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out string? identifier)
    {
        var parameter = FindParameterForArgument(semanticModel, argument, cancellationToken);
        return HasMatchingStringSyntaxAttribute(parameter, out identifier);
    }

    //private bool IsArgumentToAttributeParameterWithMatchingStringSyntaxAttribute(
    //    SemanticModel semanticModel,
    //    SyntaxNode argument,
    //    CancellationToken cancellationToken,
    //    [NotNullWhen(true)] out string? identifier)
    //{
    //    var parameter = FindParameterForAttributeArgument(semanticModel, argument, cancellationToken);
    //    return HasMatchingStringSyntaxAttribute(parameter, out identifier);
    //}

    private bool HasMatchingStringSyntaxAttribute(
        [NotNullWhen(true)] ISymbol? symbol,
        [NotNullWhen(true)] out string? identifier)
    {
        if (symbol != null)
        {
            foreach (var attribute in symbol.GetAttributes())
            {
                if (IsMatchingStringSyntaxAttribute(attribute, out identifier))
                {
                    return true;
                }
            }
        }

        identifier = null;
        return false;
    }

    private bool IsMatchingStringSyntaxAttribute(
        AttributeData attribute,
        [NotNullWhen(true)] out string? identifier)
    {
        identifier = null;
        if (attribute.ConstructorArguments.Length == 0)
        {
            return false;
        }

        if (attribute.AttributeClass is not
            {
                Name: "StringSyntaxAttribute",
                ContainingNamespace:
                {
                    Name: nameof(CodeAnalysis),
                    ContainingNamespace:
                    {
                        Name: nameof(System.Diagnostics),
                        ContainingNamespace:
                        {
                            Name: nameof(System),
                            ContainingNamespace.IsGlobalNamespace: true,
                        }
                    }
                }
            })
        {
            return false;
        }

        var argument = attribute.ConstructorArguments[0];
        if (argument.Kind != TypedConstantKind.Primitive || argument.Value is not string argString)
        {
            return false;
        }

        identifier = argString;
        return true;
    }

    public IParameterSymbol FindParameterForArgument(SemanticModel semanticModel, SyntaxNode argument, CancellationToken cancellationToken)
        => DetermineParameter((ArgumentSyntax)argument, semanticModel, allowParams: false, cancellationToken);

    //public IParameterSymbol FindParameterForAttributeArgument(SemanticModel semanticModel, SyntaxNode argument, CancellationToken cancellationToken)
    //    => DetermineParameter((AttributeArgumentSyntax)argument, semanticModel, allowParams: false, cancellationToken);

    /// <summary>
    /// Returns the parameter to which this argument is passed. If <paramref name="allowParams"/>
    /// is true, the last parameter will be returned if it is params parameter and the index of
    /// the specified argument is greater than the number of parameters.
    /// </summary>
    public static IParameterSymbol? DetermineParameter(
        ArgumentSyntax argument,
        SemanticModel semanticModel,
        bool allowParams = false,
        CancellationToken cancellationToken = default)
    {
        if (argument.Parent is not BaseArgumentListSyntax argumentList ||
            argumentList.Parent is null)
        {
            return null;
        }

        // Get the symbol as long if it's not null or if there is only one candidate symbol
        var symbolInfo = semanticModel.GetSymbolInfo(argumentList.Parent, cancellationToken);
        var symbol = symbolInfo.Symbol;
        if (symbol == null && symbolInfo.CandidateSymbols.Length == 1)
        {
            symbol = symbolInfo.CandidateSymbols[0];
        }

        if (symbol == null)
        {
            return null;
        }

        var parameters = GetParameters(symbol);

        // Handle named argument
        if (argument.NameColon != null && !argument.NameColon.IsMissing)
        {
            var name = argument.NameColon.Name.Identifier.ValueText;
            return parameters.FirstOrDefault(p => p.Name == name);
        }

        // Handle positional argument
        var index = argumentList.Arguments.IndexOf(argument);
        if (index < 0)
        {
            return null;
        }

        if (index < parameters.Length)
        {
            return parameters[index];
        }

        if (allowParams)
        {
            var lastParameter = parameters.LastOrDefault();
            if (lastParameter == null)
            {
                return null;
            }

            if (lastParameter.IsParams)
            {
                return lastParameter;
            }
        }

        return null;
    }

    public static ImmutableArray<IParameterSymbol> GetParameters(ISymbol? symbol)
        => symbol switch
        {
            IMethodSymbol m => m.Parameters,
            IPropertySymbol nt => nt.Parameters,
            _ => ImmutableArray<IParameterSymbol>.Empty,
        };

    private SyntaxNode? TryFindContainer(SyntaxToken token)
    {
        var node = WalkUpParentheses(GetRequiredParent(token));

        // if we're inside some collection-like initializer, find the instance actually being created. 
        if (IsAnyInitializerExpression(node.Parent, out var instance))
        {
            node = WalkUpParentheses(instance);
        }

        return node;
    }

    public static SyntaxNode GetRequiredParent(SyntaxToken token)
        => token.Parent ?? throw new InvalidOperationException("Token's parent was null");

    [return: NotNullIfNotNull("node")]
    public static SyntaxNode? WalkUpParentheses(SyntaxNode? node)
    {
        while (IsParenthesizedExpression(node?.Parent))
        {
            node = node.Parent;
        }

        return node;
    }

    public static bool IsParenthesizedExpression([NotNullWhen(true)] SyntaxNode? node)
        => node?.RawKind == (int)SyntaxKind.ParenthesizedExpression;

    public bool IsAnyInitializerExpression([NotNullWhen(true)] SyntaxNode? node, [NotNullWhen(true)] out SyntaxNode? creationExpression)
    {
        if (node is InitializerExpressionSyntax
            {
                Parent: BaseObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax
            })
        {
            creationExpression = node.Parent;
            return true;
        }

        creationExpression = null;
        return false;
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterSemanticModelAction(Analyze);
    }
}
