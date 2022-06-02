// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

using static RoutePatternHelpers;
using RoutePatternToken = EmbeddedSyntaxToken<NewRoutePatternKind>;

internal partial struct NewRoutePatternParser
{
    private NewRoutePatternLexer _lexer;
    private RoutePatternToken _currentToken;

    private NewRoutePatternParser(AspNetCoreVirtualCharSequence text) : this()
    {
        _lexer = new NewRoutePatternLexer(text);

        // Get the first token.  It is allowed to have trivia on it.
        ConsumeCurrentToken();
    }

    /// <summary>
    /// Returns the latest token the lexer has produced, and then asks the lexer to 
    /// produce the next token after that.
    /// </summary>
    /// <param name="allowTrivia">Whether or not trivia is allowed on the next token
    /// produced.  In the .NET parser trivia is only allowed on a few constructs,
    /// and our parser mimics that behavior.  Note that even if trivia is allowed,
    /// the type of trivia that can be scanned depends on the current RegexOptions.
    /// For example, if <see cref="RegexOptions.IgnorePatternWhitespace"/> is currently
    /// enabled, then '#...' comments are allowed.  Otherwise, only '(?#...)' comments
    /// are allowed.</param>
    private RoutePatternToken ConsumeCurrentToken()
    {
        var previous = _currentToken;
        _currentToken = _lexer.ScanNextToken();
        return previous;
    }

    /// <summary>
    /// Given an input text, and set of options, parses out a fully representative syntax tree 
    /// and list of diagnostics.  Parsing should always succeed, except in the case of the stack 
    /// overflowing.
    /// </summary>
    public static NewRoutePatternTree? TryParse(AspNetCoreVirtualCharSequence text)
    {
        // Parse the tree once, to figure out the capture groups.  These are needed
        // to then parse the tree again, as the captures will affect how we interpret
        // certain things (i.e. escape references) and what errors will be reported.
        //
        // This is necessary as .NET regexes allow references to *future* captures.
        // As such, we don't know when we're seeing a reference if it's to something
        // that exists or not.
        var tree1 = new NewRoutePatternParser(text).ParseTree();

        return tree1;
    }

    private NewRoutePatternTree ParseTree()
    {
        var rootParts = ParseRootParts();

        // Most callers to ParseAlternatingSequences are from group constructs.  As those
        // constructs will have already consumed the open paren, they don't want this sub-call
        // to consume through close-paren tokens as they want that token for themselves.
        // However, we're the topmost call and have not consumed an open paren.  And, we want
        // this call to consume all the way to the end, eating up excess close-paren tokens that
        // are encountered.
        //var expression = ParseAlternatingSequencesWorker(consumeCloseParen: true, isConditional: false);
        Debug.Assert(_lexer.Position == _lexer.Text.Length);
        Debug.Assert(_currentToken.Kind == NewRoutePatternKind.EndOfFile);

        var root = new RoutePatternCompilationUnit(rootParts, _currentToken);

        var routeParameters = new Dictionary<string, RouteParameter>(StringComparer.OrdinalIgnoreCase);
        var seenDiagnostics = new HashSet<EmbeddedDiagnostic>();
        var diagnostics = new List<EmbeddedDiagnostic>();

        CollectDiagnostics(root, seenDiagnostics, diagnostics);
        ValidateNoConsecutiveParameters(root, diagnostics);
        ValidateCatchAllParameters(root, diagnostics);
        ValidateParameterParts(root, diagnostics, routeParameters);

        return new NewRoutePatternTree(_lexer.Text, root, diagnostics.ToImmutable(), routeParameters.ToImmutableDictionary());
    }

