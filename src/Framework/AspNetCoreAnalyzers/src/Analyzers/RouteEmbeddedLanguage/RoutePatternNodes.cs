// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

using RoutePatternAlternatingSequenceList = EmbeddedSeparatedSyntaxNodeList<RoutePatternKind, RoutePatternNode, RegexSequenceNode>;
using RoutePatternNodeOrToken = EmbeddedSyntaxNodeOrToken<RoutePatternKind, RoutePatternNode>;
using RoutePatternToken = EmbeddedSyntaxToken<RoutePatternKind>;

internal sealed class RegexCompilationUnit : RoutePatternNode
{
    public RegexCompilationUnit(RegexExpressionNode expression, RoutePatternToken endOfFileToken)
        : base(RoutePatternKind.CompilationUnit)
    {
        Debug.Assert(expression != null);
        Debug.Assert(endOfFileToken.Kind == RoutePatternKind.EndOfFile);
        Expression = expression;
        EndOfFileToken = endOfFileToken;
    }

    public RegexExpressionNode Expression { get; }
    public RoutePatternToken EndOfFileToken { get; }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => Expression,
            1 => EndOfFileToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Represents a possibly-empty sequence of regex expressions.  For example, the regex ""
/// will produce an empty RegexSequence nodes, and "a|" will produce an alternation with an
/// empty sequence on the right side.  Having a node represent the empty sequence is actually
/// appropriate as these are legal regexes and the empty sequence represents 'a pattern
/// that will match any position'.  Not having a node for this would actually end up 
/// complicating things in terms of dealing with nulls in the tree.
/// 
/// This does not deviate from Roslyn principles.  While nodes for empty text are rare, they
/// are allowed (for example, OmittedTypeArgument in C#).
/// </summary>
internal sealed class RegexSequenceNode : RegexExpressionNode
{
    public ImmutableArray<RegexExpressionNode> Children { get; }

    internal override int ChildCount => Children.Length;

    public RegexSequenceNode(ImmutableArray<RegexExpressionNode> children)
        : base(RoutePatternKind.Sequence)
    {
        Children = children;
    }

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => Children[index];

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Represents a chunk of text (usually just a single char) from the original pattern.
/// </summary>
internal sealed class RegexTextNode : RegexPrimaryExpressionNode
{
    public RegexTextNode(RoutePatternToken textToken)
        : base(RoutePatternKind.Text)
    {
        Debug.Assert(textToken.Kind == RoutePatternKind.TextToken);
        TextToken = textToken;
    }

    public RoutePatternToken TextToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => TextToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Base type for [...] and [^...] character classes.
/// </summary>
internal abstract class RegexBaseCharacterClassNode : RegexPrimaryExpressionNode
{
    protected RegexBaseCharacterClassNode(
        RoutePatternKind kind, RoutePatternToken openBracketToken, RegexSequenceNode components, RoutePatternToken closeBracketToken)
        : base(kind)
    {
        Debug.Assert(openBracketToken.Kind == RoutePatternKind.OpenBracketToken);
        Debug.Assert(components != null);
        Debug.Assert(closeBracketToken.Kind == RoutePatternKind.CloseBracketToken);
        OpenBracketToken = openBracketToken;
        Components = components;
        CloseBracketToken = closeBracketToken;
    }

    public RoutePatternToken OpenBracketToken { get; }
    public RegexSequenceNode Components { get; }
    public RoutePatternToken CloseBracketToken { get; }
}

/// <summary>
/// [...] node.
/// </summary>
internal sealed class RegexCharacterClassNode : RegexBaseCharacterClassNode
{
    public RegexCharacterClassNode(
        RoutePatternToken openBracketToken, RegexSequenceNode components, RoutePatternToken closeBracketToken)
        : base(RoutePatternKind.CharacterClass, openBracketToken, components, closeBracketToken)
    {
    }

    internal override int ChildCount => 3;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenBracketToken,
            1 => Components,
            2 => CloseBracketToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// [^...] node
/// </summary>
internal sealed class RegexNegatedCharacterClassNode : RegexBaseCharacterClassNode
{
    public RegexNegatedCharacterClassNode(
        RoutePatternToken openBracketToken, RoutePatternToken caretToken, RegexSequenceNode components, RoutePatternToken closeBracketToken)
        : base(RoutePatternKind.NegatedCharacterClass, openBracketToken, components, closeBracketToken)
    {
        Debug.Assert(caretToken.Kind == RoutePatternKind.CaretToken);
        CaretToken = caretToken;
    }

    public RoutePatternToken CaretToken { get; }

