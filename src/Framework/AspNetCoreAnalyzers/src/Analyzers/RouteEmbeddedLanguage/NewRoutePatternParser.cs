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
        => _currentToken.Kind switch
        {
            NewRoutePatternKind.OpenBraceToken => ParseParameter(),
            //NewRoutePatternKind.OpenBracketToken => ParseReplacement(),
            _ => ParseLiteral(),
        };

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

    private RoutePatternParameterNode ParseParameter()
    {
        var result = new RoutePatternParameterNode(
            ConsumeCurrentToken(),
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
        var colonToken = _currentToken;
        var policyName = _lexer.TryScanPolicyName();
        if (policyName == null)
        {
            policyName = CreateMissingToken(NewRoutePatternKind.PolicyNameToken);
            if (_currentToken.Kind != NewRoutePatternKind.EndOfFile)
            {
                policyName = policyName.Value.AddDiagnosticIfNone(new EmbeddedDiagnostic("", _currentToken.GetFullSpan().Value));
            }
        }

        ConsumeCurrentToken();

        if (_currentToken.Kind == NewRoutePatternKind.OpenParenToken)
        {
            return new RoutePatternPolicyWithArgumentsParameterPartNode(
                colonToken,
                policyName.Value,
                _currentToken,
                ParsePolicyArgument(),
                ConsumeToken(NewRoutePatternKind.CloseParenToken, error: null));
        }
        else
        {
            return new RoutePatternPolicyParameterPartNode(colonToken, policyName.Value);
        }
    }

    private RoutePatternToken ParsePolicyArgument()
    {
        var policyArgument = _lexer.TryScanPolicyArgument();
        if (policyArgument == null)
        {
            policyArgument = CreateMissingToken(NewRoutePatternKind.PolicyArgumentToken);
            //if (_currentToken.Kind != NewRoutePatternKind.EndOfFile)
            //{
            //    policyArgument = policyName.Value.AddDiagnosticIfNone(new EmbeddedDiagnostic("", _currentToken.GetFullSpan().Value));
            //}
        }

        ConsumeCurrentToken();

        return policyArgument.Value;
    }

    private RoutePatternSegmentSeperatorNode ParseSegmentSeperator()
        => new(ConsumeCurrentToken());

    private TextSpan GetTokenStartPositionSpan(RoutePatternToken token)
    {
        return token.Kind == NewRoutePatternKind.EndOfFile
            ? new TextSpan(_lexer.Text.Last().Span.End, 0)
            : new TextSpan(token.VirtualChars[0].Span.Start, 0);
    }

    /*

    /// <summary>
    /// Parses out code of the form: ...|...|... This is the type of code you have at the top level of a regex, or
    /// inside any grouping construct.  Note that sequences can be empty in .NET regex.  i.e. the following is
    /// legal:
    ///
    ///     ...||...
    ///
    /// An empty sequence just means "match at every position in the test string".
    /// </summary>


    private RegexSequenceNode ParseSequence(bool consumeCloseParen)
    {
        var builder = new List<RegexExpressionNode>();
        while (ShouldConsumeSequenceElement(consumeCloseParen))
        {
            var last = builder.Count == 0 ? null : builder.Last();
            builder.Add(ParsePrimaryExpressionAndQuantifiers(last));
        }

        // We will commonly get tons of text nodes in a row.  For example, the regex `abc` will be three text nodes
        // in a row.  To help save on memory try to merge that into one single text node.
        var sequence = new List<RegexExpressionNode>();
        MergeTextNodes(builder, sequence);

        return new RegexSequenceNode(sequence.ToImmutable());
    }

    private static void MergeTextNodes(List<RegexExpressionNode> list, List<RegexExpressionNode> final)
    {
        // Iterate all the nodes in the sequence we have, adding them directly to
        // `final` if they are not text nodes.  If they are text nodes, we attempt
        // to keep merging them with any following text nodes as long as well.
        for (var index = 0; index < list.Count;)
        {
            var current = list[index];
            if (current.Kind != RoutePatternKind.Text)
            {
                // Not a text node.  Just add as-is, and move to the next node.
                index++;
                final.Add(current);
                continue;
            }

            // Got a text node.  Try to combine it with all following nodes.
            index = MergeAndAddAdjacentTextNodes(list, final, index);
        }

        return;

        // local functions

        static int MergeAndAddAdjacentTextNodes(
            List<RegexExpressionNode> list,
            List<RegexExpressionNode> final,
            int index)
        {
            var startIndex = index;
            var startTextNode = (RegexTextNode)list[startIndex];

            // Keep walking forward as long as we hit text nodes and we can 
            // merge that text node with the previous text node.
            index++;
            var lastTextNode = startTextNode;
            for (; index < list.Count; index++)
            {
                var currentNode = list[index];
                if (!CanMerge(lastTextNode, currentNode))
                {
                    // Hit something we couldn't merge with our last text node
                    // Break out and merge what we have so far.  'index' will
                    // be pointing at the right node for our caller.
                    break;
                }

                lastTextNode = (RegexTextNode)currentNode;
            }

            // If didn't have multiple text nodes in a row.  Just return the
            // starting node.  Otherwise, create one text node that has a token
            // that spans from the start of the first node to the end of the last node.
            final.Add(startTextNode == lastTextNode
                ? startTextNode
                : new RegexTextNode(CreateToken(
                    RoutePatternKind.TextToken,
                    AspNetCoreVirtualCharSequence.FromBounds(
                        startTextNode.TextToken.VirtualChars,
                        lastTextNode.TextToken.VirtualChars))));

            return index;
        }

        // Local functions
        static bool CanMerge(RegexTextNode lastNode, RegexExpressionNode next)
        {
            if (next.Kind == RoutePatternKind.Text)
            {
                var lastTextToken = lastNode.TextToken;
                var nextTextToken = ((RegexTextNode)next).TextToken;

                // Can't merge if the next text node has leading trivia. Also, conservatively 
                // don't allow merging if there are diagnostics or values for these tokens.  
                // We might be able to support that, but it's easier to not do anything that 
                // might break an expectation someone might have downstream.                    /
                if (lastTextToken.Diagnostics.Length == 0 &&
                    nextTextToken.Diagnostics.Length == 0 &&
                    lastTextToken.Value == null &&
                    nextTextToken.Value == null)
                {
                    //lastTextToken.VirtualChars.AssertAdjacentTo(nextTextToken.VirtualChars);
                    return true;
                }
            }

            return false;
        }
    }

    private bool ShouldConsumeSequenceElement(bool consumeCloseParen)
    {
        if (_currentToken.Kind == RoutePatternKind.EndOfFile)
        {
            return false;
        }

        if (_currentToken.Kind == RoutePatternKind.BarToken)
        {
            return false;
        }

        if (_currentToken.Kind == RoutePatternKind.CloseParenToken)
        {
            return consumeCloseParen;
        }

        return true;
    }

    private RegexExpressionNode ParsePrimaryExpressionAndQuantifiers(RegexExpressionNode? lastExpression)
    {
        var current = ParsePrimaryExpression(lastExpression);
        if (current.Kind == RoutePatternKind.SimpleOptionsGrouping)
        {
            // Simple options (i.e. "(?i-x)" can't have quantifiers attached to them).
            return current;
        }

        return _currentToken.Kind switch
        {
            RoutePatternKind.AsteriskToken => ParseZeroOrMoreQuantifier(current),
            RoutePatternKind.PlusToken => ParseOneOrMoreQuantifier(current),
            RoutePatternKind.QuestionToken => ParseZeroOrOneQuantifier(current),
            RoutePatternKind.OpenBraceToken => TryParseNumericQuantifier(current, _currentToken),
            _ => current,
        };
    }

    private RegexExpressionNode TryParseLazyQuantifier(RegexQuantifierNode quantifier)
    {
        if (_currentToken.Kind != RoutePatternKind.QuestionToken)
        {
            return quantifier;
        }

        // Whitespace allowed after the question and the next sequence element.
        return new RegexLazyQuantifierNode(quantifier, ConsumeCurrentToken());
    }

    private RegexExpressionNode ParseZeroOrMoreQuantifier(RegexPrimaryExpressionNode current)
    {
        // Whitespace allowed between the quantifier and the possible following ? or next sequence item.
        return TryParseLazyQuantifier(new RegexZeroOrMoreQuantifierNode(current, ConsumeCurrentToken()));
    }

    private RegexExpressionNode ParseOneOrMoreQuantifier(RegexPrimaryExpressionNode current)
    {
        // Whitespace allowed between the quantifier and the possible following ? or next sequence item.
        return TryParseLazyQuantifier(new RegexOneOrMoreQuantifierNode(current, ConsumeCurrentToken()));
    }

    private RegexExpressionNode ParseZeroOrOneQuantifier(RegexPrimaryExpressionNode current)
    {
        // Whitespace allowed between the quantifier and the possible following ? or next sequence item.
        return TryParseLazyQuantifier(new RegexZeroOrOneQuantifierNode(current, ConsumeCurrentToken()));
    }

    private RegexExpressionNode TryParseNumericQuantifier(
        RegexPrimaryExpressionNode expression, RoutePatternToken openBraceToken)
    {
        var start = _lexer.Position;

        if (!TryParseNumericQuantifierParts(
                out var firstNumberToken,
                out var commaToken,
                out var secondNumberToken,
                out var closeBraceToken))
        {
            _currentToken = openBraceToken;
            _lexer.Position = start;
            return expression;
        }

        var quantifier = CreateQuantifier(
            expression, openBraceToken, firstNumberToken, commaToken,
            secondNumberToken, closeBraceToken);

        return TryParseLazyQuantifier(quantifier);
    }

    private static RegexQuantifierNode CreateQuantifier(
        RegexPrimaryExpressionNode expression,
        RoutePatternToken openBraceToken, RoutePatternToken firstNumberToken, RoutePatternToken? commaToken,
        RoutePatternToken? secondNumberToken, RoutePatternToken closeBraceToken)
    {
        if (commaToken != null)
        {
            return secondNumberToken != null
                ? new RegexClosedNumericRangeQuantifierNode(expression, openBraceToken, firstNumberToken, commaToken.Value, secondNumberToken.Value, closeBraceToken)
                : new RegexOpenNumericRangeQuantifierNode(expression, openBraceToken, firstNumberToken, commaToken.Value, closeBraceToken);
        }

        return new RegexExactNumericQuantifierNode(expression, openBraceToken, firstNumberToken, closeBraceToken);
    }

    private bool TryParseNumericQuantifierParts(
        out RoutePatternToken firstNumberToken, out RoutePatternToken? commaToken,
        out RoutePatternToken? secondNumberToken, out RoutePatternToken closeBraceToken)
    {
        firstNumberToken = default;
        commaToken = null;
        secondNumberToken = null;
        closeBraceToken = default;

        var firstNumber = _lexer.TryScanNumber();
        if (firstNumber == null)
        {
            return false;
        }

        firstNumberToken = firstNumber.Value;

        // Nothing allowed between {x,n}
        ConsumeCurrentToken();

        if (_currentToken.Kind == RoutePatternKind.CommaToken)
        {
            commaToken = _currentToken;

            var start = _lexer.Position;
            secondNumberToken = _lexer.TryScanNumber();

            if (secondNumberToken == null)
            {
                // Nothing allowed between {x,n}
                ResetToPositionAndConsumeCurrentToken(start);
            }
            else
            {
                var secondNumberTokenLocal = secondNumberToken.Value;

                // Nothing allowed between {x,n}
                ConsumeCurrentToken();

                var val1 = (int)firstNumberToken.Value;
                var val2 = (int)secondNumberTokenLocal.Value;

                if (val2 < val1)
                {
                    secondNumberTokenLocal = secondNumberTokenLocal.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                        FeaturesResources.Illegal_x_y_with_x_less_than_y,
                        secondNumberTokenLocal.GetSpan()));
                    secondNumberToken = secondNumberTokenLocal;
                }
            }
        }

        if (_currentToken.Kind != RoutePatternKind.CloseBraceToken)
        {
            return false;
        }

        // Whitespace allowed between the quantifier and the possible following ? or next sequence item.
        closeBraceToken = ConsumeCurrentToken();
        return true;
    }

    private void ResetToPositionAndConsumeCurrentToken(int position)
    {
        _lexer.Position = position;
        ConsumeCurrentToken();
    }

    private RegexPrimaryExpressionNode ParsePrimaryExpression(RegexExpressionNode? lastExpression)
    {
        return _currentToken.Kind switch
        {
            RoutePatternKind.DotToken => ParseWildcard(),
            RoutePatternKind.CaretToken => ParseStartAnchor(),
            RoutePatternKind.DollarToken => ParseEndAnchor(),
            RoutePatternKind.BackslashToken => ParseEscape(_currentToken),
            RoutePatternKind.OpenBracketToken => ParseCharacterClass(),
            RoutePatternKind.OpenParenToken => ParseGrouping(),
            RoutePatternKind.CloseParenToken => ParseUnexpectedCloseParenToken(),
            RoutePatternKind.OpenBraceToken => ParsePossibleUnexpectedNumericQuantifier(lastExpression),
            RoutePatternKind.AsteriskToken or RoutePatternKind.PlusToken or RoutePatternKind.QuestionToken => ParseUnexpectedQuantifier(lastExpression),
            _ => ParseText(),
        };
    }

    private RegexPrimaryExpressionNode ParsePossibleUnexpectedNumericQuantifier(RegexExpressionNode? lastExpression)
    {
        // Native parser looks for something like {0,1} in a top level sequence and reports
        // an explicit error that that's not allowed.  However, something like {0, 1} is fine
        // and is treated as six textual tokens.
        var openBraceToken = _currentToken.With(kind: RoutePatternKind.TextToken);
        var start = _lexer.Position;

        if (TryParseNumericQuantifierParts(
                out _, out _, out _, out _))
        {
            // Report that a numeric quantifier isn't allowed here.
            CheckQuantifierExpression(lastExpression, ref openBraceToken);
        }

        // Started with { but wasn't a numeric quantifier.  This is totally legal and is just
        // a textual sequence.  Restart, scanning this token as a normal sequence element.
        ResetToPositionAndConsumeCurrentToken(start);
        return new RegexTextNode(openBraceToken);
    }

    private RegexPrimaryExpressionNode ParseUnexpectedCloseParenToken()
    {
        var token = _currentToken.With(kind: RoutePatternKind.TextToken).AddDiagnosticIfNone(
            new EmbeddedDiagnostic(FeaturesResources.Too_many_close_parens, _currentToken.GetSpan()));

        // Technically, since an error occurred, we can do whatever we want here.  However,
        // the spirit of the native parser is that top level sequence elements are allowed
        // to have trivia.  So that's the behavior we mimic.
        ConsumeCurrentToken();
        return new RegexTextNode(token);
    }

    private RegexPrimaryExpressionNode ParseText()
    {
        var token = ConsumeCurrentToken();
        Debug.Assert(token.Value == null);

        // Allow trivia between this piece of text and the next sequence element
        return new RegexTextNode(token.With(kind: RoutePatternKind.TextToken));
    }

    private RegexPrimaryExpressionNode ParseEndAnchor()
    {
        // Allow trivia between this anchor and the next sequence element
        return new RegexAnchorNode(RoutePatternKind.EndAnchor, ConsumeCurrentToken());
    }

    private RegexPrimaryExpressionNode ParseStartAnchor()
    {
        // Allow trivia between this anchor and the next sequence element
        return new RegexAnchorNode(RoutePatternKind.StartAnchor, ConsumeCurrentToken());
    }

    private RegexPrimaryExpressionNode ParseWildcard()
    {
        // Allow trivia between the . and the next sequence element
        return new RegexWildcardNode(ConsumeCurrentToken());
    }

    private RegexGroupingNode ParseGrouping()
    {
        var start = _lexer.Position;

        // Check what immediately follows the (.  If we have (? it is processed specially.
        // However, we do not treat (? the same as ( ?
        var openParenToken = ConsumeCurrentToken();

        switch (_currentToken.Kind)
        {
            case RoutePatternKind.QuestionToken:
                return ParseGroupQuestion(openParenToken, _currentToken);

            default:
                // Wasn't (? just parse this as a normal group.
                _lexer.Position = start;
                return ParseSimpleGroup(openParenToken);
        }
    }

    private RoutePatternToken ParseGroupingCloseParen()
    {
        switch (_currentToken.Kind)
        {
            case RoutePatternKind.CloseParenToken:
                // Grouping completed normally.  Allow trivia between it and the next sequence element.
                return ConsumeCurrentToken();

            default:
                return CreateMissingToken(RoutePatternKind.CloseParenToken).AddDiagnosticIfNone(
                    new EmbeddedDiagnostic(FeaturesResources.Not_enough_close_parens, GetTokenStartPositionSpan(_currentToken)));
        }
    }

    private RegexSimpleGroupingNode ParseSimpleGroup(RoutePatternToken openParenToken)
        => new(
            openParenToken, ParseGroupingEmbeddedExpression(_options), ParseGroupingCloseParen());

    private RegexExpressionNode ParseGroupingEmbeddedExpression(RegexOptions embeddedOptions)
    {
        // Save and restore options when we go into, and pop out of a group node.
        var currentOptions = _options;
        _options = embeddedOptions;

        // We're parsing the embedded sequence inside the current group.  As this is a sequence
        // we want to allow trivia between the current token we're on, and the first token
        // of the embedded sequence.
        ConsumeCurrentToken();

        // When parsing out the sequence don't grab the close paren, that will be for our caller
        // to get.
        var expression = this.ParseAlternatingSequencesWorker(consumeCloseParen: false, isConditional: false);
        _options = currentOptions;
        return expression;
    }

    private TextSpan GetTokenSpanIncludingEOF(RoutePatternToken token)
        => token.Kind == RoutePatternKind.EndOfFile
            ? GetTokenStartPositionSpan(token)
            : token.GetSpan();

    private RegexGroupingNode ParseGroupQuestion(RoutePatternToken openParenToken, RoutePatternToken questionToken)
    {
        var optionsToken = _lexer.TryScanOptions();
        if (optionsToken != null)
        {
            return ParseOptionsGroupingNode(openParenToken, questionToken, optionsToken.Value);
        }

        var afterQuestionPos = _lexer.Position;

        // Lots of possible options when we see (?.  Look at the immediately following character
        // (without any allowed spaces) to decide what to parse out next.
        ConsumeCurrentToken();
        switch (_currentToken.Kind)
        {
            case RoutePatternKind.LessThanToken:
                // (?<=...) or (?<!...) or (?<...>...) or (?<...-...>...)
                return ParseLookbehindOrNamedCaptureOrBalancingGrouping(openParenToken, questionToken);

            case RoutePatternKind.SingleQuoteToken:
                //  (?'...'...) or (?'...-...'...)
                return ParseNamedCaptureOrBalancingGrouping(
                    openParenToken, questionToken, _currentToken);

            case RoutePatternKind.OpenParenToken:
                // alternation construct (?(...) | )
                return ParseConditionalGrouping(openParenToken, questionToken);

            case RoutePatternKind.ColonToken:
                return ParseNonCapturingGroupingNode(openParenToken, questionToken);

            case RoutePatternKind.EqualsToken:
                return ParsePositiveLookaheadGrouping(openParenToken, questionToken);

            case RoutePatternKind.ExclamationToken:
                return ParseNegativeLookaheadGrouping(openParenToken, questionToken);

            case RoutePatternKind.GreaterThanToken:
                return ParseAtomicGrouping(openParenToken, questionToken);

            default:
                if (_currentToken.Kind != RoutePatternKind.CloseParenToken)
                {
                    // Native parser reports "Unrecognized grouping construct", *except* for (?)
                    openParenToken = openParenToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                        FeaturesResources.Unrecognized_grouping_construct,
                        openParenToken.GetSpan()));
                }

                break;
        }

        // (?)
        // Parse this as a normal group. The question will immediately error as it's a 
        // quantifier not following anything.
        _lexer.Position = afterQuestionPos - 1;
        return ParseSimpleGroup(openParenToken);
    }

    private RegexConditionalGroupingNode ParseConditionalGrouping(RoutePatternToken openParenToken, RoutePatternToken questionToken)
    {
        var innerOpenParenToken = _currentToken;
        var afterInnerOpenParen = _lexer.Position;

        var captureToken = _lexer.TryScanNumberOrCaptureName();
        if (captureToken == null)
        {
            return ParseConditionalExpressionGrouping(openParenToken, questionToken);
        }

        var capture = captureToken.Value;

        RoutePatternToken innerCloseParenToken;
        if (capture.Kind == RoutePatternKind.NumberToken)
        {
            // If it's a numeric group, it has to be immediately followed by a ) and the
            // numeric reference has to exist.
            //
            // That means that (?(4 ) is not treated as an embedded expression but as an
            // error.  This is different from (?(a ) which will be treated as an embedded
            // expression, and different from (?(a) will be treated as an embedded
            // expression or capture group depending on if 'a' is a existing capture name.

            ConsumeCurrentToken();
            if (_currentToken.Kind == RoutePatternKind.CloseParenToken)
            {
                innerCloseParenToken = _currentToken;
                if (!HasCapture((int)capture.Value))
                {
                    capture = capture.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                        FeaturesResources.Reference_to_undefined_group,
                        capture.GetSpan()));
                }
            }
            else
            {
                innerCloseParenToken = CreateMissingToken(RoutePatternKind.CloseParenToken);
                capture = capture.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                    FeaturesResources.Malformed,
                    capture.GetSpan()));
                MoveBackBeforePreviousScan();
            }
        }
        else
        {
            // If it's a capture name, it's ok if that capture doesn't exist.  In that case we
            // will just treat this as an conditional expression.
            if (!HasCapture((string)capture.Value))
            {
                _lexer.Position = afterInnerOpenParen;
                return ParseConditionalExpressionGrouping(openParenToken, questionToken);
            }

            // Capture name existed.  For this to be a capture grouping it exactly has to
            // match (?(a)   anything other than a close paren after the ) will make this
            // into a conditional expression.
            ConsumeCurrentToken();
            if (_currentToken.Kind != RoutePatternKind.CloseParenToken)
            {
                _lexer.Position = afterInnerOpenParen;
                return ParseConditionalExpressionGrouping(openParenToken, questionToken);
            }

            innerCloseParenToken = _currentToken;
        }

        // Was (?(name) or (?(num)  and name/num was a legal capture name.  Parse
        // this out as a conditional grouping.  Because we're going to be parsing out
        // an embedded sequence, allow trivia before the first element.
        ConsumeCurrentToken();
        var result = ParseConditionalGroupingResult();

        return new RegexConditionalCaptureGroupingNode(
            openParenToken, questionToken,
            innerOpenParenToken, capture, innerCloseParenToken,
            result, ParseGroupingCloseParen());
    }

    private bool HasCapture(int value)
        => _captureNumbersToSpan.ContainsKey(value);

    private bool HasCapture(string value)
        => _captureNamesToSpan.ContainsKey(value);

    private RegexConditionalGroupingNode ParseConditionalExpressionGrouping(
        RoutePatternToken openParenToken, RoutePatternToken questionToken)
    {
        // Reproduce very specific errors the .NET regex parser looks for.  Technically,
        // we would error out in these cases no matter what.  However, it means we can
        // stringently enforce that our parser produces the same errors as the native one.
        //
        // Move back before the (
        _lexer.Position--;
        if (_lexer.IsAt("(?'"))
        {
            openParenToken = openParenToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Alternation_conditions_do_not_capture_and_cannot_be_named,
                openParenToken.GetSpan()));
        }
        else if (_lexer.IsAt("(?<"))
        {
            if (!_lexer.IsAt("(?<!") &&
                !_lexer.IsAt("(?<="))
            {
                openParenToken = openParenToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                    FeaturesResources.Alternation_conditions_do_not_capture_and_cannot_be_named,
                    openParenToken.GetSpan()));
            }
        }

        // Consume the ( once more.
        ConsumeCurrentToken();
        Debug.Assert(_currentToken.Kind == RoutePatternKind.OpenParenToken);

        // Parse out the grouping that starts with the second open paren in (?(
        // this will get us to (?(...)
        var grouping = ParseGrouping();

        // Now parse out the embedded expression that follows that.  this will get us to
        // (?(...)...
        var result = ParseConditionalGroupingResult();

        // Finally, grab the close paren and produce (?(...)...)
        return new RegexConditionalExpressionGroupingNode(
            openParenToken, questionToken,
            grouping, result, ParseGroupingCloseParen());
    }

    private RegexExpressionNode ParseConditionalGroupingResult()
    {
        var currentOptions = _options;
        var result = this.ParseAlternatingSequencesWorker(consumeCloseParen: false, isConditional: true);
        _options = currentOptions;
        return result;
    }

    private RegexGroupingNode ParseLookbehindOrNamedCaptureOrBalancingGrouping(
        RoutePatternToken openParenToken, RoutePatternToken questionToken)
    {
        var start = _lexer.Position;

        // We have  (?<  Look for  (?<=  or  (?<!
        var lessThanToken = ConsumeCurrentToken();

        switch (_currentToken.Kind)
        {
            case RoutePatternKind.EqualsToken:
                return new RegexPositiveLookbehindGroupingNode(
                    openParenToken, questionToken, lessThanToken, _currentToken,
                    ParseGroupingEmbeddedExpression(_options | RegexOptions.RightToLeft), ParseGroupingCloseParen());

            case RoutePatternKind.ExclamationToken:
                return new RegexNegativeLookbehindGroupingNode(
                    openParenToken, questionToken, lessThanToken, _currentToken,
                    ParseGroupingEmbeddedExpression(_options | RegexOptions.RightToLeft), ParseGroupingCloseParen());

            default:
                // Didn't have a lookbehind group.  Parse out as  (?<...>  or  (?<...-...>
                _lexer.Position = start;
                return ParseNamedCaptureOrBalancingGrouping(openParenToken, questionToken, lessThanToken);
        }
    }

    private RegexGroupingNode ParseNamedCaptureOrBalancingGrouping(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken openToken)
    {
        if (_lexer.Position == _lexer.Text.Length)
        {
            openParenToken = openParenToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Unrecognized_grouping_construct,
                GetSpan(openParenToken, openToken)));
        }

        // (?<...>...) or (?<...-...>...)
        // (?'...'...) or (?'...-...'...)
        var captureToken = _lexer.TryScanNumberOrCaptureName();
        if (captureToken == null)
        {
            // Can't have any trivia between the elements in this grouping header.
            ConsumeCurrentToken();
            captureToken = CreateMissingToken(RoutePatternKind.CaptureNameToken);

            if (_currentToken.Kind == RoutePatternKind.MinusToken)
            {
                return ParseBalancingGrouping(
                    openParenToken, questionToken, openToken, captureToken.Value);
            }
            else
            {
                openParenToken = openParenToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                    FeaturesResources.Invalid_group_name_Group_names_must_begin_with_a_word_character,
                    GetTokenSpanIncludingEOF(_currentToken)));

                // If we weren't at the end of the text, go back to before whatever character
                // we just consumed.
                MoveBackBeforePreviousScan();
            }
        }

        var capture = captureToken.Value;
        if (capture.Kind == RoutePatternKind.NumberToken && (int)capture.Value == 0)
        {
            capture = capture.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Capture_number_cannot_be_zero,
                capture.GetSpan()));
        }

        // Can't have any trivia between the elements in this grouping header.
        ConsumeCurrentToken();

        if (_currentToken.Kind == RoutePatternKind.MinusToken)
        {
            // Have  (?<...-  parse out the balancing group form.
            return ParseBalancingGrouping(
                openParenToken, questionToken,
                openToken, capture);
        }

        var closeToken = ParseCaptureGroupingCloseToken(ref openParenToken, openToken);

        return new RegexCaptureGroupingNode(
            openParenToken, questionToken,
            openToken, capture, closeToken,
            ParseGroupingEmbeddedExpression(_options), ParseGroupingCloseParen());
    }

    private RoutePatternToken ParseCaptureGroupingCloseToken(ref RoutePatternToken openParenToken, RoutePatternToken openToken)
    {
        if (openToken.Kind == RoutePatternKind.LessThanToken && _currentToken.Kind == RoutePatternKind.GreaterThanToken ||
            openToken.Kind == RoutePatternKind.SingleQuoteToken && _currentToken.Kind == RoutePatternKind.SingleQuoteToken)
        {
            return _currentToken;
        }

        if (_currentToken.Kind == RoutePatternKind.EndOfFile)
        {
            openParenToken = openParenToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Unrecognized_grouping_construct,
                GetSpan(openParenToken, openToken)));
        }
        else
        {
            openParenToken = openParenToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Invalid_group_name_Group_names_must_begin_with_a_word_character,
                _currentToken.GetSpan()));

            // Rewind to where we were before seeing this bogus character.
            _lexer.Position--;
        }

        return CreateMissingToken(
            openToken.Kind == RoutePatternKind.LessThanToken
                ? RoutePatternKind.GreaterThanToken : RoutePatternKind.SingleQuoteToken);
    }

    private RegexBalancingGroupingNode ParseBalancingGrouping(
        RoutePatternToken openParenToken, RoutePatternToken questionToken,
        RoutePatternToken openToken, RoutePatternToken firstCapture)
    {
        var minusToken = _currentToken;
        var secondCapture = _lexer.TryScanNumberOrCaptureName();
        if (secondCapture == null)
        {
            // Invalid group name: Group names must begin with a word character
            ConsumeCurrentToken();

            openParenToken = openParenToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Invalid_group_name_Group_names_must_begin_with_a_word_character,
                GetTokenSpanIncludingEOF(_currentToken)));

            // If we weren't at the end of the text, go back to before whatever character
            // we just consumed.
            MoveBackBeforePreviousScan();
            secondCapture = CreateMissingToken(RoutePatternKind.CaptureNameToken);
        }

        var second = secondCapture.Value;
        CheckCapture(ref second);

        // Can't have any trivia between the elements in this grouping header.
        ConsumeCurrentToken();
        var closeToken = ParseCaptureGroupingCloseToken(ref openParenToken, openToken);

        return new RegexBalancingGroupingNode(
            openParenToken, questionToken,
            openToken, firstCapture, minusToken, second, closeToken,
            ParseGroupingEmbeddedExpression(_options), ParseGroupingCloseParen());
    }

    private void CheckCapture(ref RoutePatternToken captureToken)
    {
        if (captureToken.IsMissing)
        {
            // Don't need to check for a synthesized error capture token.
            return;
        }

        if (captureToken.Kind == RoutePatternKind.NumberToken)
        {
            var val = (int)captureToken.Value;
            if (!HasCapture(val))
            {
                captureToken = captureToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                    string.Format(FeaturesResources.Reference_to_undefined_group_number_0, val),
                    captureToken.GetSpan()));
            }
        }
        else
        {
            var val = (string)captureToken.Value;
            if (!HasCapture(val))
            {
                captureToken = captureToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                    string.Format(FeaturesResources.Reference_to_undefined_group_name_0, val),
                    captureToken.GetSpan()));
            }
        }
    }

    private RegexNonCapturingGroupingNode ParseNonCapturingGroupingNode(RoutePatternToken openParenToken, RoutePatternToken questionToken)
        => new(
            openParenToken, questionToken, _currentToken,
            ParseGroupingEmbeddedExpression(_options), ParseGroupingCloseParen());

    private RegexPositiveLookaheadGroupingNode ParsePositiveLookaheadGrouping(RoutePatternToken openParenToken, RoutePatternToken questionToken)
        => new(
            openParenToken, questionToken, _currentToken,
            ParseGroupingEmbeddedExpression(_options & ~RegexOptions.RightToLeft), ParseGroupingCloseParen());

    private RegexNegativeLookaheadGroupingNode ParseNegativeLookaheadGrouping(RoutePatternToken openParenToken, RoutePatternToken questionToken)
        => new(
            openParenToken, questionToken, _currentToken,
            ParseGroupingEmbeddedExpression(_options & ~RegexOptions.RightToLeft), ParseGroupingCloseParen());

    private RegexAtomicGroupingNode ParseAtomicGrouping(RoutePatternToken openParenToken, RoutePatternToken questionToken)
        => new(
            openParenToken, questionToken, _currentToken,
            ParseGroupingEmbeddedExpression(_options), ParseGroupingCloseParen());

    private RegexGroupingNode ParseOptionsGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken optionsToken)
    {
        // Only (?opts:...) or (?opts) are allowed.  After the opts must be a : or )
        ConsumeCurrentToken();
        switch (_currentToken.Kind)
        {
            case RoutePatternKind.CloseParenToken:
                // Allow trivia after the options and the next element in the sequence.
                _options = GetNewOptionsFromToken(_options, optionsToken);
                return new RegexSimpleOptionsGroupingNode(
                    openParenToken, questionToken, optionsToken,
                    ConsumeCurrentToken());

            case RoutePatternKind.ColonToken:
                return ParseNestedOptionsGroupingNode(openParenToken, questionToken, optionsToken);

            default:
                return new RegexSimpleOptionsGroupingNode(
                    openParenToken, questionToken, optionsToken,
                    CreateMissingToken(RoutePatternKind.CloseParenToken).AddDiagnosticIfNone(
                        new EmbeddedDiagnostic(FeaturesResources.Unrecognized_grouping_construct, openParenToken.GetSpan())));
        }
    }

    private RegexNestedOptionsGroupingNode ParseNestedOptionsGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken optionsToken)
        => new(
            openParenToken, questionToken, optionsToken, _currentToken,
            ParseGroupingEmbeddedExpression(GetNewOptionsFromToken(_options, optionsToken)), ParseGroupingCloseParen());

    private static bool IsTextChar(RoutePatternToken currentToken, char ch)
        => currentToken.Kind == RoutePatternKind.TextToken && currentToken.VirtualChars.Length == 1 && currentToken.VirtualChars[0].Value == ch;

    private static RegexOptions GetNewOptionsFromToken(RegexOptions currentOptions, RoutePatternToken optionsToken)
    {
        var copy = currentOptions;
        var on = true;
        foreach (var ch in optionsToken.VirtualChars)
        {
            switch (ch.Value)
            {
                case '-': on = false; break;
                case '+': on = true; break;
                default:
                    var newOption = OptionFromCode(ch);
                    if (on)
                    {
                        copy |= newOption;
                    }
                    else
                    {
                        copy &= ~newOption;
                    }

                    break;
            }
        }

        return copy;
    }

    private static RegexOptions OptionFromCode(AspNetCoreVirtualChar ch)
    {
        switch (ch.Value)
        {
            case 'i': case 'I': return RegexOptions.IgnoreCase;
            case 'm': case 'M': return RegexOptions.Multiline;
            case 'n': case 'N': return RegexOptions.ExplicitCapture;
            case 's': case 'S': return RegexOptions.Singleline;
            case 'x': case 'X': return RegexOptions.IgnorePatternWhitespace;
            default:
                throw new InvalidOperationException();
        }
    }

    private RegexBaseCharacterClassNode ParseCharacterClass()
    {
        var openBracketToken = _currentToken;
        Debug.Assert(openBracketToken.Kind == RoutePatternKind.OpenBracketToken);
        var caretToken = CreateMissingToken(RoutePatternKind.CaretToken);
        var closeBracketToken = CreateMissingToken(RoutePatternKind.CloseBracketToken);

        // trivia is not allowed anywhere in a character class
        ConsumeCurrentToken();
        if (_currentToken.Kind == RoutePatternKind.CaretToken)
        {
            caretToken = _currentToken;
        }
        else
        {
            MoveBackBeforePreviousScan();
        }

        // trivia is not allowed anywhere in a character class
        ConsumeCurrentToken();

        var builder = new List<RegexExpressionNode>();
        while (_currentToken.Kind != RoutePatternKind.EndOfFile)
        {
            Debug.Assert(_currentToken.VirtualChars.Length == 1);

            if (_currentToken.Kind == RoutePatternKind.CloseBracketToken && builder.Count > 0)
            {
                // Allow trivia after the character class, and whatever is next in the sequence.
                closeBracketToken = ConsumeCurrentToken();
                break;
            }

            ParseCharacterClassComponents(builder);
        }

        // We will commonly get tons of text nodes in a row.  For example, the regex `[abc]` will be three text
        // nodes in a row.  To help save on memory try to merge that into one single text node.
        var contents = new List<RegexExpressionNode>();
        MergeTextNodes(builder, contents);

        if (closeBracketToken.IsMissing)
        {
            closeBracketToken = closeBracketToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Unterminated_character_class_set,
                GetTokenStartPositionSpan(_currentToken)));
        }

        var components = new RegexSequenceNode(contents.ToImmutable());
        return caretToken.IsMissing
            ? new RegexCharacterClassNode(openBracketToken, components, closeBracketToken)
            : new RegexNegatedCharacterClassNode(openBracketToken, caretToken, components, closeBracketToken);
    }

    private void ParseCharacterClassComponents(List<RegexExpressionNode> components)
    {
        var left = ParseSingleCharacterClassComponent(isFirst: components.Count == 0, afterRangeMinus: false);
        if (left.Kind == RoutePatternKind.CharacterClassEscape ||
            left.Kind == RoutePatternKind.CategoryEscape ||
            IsEscapedMinus(left))
        {
            // \s or \p{Lu} or \- on the left of a minus doesn't start a range. If there is a following
            // minus, it's just treated textually.
            components.Add(left);
            return;
        }

        if (_currentToken.Kind == RoutePatternKind.MinusToken && !_lexer.IsAt("]"))
        {
            // trivia is not allowed anywhere in a character class
            var minusToken = ConsumeCurrentToken();

            if (_currentToken.Kind == RoutePatternKind.OpenBracketToken)
            {
                components.Add(left);
                components.Add(ParseCharacterClassSubtractionNode(minusToken));
            }
            else
            {
                // Note that behavior of parsing here changed in .net.  See issue:
                // https://github.com/dotnet/corefx/issues/31786
                //
                // We follow the latest behavior in .net which parses things correctly.
                var right = ParseSingleCharacterClassComponent(isFirst: false, afterRangeMinus: true);

                if (TryGetRangeComponentValue(left, out var leftCh) &&
                    TryGetRangeComponentValue(right, out var rightCh) &&
                    leftCh > rightCh)
                {
                    minusToken = minusToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                        FeaturesResources.x_y_range_in_reverse_order,
                        minusToken.GetSpan()));
                }

                components.Add(new RegexCharacterClassRangeNode(left, minusToken, right));
            }
        }
        else
        {
            components.Add(left);
        }
    }

    private static bool IsEscapedMinus([NotNullWhen(true)] RoutePatternNode? node)
        => node is RegexSimpleEscapeNode simple && IsTextChar(simple.TypeToken, '-');

    private bool TryGetRangeComponentValue(RegexExpressionNode component, out int ch)
    {
        // Don't bother examining the component if it has any errors already.  This also means
        // we don't have to worry about running into invalid escape sequences and the like.
        if (!HasProblem(component))
        {
            return TryGetRangeComponentValueWorker(component, out ch);
        }

        ch = default;
        return false;
    }

    private bool TryGetRangeComponentValueWorker(RoutePatternNode component, out int ch)
    {
        switch (component.Kind)
        {
            case RoutePatternKind.SimpleEscape:
                throw new NotImplementedException();
                //var escapeNode = (RegexSimpleEscapeNode)component;
                //ch = MapEscapeChar(escapeNode.TypeToken.VirtualChars[0]).Value;
                //return true;

            case RoutePatternKind.ControlEscape:
                var controlEscape = (RegexControlEscapeNode)component;
                var controlCh = controlEscape.ControlToken.VirtualChars[0].Value;

                // \ca interpreted as \cA
                if (controlCh is >= 'a' and <= 'z')
                {
                    controlCh -= (char)('a' - 'A');
                }

                // The control characters have values mapping from the A-Z range to numeric
                // values 1-26.  So, to map that, we subtract 'A' from the value (which would
                // give us 0-25) and then add '1' back to it.
                ch = controlCh - 'A' + 1;
                return true;

            case RoutePatternKind.OctalEscape:
                ch = GetCharValue(((RegexOctalEscapeNode)component).OctalText, withBase: 8);
                return true;

            case RoutePatternKind.HexEscape:
                ch = GetCharValue(((RegexHexEscapeNode)component).HexText, withBase: 16);
                return true;

            case RoutePatternKind.UnicodeEscape:
                ch = GetCharValue(((RegexUnicodeEscapeNode)component).HexText, withBase: 16);
                return true;

            case RoutePatternKind.PosixProperty:
                // When the native parser sees [:...:] it treats this as if it just saw '[' and skipped the 
                // rest.
                ch = '[';
                return true;

            case RoutePatternKind.Text:
                ch = ((RegexTextNode)component).TextToken.VirtualChars[0].Value;
                return true;

            case RoutePatternKind.Sequence:
                var sequence = (RegexSequenceNode)component;
#if DEBUG
                Debug.Assert(sequence.ChildCount > 0);
                for (int i = 0, n = sequence.ChildCount - 1; i < n; i++)
                {
                    Debug.Assert(IsEscapedMinus(sequence.ChildAt(i).Node));
                }
#endif

                var last = sequence.ChildAt(sequence.ChildCount - 1).Node;
                if (last == null)
                {
                    throw new InvalidOperationException("Unexpected null.");
                }
                if (IsEscapedMinus(last))
                {
                    break;
                }

                return TryGetRangeComponentValueWorker(last, out ch);
        }

        ch = default;
        return false;
    }

    private static int GetCharValue(RoutePatternToken hexText, int withBase)
    {
        unchecked
        {
            var total = 0;
            foreach (var vc in hexText.VirtualChars)
            {
                total *= withBase;
                total += HexValue(vc);
            }

            return total;
        }
    }

    private static int HexValue(AspNetCoreVirtualChar ch)
    {
        Debug.Assert(RoutePatternLexer.IsHexChar(ch));
        unchecked
        {
            var temp = ch.Value - '0';
            if (temp is >= 0 and <= 9)
            {
                return temp;
            }

            temp = ch.Value - 'a';
            if (temp is >= 0 and <= 5)
            {
                return temp + 10;
            }

            temp = ch.Value - 'A';
            if (temp is >= 0 and <= 5)
            {
                return temp + 10;
            }
        }

        throw new InvalidOperationException();
    }

    private static bool HasProblem(RoutePatternNodeOrToken component)
    {
        if (component.IsNode)
        {
            foreach (var child in component.Node)
            {
                if (HasProblem(child))
                {
                    return true;
                }
            }
        }
        else
        {
            var token = component.Token;
            if (token.IsMissing ||
                token.Diagnostics.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    private RegexPrimaryExpressionNode ParseSingleCharacterClassComponent(bool isFirst, bool afterRangeMinus)
    {
        if (_currentToken.Kind == RoutePatternKind.BackslashToken && _lexer.Position < _lexer.Text.Length)
        {
            var backslashToken = _currentToken;

            // trivia is not allowed anywhere in a character class, and definitely not between
            // a \ and the following character.
            ConsumeCurrentToken();
            Debug.Assert(_currentToken.VirtualChars.Length == 1);

            var nextChar = _currentToken.VirtualChars[0];
            switch (nextChar.Value)
            {
                case 'D':
                case 'd':
                case 'S':
                case 's':
                case 'W':
                case 'w':
                case 'p':
                case 'P':
                    if (afterRangeMinus)
                    {
                        backslashToken = backslashToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                            string.Format(FeaturesResources.Cannot_include_class_0_in_character_range, nextChar),
                            GetSpan(backslashToken, _currentToken)));
                    }

                    // move back before the character we just scanned.
                    // trivia is not allowed anywhere in a character class.

                    // The above list are character class and category escapes.  ParseEscape can
                    // handle both of those, so we just defer to it.
                    _lexer.Position--;
                    return ParseEscape(backslashToken);

                case '-':
                    // trivia is not allowed anywhere in a character class.

                    // We just let the basic consumption code pull out a token for us, we then
                    // convert that to text since we treat all characters after the - as text no
                    // matter what.
                    return new RegexSimpleEscapeNode(
                        backslashToken, ConsumeCurrentToken().With(kind: RoutePatternKind.TextToken));

                default:
                    // trivia is not allowed anywhere in a character class.

                    // Note: it is very intentional that we're calling ParseCharEscape and not
                    // ParseEscape.  Normal escapes are not interpreted the same way inside a
                    // character class.  For example \b is not an anchor in a character class.
                    // And things like \k'...' are not k-captures, etc. etc.  
                    _lexer.Position--;
                    return ParseCharEscape(backslashToken);
            }
        }

        if (!afterRangeMinus &&
            !isFirst &&
            _currentToken.Kind == RoutePatternKind.MinusToken &&
            _lexer.IsAt("["))
        {
            // have a trailing subtraction.
            // trivia is not allowed anywhere in a character class
            return ParseCharacterClassSubtractionNode(
                ConsumeCurrentToken());
        }

        // From the .NET regex code:
        // This is code for Posix style properties - [:Ll:] or [:IsTibetan:].
        // It currently doesn't do anything other than skip the whole thing!
        if (!afterRangeMinus && _currentToken.Kind == RoutePatternKind.OpenBracketToken && _lexer.IsAt(":"))
        {
            var beforeBracketPos = _lexer.Position - 1;
            // trivia is not allowed anywhere in a character class
            ConsumeCurrentToken();

            var captureName = _lexer.TryScanCaptureName();
            if (captureName.HasValue && _lexer.IsAt(":]"))
            {
                _lexer.Position += 2;
                var textChars = _lexer.GetSubPattern(beforeBracketPos, _lexer.Position);
                var token = CreateToken(RoutePatternKind.TextToken, textChars);

                // trivia is not allowed anywhere in a character class
                ConsumeCurrentToken();
                return new RegexPosixPropertyNode(token);
            }
            else
            {
                // Reset to back where we were.
                // trivia is not allowed anywhere in a character class
                _lexer.Position = beforeBracketPos;
                ConsumeCurrentToken();
                Debug.Assert(_currentToken.Kind == RoutePatternKind.OpenBracketToken);
            }
        }

        // trivia is not allowed anywhere in a character class
        return new RegexTextNode(
            ConsumeCurrentToken().With(kind: RoutePatternKind.TextToken));
    }

    private RegexPrimaryExpressionNode ParseCharacterClassSubtractionNode(RoutePatternToken minusToken)
    {
        var charClass = ParseCharacterClass();

        if (_currentToken.Kind is not RoutePatternKind.CloseBracketToken and not RoutePatternKind.EndOfFile)
        {
            minusToken = minusToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.A_subtraction_must_be_the_last_element_in_a_character_class,
                GetTokenStartPositionSpan(minusToken)));
        }

        return new RegexCharacterClassSubtractionNode(minusToken, charClass);
    }

    /// <summary>
    /// Parses out an escape sequence.  Escape sequences are allowed in top level sequences
    /// and in character classes.  In a top level sequence trivia will be allowed afterwards,
    /// but in a character class trivia is not allowed afterwards.
    /// </summary>
    private RegexEscapeNode ParseEscape(RoutePatternToken backslashToken)
    {
        Debug.Assert(_lexer.Text[_lexer.Position - 1].Value == '\\');

        // No spaces between \ and next char.
        ConsumeCurrentToken();

        if (_currentToken.Kind == RoutePatternKind.EndOfFile)
        {
            backslashToken = backslashToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Illegal_backslash_at_end_of_pattern,
                backslashToken.GetSpan()));
            return new RegexSimpleEscapeNode(backslashToken, CreateMissingToken(RoutePatternKind.TextToken));
        }

        Debug.Assert(_currentToken.VirtualChars.Length == 1);
        switch (_currentToken.VirtualChars[0].Value)
        {
            case 'b':
            case 'B':
            case 'A':
            case 'G':
            case 'Z':
            case 'z':
                return new RegexAnchorEscapeNode(
                    backslashToken, ConsumeCurrentToken());

            case 'w':
            case 'W':
            case 's':
            case 'S':
            case 'd':
            case 'D':
                return new RegexCharacterClassEscapeNode(
                    backslashToken, ConsumeCurrentToken());

            case 'p':
            case 'P':
                return ParseCategoryEscape(backslashToken);
        }

        // Move back to after the backslash
        _lexer.Position--;
        return ParseBasicBackslash(backslashToken);
    }

    private RegexEscapeNode ParseBasicBackslash(RoutePatternToken backslashToken)
    {
        Debug.Assert(_lexer.Text[_lexer.Position - 1].Value == '\\');

        // No spaces between \ and next char.
        ConsumeCurrentToken();

        if (_currentToken.Kind == RoutePatternKind.EndOfFile)
        {
            backslashToken = backslashToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Illegal_backslash_at_end_of_pattern,
                backslashToken.GetSpan()));
            return new RegexSimpleEscapeNode(backslashToken, CreateMissingToken(RoutePatternKind.TextToken));
        }

        Debug.Assert(_currentToken.VirtualChars.Length == 1);
        var ch = _currentToken.VirtualChars[0];
        if (ch.Value == 'k')
        {
            return ParsePossibleKCaptureEscape(backslashToken);
        }

        if (ch.Value is '<' or '\'')
        {
            _lexer.Position--;
            return ParsePossibleCaptureEscape(backslashToken);
        }

        if (ch.Value is >= '1' and <= '9')
        {
            _lexer.Position--;
            return ParsePossibleBackreferenceEscape(backslashToken);
        }

        _lexer.Position--;
        return ParseCharEscape(backslashToken);
    }

    private RegexEscapeNode ParsePossibleBackreferenceEscape(RoutePatternToken backslashToken)
    {
        Debug.Assert(_lexer.Text[_lexer.Position - 1].Value == '\\');
        return ParsePossibleRegularBackreferenceEscape(backslashToken);
    }

    private RegexEscapeNode ParsePossibleRegularBackreferenceEscape(
        RoutePatternToken backslashToken)
    {
        Debug.Assert(_lexer.Text[_lexer.Position - 1].Value == '\\');
        var start = _lexer.Position;

        var number = _lexer.TryScanNumber();
        if (number == null)
        {
            throw new InvalidOperationException("Unexpected null.");
        }
        var numberToken = number.Value;
        var capVal = (int)numberToken.Value;
        if (HasCapture(capVal) ||
            capVal <= 9)
        {
            CheckCapture(ref numberToken);

            ConsumeCurrentToken();
            return new RegexBackreferenceEscapeNode(backslashToken, numberToken);
        }

        _lexer.Position = start;
        return ParseCharEscape(backslashToken);
    }

    private RegexEscapeNode ParsePossibleCaptureEscape(RoutePatternToken backslashToken)
    {
        Debug.Assert(_lexer.Text[_lexer.Position - 1].Value == '\\');
        Debug.Assert(_lexer.Text[_lexer.Position].Value is '<' or '\'');

        var afterBackslashPosition = _lexer.Position;
        ScanCaptureParts(out var openToken, out var capture, out var closeToken);

        if (openToken.IsMissing || capture.IsMissing || closeToken.IsMissing)
        {
            _lexer.Position = afterBackslashPosition;
            return ParseCharEscape(backslashToken);
        }

        return new RegexCaptureEscapeNode(
            backslashToken, openToken, capture, closeToken);
    }

    private RegexEscapeNode ParsePossibleKCaptureEscape(RoutePatternToken backslashToken)
    {
        var typeToken = _currentToken;
        var afterBackslashPosition = _lexer.Position - @"k".Length;

        ScanCaptureParts(out var openToken, out var capture, out var closeToken);
        if (openToken.IsMissing)
        {
            backslashToken = backslashToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Malformed_named_back_reference,
                GetSpan(backslashToken, typeToken)));
            return new RegexSimpleEscapeNode(backslashToken, typeToken.With(kind: RoutePatternKind.TextToken));
        }

        if (capture.IsMissing || closeToken.IsMissing)
        {
            // Native parser falls back to normal escape scanning, if it doesn't see a capture,
            // or close brace.  For normal .NET regexes, this will then fail later (as \k is not
            // a legal escape), but will succeed for ecmascript regexes.
            _lexer.Position = afterBackslashPosition;
            return ParseCharEscape(backslashToken);
        }

        return new RegexKCaptureEscapeNode(
            backslashToken, typeToken, openToken, capture, closeToken);
    }

    private void ScanCaptureParts(out RoutePatternToken openToken, out RoutePatternToken capture, out RoutePatternToken closeToken)
    {
        openToken = CreateMissingToken(RoutePatternKind.LessThanToken);
        capture = CreateMissingToken(RoutePatternKind.CaptureNameToken);
        closeToken = CreateMissingToken(RoutePatternKind.GreaterThanToken);

        // No trivia allowed in <cap> or 'cap'
        ConsumeCurrentToken();

        if (_lexer.Position < _lexer.Text.Length &&
            (_currentToken.Kind == RoutePatternKind.LessThanToken || _currentToken.Kind == RoutePatternKind.SingleQuoteToken))
        {
            openToken = _currentToken;
        }
        else
        {
            return;
        }

        var captureToken = _lexer.TryScanNumberOrCaptureName();
        capture = captureToken == null
            ? CreateMissingToken(RoutePatternKind.CaptureNameToken)
            : captureToken.Value;

        // No trivia allowed in <cap> or 'cap'
        ConsumeCurrentToken();
        closeToken = CreateMissingToken(RoutePatternKind.GreaterThanToken);

        if (!capture.IsMissing &&
            (openToken.Kind == RoutePatternKind.LessThanToken && _currentToken.Kind == RoutePatternKind.GreaterThanToken ||
             openToken.Kind == RoutePatternKind.SingleQuoteToken && _currentToken.Kind == RoutePatternKind.SingleQuoteToken))
        {
            CheckCapture(ref capture);
            closeToken = ConsumeCurrentToken();
        }
    }

    private RegexEscapeNode ParseCharEscape(RoutePatternToken backslashToken)
    {
        Debug.Assert(_lexer.Text[_lexer.Position - 1].Value == '\\');

        // no trivia between \ and the next char
        ConsumeCurrentToken();
        Debug.Assert(_currentToken.VirtualChars.Length == 1);

        var ch = _currentToken.VirtualChars[0];
        if (ch.Value is >= '0' and <= '7')
        {
            _lexer.Position--;
            var octalDigits = _lexer.ScanOctalCharacters(_options);
            Debug.Assert(octalDigits.VirtualChars.Length > 0);

            ConsumeCurrentToken();
            return new RegexOctalEscapeNode(backslashToken, octalDigits);
        }

        switch (ch.Value)
        {
            case 'a':
            case 'b':
            case 'e':
            case 'f':
            case 'n':
            case 'r':
            case 't':
            case 'v':
                return new RegexSimpleEscapeNode(
                    backslashToken, ConsumeCurrentToken());
            case 'x':
                return ParseHexEscape(backslashToken);
            case 'u':
                return ParseUnicodeEscape(backslashToken);
            case 'c':
                return ParseControlEscape(backslashToken);
            default:
                var typeToken = ConsumeCurrentToken().With(kind: RoutePatternKind.TextToken);

                if (RegexCharClass.IsBoundaryWordChar(ch))
                {
                    typeToken = typeToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                        string.Format(FeaturesResources.Unrecognized_escape_sequence_0, ch),
                        typeToken.GetSpan()));
                }

                return new RegexSimpleEscapeNode(backslashToken, typeToken);
        }
    }

    private RegexEscapeNode ParseUnicodeEscape(RoutePatternToken backslashToken)
    {
        var typeToken = _currentToken;
        var hexChars = _lexer.ScanHexCharacters(4);
        ConsumeCurrentToken();
        return new RegexUnicodeEscapeNode(backslashToken, typeToken, hexChars);
    }

    private RegexEscapeNode ParseHexEscape(RoutePatternToken backslashToken)
    {
        var typeToken = _currentToken;
        var hexChars = _lexer.ScanHexCharacters(2);
        ConsumeCurrentToken();
        return new RegexHexEscapeNode(backslashToken, typeToken, hexChars);
    }

    private RegexControlEscapeNode ParseControlEscape(RoutePatternToken backslashToken)
    {
        // Nothing allowed between \c and the next char
        var typeToken = ConsumeCurrentToken();

        if (_currentToken.Kind == RoutePatternKind.EndOfFile)
        {
            typeToken = typeToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Missing_control_character,
                typeToken.GetSpan()));
            return new RegexControlEscapeNode(backslashToken, typeToken, CreateMissingToken(RoutePatternKind.TextToken));
        }

        Debug.Assert(_currentToken.VirtualChars.Length == 1);

        var ch = _currentToken.VirtualChars[0].Value;

        unchecked
        {
            // From: https://github.com/dotnet/corefx/blob/80e220fc7009de0f0611ee6b52d4d5ffd25eb6c7/src/System.Text.RegularExpressions/src/System/Text/RegularExpressions/RegexParser.cs#L1450

            // Note: Roslyn accepts a control escape that current .NET parser does not.
            // Specifically: \c[
            //
            // It is a bug that the .NET parser does not support this construct.  The bug was
            // reported at: https://github.com/dotnet/corefx/issues/26501 and was fixed for
            // CoreFx with https://github.com/dotnet/corefx/commit/80e220fc7009de0f0611ee6b52d4d5ffd25eb6c7
            //
            // Because it was a bug, we follow the correct behavior.  That means we will not
            // report a diagnostic for a Regex that someone might run on a previous version of
            // .NET that ends up throwing at runtime.  That's acceptable.  Our goal is to match
            // the latest .NET 'correct' behavior.  Not intermediary points with bugs that have
            // since been fixed.

            // \ca interpreted as \cA
            if (ch is >= 'a' and <= 'z')
            {
                ch -= (char)('a' - 'A');
            }

            if (ch is >= '@' and <= '_')
            {
                var controlToken = ConsumeCurrentToken().With(kind: RoutePatternKind.TextToken);
                return new RegexControlEscapeNode(backslashToken, typeToken, controlToken);
            }
            else
            {
                typeToken = typeToken.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                    FeaturesResources.Unrecognized_control_character,
                    _currentToken.GetSpan()));

                // Don't consume the bogus control character.
                return new RegexControlEscapeNode(backslashToken, typeToken, CreateMissingToken(RoutePatternKind.TextToken));
            }
        }
    }

    private RegexEscapeNode ParseCategoryEscape(RoutePatternToken backslash)
    {
        Debug.Assert(_lexer.Text[_lexer.Position - 1] is var ch && (ch.Value == 'P' || ch.Value == 'p'));
        var typeToken = _currentToken;

        var start = _lexer.Position;

        if (!TryGetCategoryEscapeParts(
                out var openBraceToken,
                out var categoryToken,
                out var closeBraceToken,
                out var message))
        {
            ResetToPositionAndConsumeCurrentToken(start);
            typeToken = typeToken.With(kind: RoutePatternKind.TextToken).AddDiagnosticIfNone(new EmbeddedDiagnostic(
                message, GetSpan(backslash, typeToken)));
            return new RegexSimpleEscapeNode(backslash, typeToken);
        }

        return new RegexCategoryEscapeNode(backslash, typeToken, openBraceToken, categoryToken, closeBraceToken);
    }

    private bool TryGetCategoryEscapeParts(
        out RoutePatternToken openBraceToken,
        out RoutePatternToken categoryToken,
        out RoutePatternToken closeBraceToken,
        [NotNullWhen(false)] out string? message)
    {
        openBraceToken = default;
        categoryToken = default;
        closeBraceToken = default;
        message = null;

        if (_lexer.Text.Length - _lexer.Position < "{x}".Length)
        {
            message = FeaturesResources.Incomplete_character_escape;
            return false;
        }

        // no whitespace in \p{x}
        ConsumeCurrentToken();

        if (_currentToken.Kind != RoutePatternKind.OpenBraceToken)
        {
            message = FeaturesResources.Malformed_character_escape;
            return false;
        }

        openBraceToken = _currentToken;
        var category = _lexer.TryScanEscapeCategory();

        // no whitespace in \p{x}
        ConsumeCurrentToken();
        if (_currentToken.Kind != RoutePatternKind.CloseBraceToken)
        {
            message = FeaturesResources.Incomplete_character_escape;
            return false;
        }

        if (category == null)
        {
            message = FeaturesResources.Unknown_property;
            return false;
        }

        categoryToken = category.Value;
        closeBraceToken = ConsumeCurrentToken();
        return true;
    }

    private RegexTextNode ParseUnexpectedQuantifier(RegexExpressionNode? lastExpression)
    {
        // This is just a bogus element in the higher level sequence.  Allow trivia 
        // after this to abide by the spirit of the native parser.
        var token = ConsumeCurrentToken();
        CheckQuantifierExpression(lastExpression, ref token);
        return new RegexTextNode(token.With(kind: RoutePatternKind.TextToken));
    }

    private static void CheckQuantifierExpression(RegexExpressionNode? current, ref RoutePatternToken token)
    {
        if (current == null ||
            current.Kind == RoutePatternKind.SimpleOptionsGrouping)
        {
            token = token.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Quantifier_x_y_following_nothing, token.GetSpan()));
        }
        else if (current is RegexQuantifierNode or RegexLazyQuantifierNode)
        {
            token = token.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                string.Format(FeaturesResources.Nested_quantifier_0, token.VirtualChars.First()), token.GetSpan()));
        }
    }
    */
}