    private static void ValidateCatchAllParameters(RoutePatternCompilationUnit root, List<EmbeddedDiagnostic> diagnostics)
    {
        RoutePatternParameterNode? catchAllParameterNode = null;
        foreach (var part in root)
        {
            if (part.Kind == NewRoutePatternKind.Segment)
            {
                if (catchAllParameterNode == null)
                {
                    foreach (var segmentPart in part.Node)
                    {
                        if (segmentPart.Kind == NewRoutePatternKind.Parameter)
                        {
                            foreach (var parameterParts in segmentPart.Node)
                            {
                                if (parameterParts.Kind == NewRoutePatternKind.CatchAll)
                                {
                                    catchAllParameterNode = (RoutePatternParameterNode)segmentPart.Node;
                                    if (part.Node.ChildCount > 1)
                                    {
                                        diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_CannotHaveCatchAllInMultiSegment, catchAllParameterNode.GetSpan()));
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_CatchAllMustBeLast, catchAllParameterNode.GetSpan()));
                    break;
                }
            }
        }
    }

    private static void ValidateNoConsecutiveParameters(RoutePatternCompilationUnit root, List<EmbeddedDiagnostic> diagnostics)
    {
        NewRoutePatternKind? previousNodeKind = null;
        foreach (var part in root)
        {
            if (part.Kind == NewRoutePatternKind.Segment)
            {
                foreach (var segmentPart in part.Node)
                {
                    if (previousNodeKind == NewRoutePatternKind.Parameter && segmentPart.Kind == NewRoutePatternKind.Parameter)
                    {
                        var parameterNode = (RoutePatternParameterNode)segmentPart.Node;
                        diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_CannotHaveConsecutiveParameters, parameterNode.GetSpan()));
                    }
                    previousNodeKind = segmentPart.Kind;
                }
                previousNodeKind = null;
            }
        }
    }

    private static void ValidateParameterParts(RoutePatternCompilationUnit root, List<EmbeddedDiagnostic> diagnostics, Dictionary<string, RouteParameter> routeParameters)
    {
        foreach (var part in root)
        {
            if (part.Kind == NewRoutePatternKind.Segment)
            {
                foreach (var segmentPart in part.Node)
                {
                    if (segmentPart.Kind == NewRoutePatternKind.Parameter)
                    {
                        var parameterNode = (RoutePatternParameterNode)segmentPart.Node;
                        var hasOptional = false;
                        var hasCatchAll = false;
                        var encodeSlashes = true;
                        string? name = null;
                        string? defaultValue = null;
                        List<string> policies = new List<string>();
                        foreach (var parameterPart in parameterNode)
                        {
                            switch (parameterPart.Kind)
                            {
                                case NewRoutePatternKind.ParameterName:
                                    var parameterNameNode = (RoutePatternNameParameterPartNode)parameterPart.Node;
                                    if (!parameterNameNode.ParameterNameToken.IsMissing)
                                    {
                                        name = parameterNameNode.ParameterNameToken.Value.ToString();
                                    }
                                    break;
                                case NewRoutePatternKind.Optional:
                                    hasOptional = true;
                                    break;
                                case NewRoutePatternKind.DefaultValue:
                                    var defaultValueNode = (RoutePatternDefaultValueParameterPartNode)parameterPart.Node;
                                    if (!defaultValueNode.DefaultValueToken.IsMissing)
                                    {
                                        defaultValue = defaultValueNode.DefaultValueToken.Value.ToString();
                                    }
                                    break;
                                case NewRoutePatternKind.CatchAll:
                                    var catchAllNode = (RoutePatternCatchAllParameterPartNode)parameterPart.Node;
                                    encodeSlashes = catchAllNode.AsteriskToken.VirtualChars.Length == 1;
                                    hasCatchAll = true;
                                    break;
                                case NewRoutePatternKind.ParameterPolicy:
                                    policies.Add(parameterPart.Node.ToString());
                                    break;
                            }
                        }

                        var routeParameter = new RouteParameter(name, encodeSlashes, defaultValue, hasOptional, hasCatchAll, policies.ToImmutable());
                        if (routeParameter.DefaultValue != null && routeParameter.IsOptional)
                        {
                            diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_OptionalCannotHaveDefaultValue, parameterNode.GetSpan()));
                        }
                        if (routeParameter.IsCatchAll && routeParameter.IsOptional)
                        {
                            diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_CatchAllCannotBeOptional, parameterNode.GetSpan()));
                        }

                        if (routeParameter.Name != null)
                        {
                            if (!routeParameters.ContainsKey(routeParameter.Name))
                            {
                                routeParameters.Add(routeParameter.Name, routeParameter);
                            }
                            else
                            {
                                diagnostics.Add(new EmbeddedDiagnostic(Resources.FormatTemplateRoute_RepeatedParameter(routeParameter.Name), parameterNode.GetSpan()));
                            }
                        }
                    }
                }
            }
        }
    }

    private void CollectDiagnostics(NewRoutePatternNode node, HashSet<EmbeddedDiagnostic> seenDiagnostics, List<EmbeddedDiagnostic> diagnostics)
    {
        foreach (var child in node)
        {
            if (child.IsNode)
            {
                CollectDiagnostics(child.Node, seenDiagnostics, diagnostics);
            }
            else
            {
                var token = child.Token;
                AddUniqueDiagnostics(seenDiagnostics, token.Diagnostics, diagnostics);
            }
        }
    }

    /// <summary>
    /// It's very common to have duplicated diagnostics.  For example, consider "((". This will
    /// have two 'missing )' diagnostics, both at the end.  Reporting both isn't helpful, so we
    /// filter duplicates out here.
    /// </summary>
    private static void AddUniqueDiagnostics(
        HashSet<EmbeddedDiagnostic> seenDiagnostics, ImmutableArray<EmbeddedDiagnostic> from, List<EmbeddedDiagnostic> to)
    {
        foreach (var diagnostic in from)
        {
            if (seenDiagnostics.Add(diagnostic))
            {
                to.Add(diagnostic);
            }
        }
    }

    private ImmutableArray<NewRoutePatternRootPartNode> ParseRootParts()
    {
        var result = new List<NewRoutePatternRootPartNode>();

        while (_currentToken.Kind != NewRoutePatternKind.EndOfFile)
        {
            result.Add(ParseRootPart());
        }

        return result.ToImmutable();
    }

    private NewRoutePatternRootPartNode ParseRootPart()
        => _currentToken.Kind switch
        {
            NewRoutePatternKind.SlashToken => ParseSegmentSeperator(),
            _ => ParseSegment(),
        };

    private RoutePatternSegmentNode ParseSegment()
    {
        var result = new List<NewRoutePatternNode>();

        while (_currentToken.Kind != NewRoutePatternKind.EndOfFile &&
            _currentToken.Kind != NewRoutePatternKind.SlashToken)
        {
            result.Add(ParsePart());
        }

        return new(result.ToImmutable());
    }

    private NewRoutePatternSegmentPartNode ParsePart()
    {
        if (_currentToken.Kind == NewRoutePatternKind.OpenBraceToken)
        {
            var openBraceToken = _currentToken;

            ConsumeCurrentToken();

            if (_currentToken.Kind != NewRoutePatternKind.OpenBraceToken)
            {
                return ParseParameter(openBraceToken);
            }
            else
            {
                MoveBackBeforePreviousScan();
            }
        }

        return ParseLiteral();
    }
        //=> _currentToken.Kind switch
        //{
        //    NewRoutePatternKind.OpenBraceToken => ParseParameter(),
        //    //NewRoutePatternKind.OpenBracketToken => ParseReplacement(),
        //    _ => ParseLiteral(),
        //};

    private RoutePatternLiteralNode ParseLiteral()
    {
        MoveBackBeforePreviousScan();

        var literal = _lexer.TryScanLiteral();

        ConsumeCurrentToken();

        // A token must be returned because we've already checked the first character.
        return new(literal.Value);
    }

    private void MoveBackBeforePreviousScan()
    {
        if (_currentToken.Kind != NewRoutePatternKind.EndOfFile)
        {
            // Move back to un-consume whatever we just consumed.
            _lexer.Position--;
        }
    }

    //private RoutePatternSegmentSeperatorNode ParseReplacement()
    //{
    //    throw new NotImplementedException();
    //}

    private RoutePatternParameterNode ParseParameter(RoutePatternToken openBraceToken)
    {
        var result = new RoutePatternParameterNode(
            openBraceToken,
            ParseParameterParts(),
            ConsumeToken(NewRoutePatternKind.CloseBraceToken, Resources.TemplateRoute_MismatchedParameter));

        return result;
    }

    private RoutePatternToken ConsumeToken(NewRoutePatternKind kind, string? error)
    {
        if (_currentToken.Kind == kind)
        {
            return ConsumeCurrentToken();
        }

        var result = CreateMissingToken(kind);
        if (error == null)
        {
            return result;
        }

        return result.AddDiagnosticIfNone(new EmbeddedDiagnostic(error, GetTokenStartPositionSpan(_currentToken)));
    }

    private ImmutableArray<NewRoutePatternParameterPartNode> ParseParameterParts()
    {
        var parts = new List<NewRoutePatternParameterPartNode>();

        // Catch-all, e.g. {*name}
        if (_currentToken.Kind == NewRoutePatternKind.AsteriskToken)
        {
            var firstAsteriskToken = _currentToken;
            ConsumeCurrentToken();

            // Unescaped catch-all, e.g. {**name}
            if (_currentToken.Kind == NewRoutePatternKind.AsteriskToken)
            {
                parts.Add(new RoutePatternCatchAllParameterPartNode(
                    CreateToken(
                        NewRoutePatternKind.AsteriskToken,
                        AspNetCoreVirtualCharSequence.FromBounds(firstAsteriskToken.VirtualChars, _currentToken.VirtualChars))));
                ConsumeCurrentToken();
            }
            else
            {
                parts.Add(new RoutePatternCatchAllParameterPartNode(firstAsteriskToken));
            }
        }

        MoveBackBeforePreviousScan();

        var parameterName = _lexer.TryScanParameterName();
        if (parameterName != null)
        {
            parts.Add(new RoutePatternNameParameterPartNode(parameterName.Value));
        }
        else
        {
            if (_currentToken.Kind != NewRoutePatternKind.EndOfFile)
            {
                parts.Add(new RoutePatternNameParameterPartNode(
                    CreateMissingToken(NewRoutePatternKind.ParameterNameToken).AddDiagnosticIfNone(
                        new EmbeddedDiagnostic(Resources.FormatTemplateRoute_InvalidParameterName(""), _currentToken.GetFullSpan().Value))));
            }
        }

        ConsumeCurrentToken();

        // Parameter policy, e.g. {name:int}
        while (_currentToken.Kind != NewRoutePatternKind.EndOfFile)
        {
            switch (_currentToken.Kind)
            {
                case NewRoutePatternKind.ColonToken:
                    parts.Add(ParsePolicy());
                    break;
                case NewRoutePatternKind.QuestionMarkToken:
                    parts.Add(new RoutePatternOptionalParameterPartNode(ConsumeCurrentToken()));
                    break;
                case NewRoutePatternKind.EqualsToken:
                    parts.Add(ParseDefaultValue());
                    break;
                case NewRoutePatternKind.CloseBraceToken:
                default:
                    return parts.ToImmutableArray();
            }
        }

        return parts.ToImmutableArray();
    }

    private RoutePatternDefaultValueParameterPartNode ParseDefaultValue()
    {
        var equalsToken = _currentToken;
        var defaultValue = _lexer.TryScanDefaultValue() ?? CreateMissingToken(NewRoutePatternKind.DefaultValueToken);
        ConsumeCurrentToken();
        return new(equalsToken, defaultValue);
    }

    private RoutePatternPolicyParameterPartNode ParsePolicy()
    {
        var colonToken = ConsumeCurrentToken();

        var fragments = new List<NewRoutePatternNode>();
        while (_currentToken.Kind != NewRoutePatternKind.EndOfFile &&
            _currentToken.Kind != NewRoutePatternKind.CloseBraceToken &&
            _currentToken.Kind != NewRoutePatternKind.ColonToken &&
            _currentToken.Kind != NewRoutePatternKind.QuestionMarkToken &&
            _currentToken.Kind != NewRoutePatternKind.EqualsToken)
        {
            MoveBackBeforePreviousScan();

            if (_currentToken.Kind == NewRoutePatternKind.OpenParenToken)
            {
                var openParenPosition = ConsumeCurrentToken();
                var escapedPolicyFragment = _lexer.TryScanEscapedPolicyFragment();
                if (escapedPolicyFragment != null)
                {
                    ConsumeCurrentToken();

                    fragments.Add(new RoutePatternPolicyFragmentEscapedNode(
                        openParenPosition,
                        escapedPolicyFragment.Value,
                        _currentToken.Kind == NewRoutePatternKind.EndOfFile
                            ? CreateMissingToken(NewRoutePatternKind.CloseParenToken)
                            : ConsumeCurrentToken()));
                    continue;
                }
            }

            var policyFragment = _lexer.TryScanUnescapedPolicyFragment();
            if (policyFragment == null)
            {
                break;
            }

            fragments.Add(new RoutePatternPolicyFragment(policyFragment.Value));
            ConsumeCurrentToken();
        }

        return new(colonToken, fragments.ToImmutableArray());
    }

    //private RoutePatternToken ParsePolicyArgument()
    //{
    //    var policyArgument = _lexer.TryScanPolicyArgument();
    //    if (policyArgument == null)
    //    {
    //        policyArgument = CreateMissingToken(NewRoutePatternKind.PolicyArgumentToken);
    //        //if (_currentToken.Kind != NewRoutePatternKind.EndOfFile)
    //        //{
    //        //    policyArgument = policyName.Value.AddDiagnosticIfNone(new EmbeddedDiagnostic("", _currentToken.GetFullSpan().Value));
    //        //}
    //    }

    //    ConsumeCurrentToken();

    //    return policyArgument.Value;
    //}

    private RoutePatternSegmentSeperatorNode ParseSegmentSeperator()
        => new(ConsumeCurrentToken());

    private TextSpan GetTokenStartPositionSpan(RoutePatternToken token)
    {
        return token.Kind == NewRoutePatternKind.EndOfFile
            ? new TextSpan(_lexer.Text.Last().Span.End, 0)
            : new TextSpan(token.VirtualChars[0].Span.Start, 0);
    }
}