    internal override int ChildCount => 4;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenBracketToken,
            1 => CaretToken,
            2 => Components,
            3 => CloseBracketToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```a-z``` node in a character class.
/// </summary>
internal sealed class RegexCharacterClassRangeNode : RegexPrimaryExpressionNode
{
    public RegexCharacterClassRangeNode(
        RegexExpressionNode left, RoutePatternToken minusToken, RegexExpressionNode right)
        : base(RoutePatternKind.CharacterClassRange)
    {
        Debug.Assert(left != null);
        Debug.Assert(minusToken.Kind == RoutePatternKind.MinusToken);
        Debug.Assert(right != null);
        Left = left;
        MinusToken = minusToken;
        Right = right;
    }

    public RegexExpressionNode Left { get; }
    public RoutePatternToken MinusToken { get; }
    public RegexExpressionNode Right { get; }

    internal override int ChildCount => 3;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => Left,
            1 => MinusToken,
            2 => Right,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```-[f-m]``` in a pattern like ```[a-z-[f-m]]```.  A subtraction must come last in a 
/// character class, and removes some range of chars from the character class built up
/// so far.
/// </summary>
internal sealed class RegexCharacterClassSubtractionNode : RegexPrimaryExpressionNode
{
    public RegexCharacterClassSubtractionNode(
        RoutePatternToken minusToken, RegexBaseCharacterClassNode characterClass)
        : base(RoutePatternKind.CharacterClassSubtraction)
    {
        Debug.Assert(minusToken.Kind == RoutePatternKind.MinusToken);
        Debug.Assert(characterClass != null);
        MinusToken = minusToken;
        CharacterClass = characterClass;
    }

    public RoutePatternToken MinusToken { get; }
    public RegexBaseCharacterClassNode CharacterClass { get; }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => MinusToken,
            1 => CharacterClass,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Represents a ```[:...:]``` node in a character class.  Note: the .NET regex parser
/// simply treats this as the character ```[``` and ignores the rest of the ```:...:]```.
/// They latter part has no impact on the actual match engine that is produced.
/// </summary>
internal sealed class RegexPosixPropertyNode : RegexPrimaryExpressionNode
{
    public RegexPosixPropertyNode(RoutePatternToken textToken)
        : base(RoutePatternKind.PosixProperty)
    {
        Debug.Assert(textToken.Kind == RoutePatternKind.TextToken);
        TextToken = textToken;
    }

    public RoutePatternToken TextToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => TextToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Root of all expression nodes.
/// </summary>
internal abstract class RegexExpressionNode : RoutePatternNode
{
    protected RegexExpressionNode(RoutePatternKind kind)
        : base(kind)
    {
    }
}

/// <summary>
/// Root of all the primary nodes (similar to unary nodes in C#).
/// </summary>
internal abstract class RegexPrimaryExpressionNode : RegexExpressionNode
{
    protected RegexPrimaryExpressionNode(RoutePatternKind kind)
        : base(kind)
    {
    }
}

/// <summary>
/// A ```.``` expression.
/// </summary>
internal sealed class RegexWildcardNode : RegexPrimaryExpressionNode
{
    public RegexWildcardNode(RoutePatternToken dotToken)
        : base(RoutePatternKind.Wildcard)
    {
        Debug.Assert(dotToken.Kind == RoutePatternKind.DotToken);
        DotToken = dotToken;
    }

    public RoutePatternToken DotToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => DotToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Root of all quantifier nodes: ```?```, ```*``` etc.
/// </summary>
internal abstract class RegexQuantifierNode : RegexExpressionNode
{
    protected RegexQuantifierNode(RoutePatternKind kind)
        : base(kind)
    {
    }
}

/// <summary>
/// ```expr*```
/// </summary>
internal sealed class RegexZeroOrMoreQuantifierNode : RegexQuantifierNode
{
    public RegexZeroOrMoreQuantifierNode(
        RegexExpressionNode expression, RoutePatternToken asteriskToken)
        : base(RoutePatternKind.ZeroOrMoreQuantifier)
    {
        Debug.Assert(expression != null);
        Debug.Assert(asteriskToken.Kind == RoutePatternKind.AsteriskToken);
        Expression = expression;
        AsteriskToken = asteriskToken;
    }

    public RegexExpressionNode Expression { get; }
    public RoutePatternToken AsteriskToken { get; }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => Expression,
            1 => AsteriskToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```expr+```
/// </summary>
internal sealed class RegexOneOrMoreQuantifierNode : RegexQuantifierNode
{
    public RegexOneOrMoreQuantifierNode(
        RegexExpressionNode expression, RoutePatternToken plusToken)
        : base(RoutePatternKind.OneOrMoreQuantifier)
    {
        Debug.Assert(expression != null);
        Debug.Assert(plusToken.Kind == RoutePatternKind.PlusToken);
        Expression = expression;
        PlusToken = plusToken;
    }

