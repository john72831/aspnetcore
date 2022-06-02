// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common;
using Microsoft.CodeAnalysis.EmbeddedLanguages.VirtualChars;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

using static RoutePatternHelpers;
using RoutePatternToken = EmbeddedSyntaxToken<NewRoutePatternKind>;

/// <summary>
/// Produces a <see cref="RoutePatternTree"/> from a sequence of <see cref="VirtualChar"/> characters.
///
/// Importantly, this parser attempts to replicate diagnostics with almost the exact same text
/// as the native .NET regex parser.  This is important so that users get an understandable
/// experience where it appears to them that this is all one cohesive system and that the IDE
/// will let them discover and fix the same issues they would encounter when previously trying
/// to just compile and execute these regexes.
/// </summary>
/// <remarks>
/// Invariants we try to maintain (and should consider a bug if we do not): l 1. If the .NET
/// regex parser does not report an error for a given pattern, we should not either. it would be
/// very bad if we told the user there was something wrong with there pattern when there really
/// wasn't.
///
/// 2. If the .NET regex parser does report an error for a given pattern, we should either not
/// report an error (not recommended) or report the same error at an appropriate location in the
/// pattern.  Not reporting the error can be confusing as the user will think their pattern is
/// ok, when it really is not.  However, it can be acceptable to do this as it's not telling
/// them that something is actually wrong, and it may be too difficult to find and report the
/// same error.  Note: there is only one time we do this in this parser (see the deviation
/// documented in <see cref="ParsePossibleEcmascriptBackreferenceEscape"/>).
///
/// Note1: "report the same error" means that we will attempt to report the error using the same
/// text the .NET regex parser uses for its error messages.  This is so that the user is not
/// confused when they use the IDE vs running the regex by getting different messages for the
/// same issue.
///
/// Note2: the above invariants make life difficult at times.  This happens due to the fact that
/// the .NET parser is multi-pass.  Meaning it does a first scan (which may report errors), then
/// does the full parse.  This means that it might report an error in a later location during
/// the initial scan than it would during the parse.  We replicate that behavior to follow the
/// second invariant.
///
/// Note3: It would be nice if we could check these invariants at runtime, so we could control
/// our behavior by the behavior of the real .NET regex engine.  For example, if the .NET regex
/// engine did not report any issues, we could suppress any diagnostics we generated and we
/// could log an NFW to record which pattern we deviated on so we could fix the issue for a
/// future release.  However, we cannot do this as the .NET regex engine has no guarantees about
/// its performance characteristics.  For example, certain regex patterns might end up causing
/// that engine to consume unbounded amounts of CPU and memory.  This is because the .NET regex
/// engine is not just a parser, but something that builds an actual recognizer using techniques
/// that are not necessarily bounded.  As such, while we test ourselves around it during our
/// tests, we cannot do the same at runtime as part of the IDE.
///
/// This parser was based off the corefx RegexParser based at:
/// https://github.com/dotnet/corefx/blob/f759243d724f462da0bcef54e86588f8a55352c6/src/System.Text.RegularExpressions/src/System/Text/RegularExpressions/RegexParser.cs#L1
///
/// Note4: The .NET parser itself changes over time (for example to fix behavior that even it
/// thinks is buggy).  When this happens, we have to make a choice as to which behavior to
/// follow. In general, the overall principle is that we should follow the more lenient
/// behavior.  If we end up taking the more strict interpretation we risk giving people an error
/// during design time that they would not get at runtime.  It's far worse to have that than to
/// not report an error, even though one might happen later.
/// </remarks>
internal partial struct NewRoutePatternParser
{
    private readonly ImmutableDictionary<string, TextSpan> _captureNamesToSpan;
    private readonly ImmutableDictionary<int, TextSpan> _captureNumbersToSpan;

    private NewRoutePatternLexer _lexer;
    private RoutePatternToken _currentToken;

    private NewRoutePatternParser(
        AspNetCoreVirtualCharSequence text,
        ImmutableDictionary<string, TextSpan> captureNamesToSpan,
        ImmutableDictionary<int, TextSpan> captureNumbersToSpan) : this()
    {
        _lexer = new NewRoutePatternLexer(text);

        _captureNamesToSpan = captureNamesToSpan;
        _captureNumbersToSpan = captureNumbersToSpan;

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
        var tree1 = new NewRoutePatternParser(text,
            ImmutableDictionary<string, TextSpan>.Empty,
            ImmutableDictionary<int, TextSpan>.Empty).ParseTree();

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

        var seenDiagnostics = new HashSet<EmbeddedDiagnostic>();
        var diagnostics = new List<EmbeddedDiagnostic>();
        CollectDiagnosticsWorker(root, seenDiagnostics, diagnostics);

        ValidateNoConsecutiveParameters(root, diagnostics);
        ValidateCatchAllParameters(root, diagnostics);
        ValidateParameterParts(root, diagnostics);

        return new NewRoutePatternTree(
            _lexer.Text, root, diagnostics.ToImmutable(),
            _captureNamesToSpan, _captureNumbersToSpan);
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

    private static void ValidateParameterParts(RoutePatternCompilationUnit root, List<EmbeddedDiagnostic> diagnostics)
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
                        var hasDefault = false;
                        var hasOptional = false;
                        var hasCatchAll = false;
                        foreach (var parameterPart in parameterNode)
                        {
                            switch (parameterPart.Kind)
                            {
                                case NewRoutePatternKind.Optional:
                                    hasOptional = true;
                                    break;
                                case NewRoutePatternKind.DefaultValue:
                                    hasDefault = true;
                                    break;
                                case NewRoutePatternKind.CatchAll:
                                    hasCatchAll = true;
                                    break;
                            }
                        }
                        if (hasDefault && hasOptional)
                        {
                            diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_OptionalCannotHaveDefaultValue, parameterNode.GetSpan()));
                        }
                        if (hasCatchAll && hasOptional)
                        {
                            diagnostics.Add(new EmbeddedDiagnostic(Resources.TemplateRoute_CatchAllCannotBeOptional, parameterNode.GetSpan()));
                        }
                    }
                }
            }
        }
    }

    private void CollectDiagnosticsWorker(NewRoutePatternNode node, HashSet<EmbeddedDiagnostic> seenDiagnostics, List<EmbeddedDiagnostic> diagnostics)
    {
        foreach (var child in node)
        {
            if (child.IsNode)
            {
                CollectDiagnosticsWorker(child.Node, seenDiagnostics, diagnostics);
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