    public RegexExpressionNode Expression { get; }
    public RoutePatternToken PlusToken { get; }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => Expression,
            1 => PlusToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```expr?```
/// </summary>
internal sealed class RegexZeroOrOneQuantifierNode : RegexQuantifierNode
{
    public RegexZeroOrOneQuantifierNode(
        RegexExpressionNode expression, RoutePatternToken questionToken)
        : base(RoutePatternKind.ZeroOrOneQuantifier)
    {
        Debug.Assert(expression != null);
        Debug.Assert(questionToken.Kind == RoutePatternKind.QuestionToken);
        Expression = expression;
        QuestionToken = questionToken;
    }

    public RegexExpressionNode Expression { get; }
    public RoutePatternToken QuestionToken { get; }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => Expression,
            1 => QuestionToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Quantifiers can be optionally followed by a ? to make them lazy.  i.e. ```a*?``` or ```a+?```.
/// You can even have ```a??```  (zero or one 'a', lazy).  However, only one lazy modifier is allowed
/// ```a*??``` or ```a???``` is not allowed.
/// </summary>
internal sealed class RegexLazyQuantifierNode : RegexExpressionNode
{
    public RegexLazyQuantifierNode(
        RegexQuantifierNode quantifier, RoutePatternToken questionToken)
        : base(RoutePatternKind.LazyQuantifier)
    {
        Debug.Assert(quantifier != null);
        Debug.Assert(quantifier.Kind != RoutePatternKind.LazyQuantifier);
        Debug.Assert(questionToken.Kind == RoutePatternKind.QuestionToken);
        Quantifier = quantifier;
        QuestionToken = questionToken;
    }

    public RegexQuantifierNode Quantifier { get; }
    public RoutePatternToken QuestionToken { get; }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => Quantifier,
            1 => QuestionToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Base type of all regex numeric quantifier nodes.  i.e.  
/// ```a{5}```,  ```a{5,}``` and ```a{5,10}```
/// </summary>
internal abstract class RegexNumericQuantifierNode : RegexQuantifierNode
{
    protected RegexNumericQuantifierNode(
        RoutePatternKind kind, RegexPrimaryExpressionNode expression, RoutePatternToken openBraceToken, RoutePatternToken firstNumberToken, RoutePatternToken closeBraceToken)
        : base(kind)
    {
        Debug.Assert(expression != null);
        Debug.Assert(openBraceToken.Kind == RoutePatternKind.OpenBraceToken);
        Debug.Assert(firstNumberToken.Kind == RoutePatternKind.NumberToken);
        Debug.Assert(closeBraceToken.Kind == RoutePatternKind.CloseBraceToken);
        Expression = expression;
        OpenBraceToken = openBraceToken;
        FirstNumberToken = firstNumberToken;
        CloseBraceToken = closeBraceToken;
    }

    public RegexExpressionNode Expression { get; }
    public RoutePatternToken OpenBraceToken { get; }
    public RoutePatternToken FirstNumberToken { get; }
    public RoutePatternToken CloseBraceToken { get; }
}

/// <summary>
/// ```a{5}```
/// </summary>
internal sealed class RegexExactNumericQuantifierNode : RegexNumericQuantifierNode
{
    public RegexExactNumericQuantifierNode(
        RegexPrimaryExpressionNode expression, RoutePatternToken openBraceToken, RoutePatternToken numberToken, RoutePatternToken closeBraceToken)
        : base(RoutePatternKind.ExactNumericQuantifier, expression, openBraceToken, numberToken, closeBraceToken)
    {
    }

    internal override int ChildCount => 4;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => Expression,
            1 => OpenBraceToken,
            2 => FirstNumberToken,
            3 => CloseBraceToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```a{5,}```
/// </summary>
internal sealed class RegexOpenNumericRangeQuantifierNode : RegexNumericQuantifierNode
{
    public RegexOpenNumericRangeQuantifierNode(
        RegexPrimaryExpressionNode expression,
        RoutePatternToken openBraceToken, RoutePatternToken firstNumberToken,
        RoutePatternToken commaToken, RoutePatternToken closeBraceToken)
        : base(RoutePatternKind.OpenRangeNumericQuantifier, expression, openBraceToken, firstNumberToken, closeBraceToken)
    {
        Debug.Assert(commaToken.Kind == RoutePatternKind.CommaToken);
        CommaToken = commaToken;
    }

    public RoutePatternToken CommaToken { get; }

    internal override int ChildCount => 5;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => Expression,
            1 => OpenBraceToken,
            2 => FirstNumberToken,
            3 => CommaToken,
            4 => CloseBraceToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```a{5,10}```
/// </summary>
internal sealed class RegexClosedNumericRangeQuantifierNode : RegexNumericQuantifierNode
{
    public RegexClosedNumericRangeQuantifierNode(
        RegexPrimaryExpressionNode expression,
        RoutePatternToken openBraceToken, RoutePatternToken firstNumberToken,
        RoutePatternToken commaToken, RoutePatternToken secondNumberToken, RoutePatternToken closeBraceToken)
        : base(RoutePatternKind.ClosedRangeNumericQuantifier, expression, openBraceToken, firstNumberToken, closeBraceToken)
    {
        Debug.Assert(commaToken.Kind == RoutePatternKind.CommaToken);
        Debug.Assert(secondNumberToken.Kind == RoutePatternKind.NumberToken);
        CommaToken = commaToken;
        SecondNumberToken = secondNumberToken;
    }

    public RoutePatternToken CommaToken { get; }
    public RoutePatternToken SecondNumberToken { get; }

    internal override int ChildCount => 6;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => Expression,
            1 => OpenBraceToken,
            2 => FirstNumberToken,
            3 => CommaToken,
            4 => SecondNumberToken,
            5 => CloseBraceToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```$``` or ```^```.
/// </summary>
internal sealed class RegexAnchorNode : RegexPrimaryExpressionNode
{
    public RegexAnchorNode(RoutePatternKind kind, RoutePatternToken anchorToken)
        : base(kind)
    {
        Debug.Assert(anchorToken.Kind is RoutePatternKind.DollarToken or RoutePatternKind.CaretToken);
        AnchorToken = anchorToken;
    }

    public RoutePatternToken AnchorToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => AnchorToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```expr1|expr2``` node.
/// </summary>
internal sealed class RegexAlternationNode : RegexExpressionNode
{
    public RegexAlternationNode(RoutePatternAlternatingSequenceList sequenceList)
        : base(RoutePatternKind.Alternation)
    {
        Debug.Assert(sequenceList.NodesAndTokens.Length > 0);
        for (var i = 1; i < sequenceList.NodesAndTokens.Length; i += 2)
        {
            Debug.Assert(sequenceList.NodesAndTokens[i].Kind == RoutePatternKind.BarToken);
        }

        SequenceList = sequenceList;
    }

    public RoutePatternAlternatingSequenceList SequenceList { get; }

    internal override int ChildCount => SequenceList.NodesAndTokens.Length;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => SequenceList.NodesAndTokens[index];

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Base type of all non-trivia ```(...)``` nodes
/// </summary>
internal abstract class RegexGroupingNode : RegexPrimaryExpressionNode
{
    protected RegexGroupingNode(RoutePatternKind kind, RoutePatternToken openParenToken, RoutePatternToken closeParenToken)
        : base(kind)
    {
        Debug.Assert(openParenToken.Kind == RoutePatternKind.OpenParenToken);
        Debug.Assert(closeParenToken.Kind == RoutePatternKind.CloseParenToken);
        OpenParenToken = openParenToken;
        CloseParenToken = closeParenToken;
    }

    public RoutePatternToken OpenParenToken { get; }
    public RoutePatternToken CloseParenToken { get; }
}

/// <summary>
/// The ```(...)``` node you get when the group does not start with ```(?```
/// </summary>
internal sealed class RegexSimpleGroupingNode : RegexGroupingNode
{
    public RegexSimpleGroupingNode(RoutePatternToken openParenToken, RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.SimpleGrouping, openParenToken, closeParenToken)
    {
        Debug.Assert(expression != null);
        Expression = expression;
    }

    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 3;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => Expression,
            2 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Base type of all ```(?...)``` groupings.
/// </summary>
internal abstract class RegexQuestionGroupingNode : RegexGroupingNode
{
    protected RegexQuestionGroupingNode(RoutePatternKind kind, RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken closeParenToken)
        : base(kind, openParenToken, closeParenToken)
    {
        Debug.Assert(questionToken.Kind == RoutePatternKind.QuestionToken);
        QuestionToken = questionToken;
    }

    public RoutePatternToken QuestionToken { get; }
}

/// <summary>
/// Base type of ```(?inmsx)``` or ```(?inmsx:...)``` nodes.
/// </summary>
internal abstract class RegexOptionsGroupingNode : RegexQuestionGroupingNode
{
    protected RegexOptionsGroupingNode(RoutePatternKind kind, RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken optionsToken, RoutePatternToken closeParenToken)
        : base(kind, openParenToken, questionToken, closeParenToken)
    {
        OptionsToken = optionsToken;
    }

    public RoutePatternToken OptionsToken { get; }
}

/// <summary>
/// ```(?inmsx)``` node.  Changes options in a sequence for all subsequence nodes.
/// </summary>
internal sealed class RegexSimpleOptionsGroupingNode : RegexOptionsGroupingNode
{
    public RegexSimpleOptionsGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken optionsToken, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.SimpleOptionsGrouping, openParenToken, questionToken, optionsToken, closeParenToken)
    {
    }

    internal override int ChildCount => 4;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => OptionsToken,
            3 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```(?inmsx:expr)``` node.  Changes options for the parsing of 'expr'.
/// </summary>
internal sealed class RegexNestedOptionsGroupingNode : RegexOptionsGroupingNode
{
    public RegexNestedOptionsGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken optionsToken,
        RoutePatternToken colonToken, RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.NestedOptionsGrouping, openParenToken, questionToken, optionsToken, closeParenToken)
    {
        Debug.Assert(colonToken.Kind == RoutePatternKind.ColonToken);
        Debug.Assert(expression != null);
        ColonToken = colonToken;
        Expression = expression;
    }

    public RoutePatternToken ColonToken { get; }
    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 6;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => OptionsToken,
            3 => ColonToken,
            4 => Expression,
            5 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```(?:expr)``` node.
/// </summary>
internal sealed class RegexNonCapturingGroupingNode : RegexQuestionGroupingNode
{
    public RegexNonCapturingGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken colonToken,
        RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.NonCapturingGrouping, openParenToken, questionToken, closeParenToken)
    {
        Debug.Assert(colonToken.Kind == RoutePatternKind.ColonToken);
        Debug.Assert(expression != null);
        ColonToken = colonToken;
        Expression = expression;
    }

    public RoutePatternToken ColonToken { get; }
    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 5;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => ColonToken,
            3 => Expression,
            4 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```(?=expr)``` node.
/// </summary>
internal sealed class RegexPositiveLookaheadGroupingNode : RegexQuestionGroupingNode
{
    public RegexPositiveLookaheadGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken equalsToken,
        RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.PositiveLookaheadGrouping, openParenToken, questionToken, closeParenToken)
    {
        Debug.Assert(equalsToken.Kind == RoutePatternKind.EqualsToken);
        Debug.Assert(expression != null);
        EqualsToken = equalsToken;
        Expression = expression;
    }

    public RoutePatternToken EqualsToken { get; }
    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 5;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => EqualsToken,
            3 => Expression,
            4 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```(?!expr)``` node.
/// </summary>
internal sealed class RegexNegativeLookaheadGroupingNode : RegexQuestionGroupingNode
{
    public RegexNegativeLookaheadGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken exclamationToken,
        RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.NegativeLookaheadGrouping, openParenToken, questionToken, closeParenToken)
    {
        Debug.Assert(exclamationToken.Kind == RoutePatternKind.ExclamationToken);
        Debug.Assert(expression != null);
        ExclamationToken = exclamationToken;
        Expression = expression;
    }

    public RoutePatternToken ExclamationToken { get; }
    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 5;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => ExclamationToken,
            3 => Expression,
            4 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal abstract class RegexLookbehindGroupingNode : RegexQuestionGroupingNode
{
    protected RegexLookbehindGroupingNode(
        RoutePatternKind kind, RoutePatternToken openParenToken, RoutePatternToken questionToken,
        RoutePatternToken lessThanToken, RoutePatternToken closeParenToken)
        : base(kind, openParenToken, questionToken, closeParenToken)
    {
        Debug.Assert(lessThanToken.Kind == RoutePatternKind.LessThanToken);
        LessThanToken = lessThanToken;
    }

    public RoutePatternToken LessThanToken { get; }
}

/// <summary>
/// ```(?&lt;=expr)``` node.
/// </summary>
internal sealed class RegexPositiveLookbehindGroupingNode : RegexLookbehindGroupingNode
{
    public RegexPositiveLookbehindGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken lessThanToken,
        RoutePatternToken equalsToken, RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.PositiveLookbehindGrouping, openParenToken, questionToken, lessThanToken, closeParenToken)
    {
        Debug.Assert(equalsToken.Kind == RoutePatternKind.EqualsToken);
        Debug.Assert(expression != null);
        EqualsToken = equalsToken;
        Expression = expression;
    }

    public RoutePatternToken EqualsToken { get; }
    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 6;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => LessThanToken,
            3 => EqualsToken,
            4 => Expression,
            5 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```(?&lt;!expr)``` node.
/// </summary>
internal sealed class RegexNegativeLookbehindGroupingNode : RegexLookbehindGroupingNode
{
    public RegexNegativeLookbehindGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken lessThanToken,
        RoutePatternToken exclamationToken, RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.NegativeLookbehindGrouping, openParenToken, questionToken, lessThanToken, closeParenToken)
    {
        Debug.Assert(exclamationToken.Kind == RoutePatternKind.ExclamationToken);
        Debug.Assert(expression != null);
        ExclamationToken = exclamationToken;
        Expression = expression;
    }

    public RoutePatternToken ExclamationToken { get; }
    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 6;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => LessThanToken,
            3 => ExclamationToken,
            4 => Expression,
            5 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```(?&gt;expr)``` node.
/// </summary>
internal sealed class RegexAtomicGroupingNode : RegexQuestionGroupingNode
{
    public RegexAtomicGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken greaterThanToken,
        RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.AtomicGrouping, openParenToken, questionToken, closeParenToken)
    {
        Debug.Assert(greaterThanToken.Kind == RoutePatternKind.GreaterThanToken);
        Debug.Assert(expression != null);
        GreaterThanToken = greaterThanToken;
        Expression = expression;
    }

    public RoutePatternToken GreaterThanToken { get; }
    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 5;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => GreaterThanToken,
            3 => Expression,
            4 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```(?'name'expr)``` or ```(?&lt;name&gt;expr)``` node.
/// </summary>
internal sealed class RegexCaptureGroupingNode : RegexQuestionGroupingNode
{
    public RegexCaptureGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken openToken,
        RoutePatternToken captureToken, RoutePatternToken closeToken,
        RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.CaptureGrouping, openParenToken, questionToken, closeParenToken)
    {
        Debug.Assert(expression != null);
        OpenToken = openToken;
        CaptureToken = captureToken;
        CloseToken = closeToken;
        Expression = expression;
    }

    public RoutePatternToken OpenToken { get; }
    public RoutePatternToken CaptureToken { get; }
    public RoutePatternToken CloseToken { get; }
    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 7;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => OpenToken,
            3 => CaptureToken,
            4 => CloseToken,
            5 => Expression,
            6 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```(?'name1-name2'expr)``` or ```(?&lt;name1-name2&gt;expr)``` node.
/// </summary>
internal sealed class RegexBalancingGroupingNode : RegexQuestionGroupingNode
{
    public RegexBalancingGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken, RoutePatternToken openToken,
        RoutePatternToken firstCaptureToken, RoutePatternToken minusToken, RoutePatternToken secondCaptureToken,
        RoutePatternToken closeToken, RegexExpressionNode expression, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.BalancingGrouping, openParenToken, questionToken, closeParenToken)
    {
        Debug.Assert(minusToken.Kind == RoutePatternKind.MinusToken);
        Debug.Assert(expression != null);
        OpenToken = openToken;
        FirstCaptureToken = firstCaptureToken;
        MinusToken = minusToken;
        SecondCaptureToken = secondCaptureToken;
        CloseToken = closeToken;
        Expression = expression;
    }

    public RoutePatternToken OpenToken { get; }
    public RoutePatternToken FirstCaptureToken { get; }
    public RoutePatternToken MinusToken { get; }
    public RoutePatternToken SecondCaptureToken { get; }
    public RoutePatternToken CloseToken { get; }
    public RegexExpressionNode Expression { get; }

    internal override int ChildCount => 9;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => OpenToken,
            3 => FirstCaptureToken,
            4 => MinusToken,
            5 => SecondCaptureToken,
            6 => CloseToken,
            7 => Expression,
            8 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal abstract class RegexConditionalGroupingNode : RegexQuestionGroupingNode
{
    protected RegexConditionalGroupingNode(
        RoutePatternKind kind, RoutePatternToken openParenToken, RoutePatternToken questionToken,
        RegexExpressionNode result, RoutePatternToken closeParenToken)
        : base(kind, openParenToken, questionToken, closeParenToken)
    {
        Debug.Assert(result != null);
        Result = result;
    }

    public RegexExpressionNode Result { get; }
}

/// <summary>
/// ```(?(capture_name)result)```
/// </summary>
internal sealed class RegexConditionalCaptureGroupingNode : RegexConditionalGroupingNode
{
    public RegexConditionalCaptureGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken,
        RoutePatternToken innerOpenParenToken, RoutePatternToken captureToken, RoutePatternToken innerCloseParenToken,
        RegexExpressionNode result, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.ConditionalCaptureGrouping, openParenToken, questionToken, result, closeParenToken)
    {
        Debug.Assert(innerOpenParenToken.Kind == RoutePatternKind.OpenParenToken);
        Debug.Assert(innerCloseParenToken.Kind == RoutePatternKind.CloseParenToken);
        InnerOpenParenToken = innerOpenParenToken;
        CaptureToken = captureToken;
        InnerCloseParenToken = innerCloseParenToken;
    }

    public RoutePatternToken InnerOpenParenToken { get; }
    public RoutePatternToken CaptureToken { get; }
    public RoutePatternToken InnerCloseParenToken { get; }

    internal override int ChildCount => 7;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => InnerOpenParenToken,
            3 => CaptureToken,
            4 => InnerCloseParenToken,
            5 => Result,
            6 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```(?(group)result)```
/// </summary>
internal sealed class RegexConditionalExpressionGroupingNode : RegexConditionalGroupingNode
{
    public RegexConditionalExpressionGroupingNode(
        RoutePatternToken openParenToken, RoutePatternToken questionToken,
        RegexGroupingNode grouping,
        RegexExpressionNode result, RoutePatternToken closeParenToken)
        : base(RoutePatternKind.ConditionalExpressionGrouping, openParenToken, questionToken, result, closeParenToken)
    {
        Debug.Assert(grouping != null);
        Grouping = grouping;
    }

    internal override int ChildCount => 5;

    public RegexGroupingNode Grouping { get; }

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenParenToken,
            1 => QuestionToken,
            2 => Grouping,
            3 => Result,
            4 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// Base type of all regex primitives that start with \
/// </summary>
internal abstract class RegexEscapeNode : RegexPrimaryExpressionNode
{
    protected RegexEscapeNode(RoutePatternKind kind, RoutePatternToken backslashToken) : base(kind)
    {
        Debug.Assert(backslashToken.Kind == RoutePatternKind.BackslashToken);
        BackslashToken = backslashToken;
    }

    public RoutePatternToken BackslashToken { get; }
}

/// <summary>
/// Base type of all regex escapes that start with \ and some informative character (like \v \t \c etc.).
/// </summary>
internal abstract class RegexTypeEscapeNode : RegexEscapeNode
{
    protected RegexTypeEscapeNode(RoutePatternKind kind, RoutePatternToken backslashToken, RoutePatternToken typeToken)
        : base(kind, backslashToken)
    {
        TypeToken = typeToken;
    }

    public RoutePatternToken TypeToken { get; }
}

/// <summary>
/// A basic escape that just has \ and one additional character and needs no further information.
/// </summary>
internal sealed class RegexSimpleEscapeNode : RegexTypeEscapeNode
{
    public RegexSimpleEscapeNode(RoutePatternToken backslashToken, RoutePatternToken typeToken)
        : base(RoutePatternKind.SimpleEscape, backslashToken, typeToken)
    {
        Debug.Assert(typeToken.Kind == RoutePatternKind.TextToken);
    }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => TypeToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// One of \b \B \A \G \z \Z
/// </summary>
internal sealed class RegexAnchorEscapeNode : RegexTypeEscapeNode
{
    public RegexAnchorEscapeNode(RoutePatternToken backslashToken, RoutePatternToken typeToken)
        : base(RoutePatternKind.AnchorEscape, backslashToken, typeToken)
    {
    }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => TypeToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// One of \s \S \d \D \w \W
/// </summary>
internal sealed class RegexCharacterClassEscapeNode : RegexTypeEscapeNode
{
    public RegexCharacterClassEscapeNode(RoutePatternToken backslashToken, RoutePatternToken typeToken)
        : base(RoutePatternKind.CharacterClassEscape, backslashToken, typeToken)
    {
    }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => TypeToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```\cX``` escape
/// </summary>
internal sealed class RegexControlEscapeNode : RegexTypeEscapeNode
{
    public RegexControlEscapeNode(RoutePatternToken backslashToken, RoutePatternToken typeToken, RoutePatternToken controlToken)
        : base(RoutePatternKind.ControlEscape, backslashToken, typeToken)
    {
        ControlToken = controlToken;
    }

    internal override int ChildCount => 3;

    public RoutePatternToken ControlToken { get; }

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => TypeToken,
            2 => ControlToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```\xFF``` escape.
/// </summary>
internal sealed class RegexHexEscapeNode : RegexTypeEscapeNode
{
    public RegexHexEscapeNode(RoutePatternToken backslashToken, RoutePatternToken typeToken, RoutePatternToken hexText)
        : base(RoutePatternKind.HexEscape, backslashToken, typeToken)
    {
        HexText = hexText;
    }

    internal override int ChildCount => 3;

    public RoutePatternToken HexText { get; }

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => TypeToken,
            2 => HexText,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```\uFFFF``` escape.
/// </summary>
internal sealed class RegexUnicodeEscapeNode : RegexTypeEscapeNode
{
    public RegexUnicodeEscapeNode(RoutePatternToken backslashToken, RoutePatternToken typeToken, RoutePatternToken hexText)
        : base(RoutePatternKind.UnicodeEscape, backslashToken, typeToken)
    {
        HexText = hexText;
    }

    internal override int ChildCount => 3;

    public RoutePatternToken HexText { get; }

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => TypeToken,
            2 => HexText,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```\'name'``` or ```\&lt;name&gt;``` escape.
/// </summary>
internal sealed class RegexCaptureEscapeNode : RegexEscapeNode
{
    public RegexCaptureEscapeNode(
        RoutePatternToken backslashToken, RoutePatternToken openToken, RoutePatternToken captureToken, RoutePatternToken closeToken)
        : base(RoutePatternKind.CaptureEscape, backslashToken)
    {
        OpenToken = openToken;
        CaptureToken = captureToken;
        CloseToken = closeToken;
    }

    internal override int ChildCount => 4;

    public RoutePatternToken OpenToken { get; }
    public RoutePatternToken CaptureToken { get; }
    public RoutePatternToken CloseToken { get; }

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => OpenToken,
            2 => CaptureToken,
            3 => CloseToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```\k'name'``` or ```\k&lt;name&gt;``` escape.
/// </summary>
internal sealed class RegexKCaptureEscapeNode : RegexTypeEscapeNode
{
    public RegexKCaptureEscapeNode(
        RoutePatternToken backslashToken, RoutePatternToken typeToken,
        RoutePatternToken openToken, RoutePatternToken captureToken, RoutePatternToken closeToken)
        : base(RoutePatternKind.KCaptureEscape, backslashToken, typeToken)
    {
        OpenToken = openToken;
        CaptureToken = captureToken;
        CloseToken = closeToken;
    }

    internal override int ChildCount => 5;

    public RoutePatternToken OpenToken { get; }
    public RoutePatternToken CaptureToken { get; }
    public RoutePatternToken CloseToken { get; }

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => TypeToken,
            2 => OpenToken,
            3 => CaptureToken,
            4 => CloseToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```\1``` escape. In contexts where back-references are not allowed.
/// </summary>
internal sealed class RegexOctalEscapeNode : RegexEscapeNode
{
    public RegexOctalEscapeNode(RoutePatternToken backslashToken, RoutePatternToken octalText)
        : base(RoutePatternKind.OctalEscape, backslashToken)
    {
        OctalText = octalText;
    }

    internal override int ChildCount => 2;

    public RoutePatternToken OctalText { get; }

    internal override RoutePatternNodeOrToken ChildAt(int index)
    {
        switch (index)
        {
            case 0: return BackslashToken;
            case 1: return OctalText;
        }

        throw new InvalidOperationException();
    }

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```\1```
/// </summary>
internal sealed class RegexBackreferenceEscapeNode : RegexEscapeNode
{
    public RegexBackreferenceEscapeNode(RoutePatternToken backslashToken, RoutePatternToken numberToken)
        : base(RoutePatternKind.BackreferenceEscape, backslashToken)
    {
        NumberToken = numberToken;
    }

    internal override int ChildCount => 2;

    public RoutePatternToken NumberToken { get; }

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => NumberToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// ```\p{...}```
/// </summary>
internal sealed class RegexCategoryEscapeNode : RegexEscapeNode
{
    public RegexCategoryEscapeNode(
        RoutePatternToken backslashToken, RoutePatternToken typeToken, RoutePatternToken openBraceToken, RoutePatternToken categoryToken, RoutePatternToken closeBraceToken)
        : base(RoutePatternKind.CategoryEscape, backslashToken)
    {
        Debug.Assert(openBraceToken.Kind == RoutePatternKind.OpenBraceToken);
        Debug.Assert(closeBraceToken.Kind == RoutePatternKind.CloseBraceToken);
        TypeToken = typeToken;
        OpenBraceToken = openBraceToken;
        CategoryToken = categoryToken;
        CloseBraceToken = closeBraceToken;
    }

    public RoutePatternToken TypeToken { get; }
    public RoutePatternToken OpenBraceToken { get; }
    public RoutePatternToken CategoryToken { get; }
    public RoutePatternToken CloseBraceToken { get; }

    internal override int ChildCount => 5;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => BackslashToken,
            1 => TypeToken,
            2 => OpenBraceToken,
            3 => CategoryToken,
            4 => CloseBraceToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(IRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}
