// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

using RoutePatternNodeOrToken = EmbeddedSyntaxNodeOrToken<NewRoutePatternKind, NewRoutePatternNode>;
using RoutePatternToken = EmbeddedSyntaxToken<NewRoutePatternKind>;

internal sealed class RoutePatternCompilationUnit : NewRoutePatternNode
{
    public RoutePatternCompilationUnit(ImmutableArray<NewRoutePatternRootPartNode> parts, RoutePatternToken endOfFileToken)
        : base(NewRoutePatternKind.CompilationUnit)
    {
        Debug.Assert(parts != null);
        Debug.Assert(endOfFileToken.Kind == NewRoutePatternKind.EndOfFile);
        Parts = parts;
        EndOfFileToken = endOfFileToken;
    }

    public ImmutableArray<NewRoutePatternRootPartNode> Parts { get; }
    public RoutePatternToken EndOfFileToken { get; }

    internal override int ChildCount => Parts.Length + 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
    {
        if (index == Parts.Length)
        {
            return EndOfFileToken;
        }

        return Parts[index];
    }

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal sealed class RoutePatternSegmentNode : NewRoutePatternRootPartNode
{
    public ImmutableArray<NewRoutePatternNode> Children { get; }

    internal override int ChildCount => Children.Length;

    public RoutePatternSegmentNode(ImmutableArray<NewRoutePatternNode> children)
        : base(NewRoutePatternKind.Segment)
    {
        Children = children;
    }

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => Children[index];

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// [controller]
/// </summary>
internal class RoutePatternReplacementNode : NewRoutePatternNode
{
    protected RoutePatternReplacementNode(
        RoutePatternToken openBracketToken, RoutePatternToken textToken, RoutePatternToken closeBracketToken)
        : base(NewRoutePatternKind.Replacement)
    {
        Debug.Assert(openBracketToken.Kind == NewRoutePatternKind.OpenBracketToken);
        Debug.Assert(textToken.Kind == NewRoutePatternKind.TextToken);
        Debug.Assert(closeBracketToken.Kind == NewRoutePatternKind.CloseBracketToken);
        OpenBracketToken = openBracketToken;
        TextToken = textToken;
        CloseBracketToken = closeBracketToken;
    }

    public RoutePatternToken OpenBracketToken { get; }
    public RoutePatternToken TextToken { get; }
    public RoutePatternToken CloseBracketToken { get; }

    internal override int ChildCount => 3;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => OpenBracketToken,
            1 => TextToken,
            2 => CloseBracketToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

/// <summary>
/// [controller]
/// </summary>
internal class RoutePatternParameterNode : NewRoutePatternSegmentPartNode
{
    public RoutePatternParameterNode(
        RoutePatternToken openBraceToken, ImmutableArray<NewRoutePatternParameterPartNode> parameterPartNodes, RoutePatternToken closeBraceToken)
        : base(NewRoutePatternKind.Parameter)
    {
        Debug.Assert(openBraceToken.Kind == NewRoutePatternKind.OpenBraceToken);
        Debug.Assert(closeBraceToken.Kind == NewRoutePatternKind.CloseBraceToken);
        OpenBraceToken = openBraceToken;
        ParameterParts = parameterPartNodes;
        CloseBraceToken = closeBraceToken;
    }

    public RoutePatternToken OpenBraceToken { get; }
    public ImmutableArray<NewRoutePatternParameterPartNode> ParameterParts { get; }
    public RoutePatternToken CloseBraceToken { get; }

    internal override int ChildCount => ParameterParts.Length + 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
    {
        if (index == 0)
        {
            return OpenBraceToken;
        }
        else if (index == ParameterParts.Length + 1)
        {
            return CloseBraceToken;
        }
        else
        {
            return ParameterParts[index - 1];
        }
    }

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal sealed class RoutePatternLiteralNode : NewRoutePatternSegmentPartNode
{
    public RoutePatternLiteralNode(RoutePatternToken literalToken)
        : base(NewRoutePatternKind.Literal)
    {
        Debug.Assert(literalToken.Kind == NewRoutePatternKind.Literal);
        LiteralToken = literalToken;
    }

    public RoutePatternToken LiteralToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => LiteralToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal sealed class RoutePatternOptionalSeperatorNode : NewRoutePatternSegmentPartNode
{
    public RoutePatternOptionalSeperatorNode(RoutePatternToken seperatorToken)
        : base(NewRoutePatternKind.Seperator)
    {
        Debug.Assert(seperatorToken.Kind == NewRoutePatternKind.DotToken);
        SeperatorToken = seperatorToken;
    }

    public RoutePatternToken SeperatorToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => SeperatorToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal sealed class RoutePatternSegmentSeperatorNode : NewRoutePatternRootPartNode
{
    public RoutePatternSegmentSeperatorNode(RoutePatternToken seperatorToken)
        : base(NewRoutePatternKind.Seperator)
    {
        Debug.Assert(seperatorToken.Kind == NewRoutePatternKind.SlashToken);
        SeperatorToken = seperatorToken;
    }

    public RoutePatternToken SeperatorToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => SeperatorToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal sealed class RoutePatternCatchAllParameterPartNode : NewRoutePatternParameterPartNode
{
    public RoutePatternCatchAllParameterPartNode(RoutePatternToken asteriskToken)
        : base(NewRoutePatternKind.CatchAll)
    {
        Debug.Assert(asteriskToken.Kind == NewRoutePatternKind.AsteriskToken);
        AsteriskToken = asteriskToken;
    }

    public RoutePatternToken AsteriskToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => AsteriskToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal sealed class RoutePatternOptionalParameterPartNode : NewRoutePatternParameterPartNode
{
    public RoutePatternOptionalParameterPartNode(RoutePatternToken questionMarkToken)
        : base(NewRoutePatternKind.Optional)
    {
        Debug.Assert(questionMarkToken.Kind == NewRoutePatternKind.QuestionMarkToken);
        QuestionMarkToken = questionMarkToken;
    }

    public RoutePatternToken QuestionMarkToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => QuestionMarkToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal sealed class RoutePatternDefaultValueParameterPartNode : NewRoutePatternParameterPartNode
{
    public RoutePatternDefaultValueParameterPartNode(RoutePatternToken equalsToken, RoutePatternToken defaultValueToken)
        : base(NewRoutePatternKind.DefaultValue)
    {
        Debug.Assert(equalsToken.Kind == NewRoutePatternKind.EqualsToken);
        Debug.Assert(defaultValueToken.Kind == NewRoutePatternKind.DefaultValueToken);
        EqualsToken = equalsToken;
        DefaultValueToken = defaultValueToken;
    }

    public RoutePatternToken EqualsToken { get; }
    public RoutePatternToken DefaultValueToken { get; }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => EqualsToken,
            1 => DefaultValueToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal sealed class RoutePatternNameParameterPartNode : NewRoutePatternParameterPartNode
{
    public RoutePatternNameParameterPartNode(RoutePatternToken parameterNameToken)
        : base(NewRoutePatternKind.ParameterName)
    {
        Debug.Assert(parameterNameToken.Kind == NewRoutePatternKind.ParameterNameToken);
        ParameterNameToken = parameterNameToken;
    }

    public RoutePatternToken ParameterNameToken { get; }

    internal override int ChildCount => 1;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => ParameterNameToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal class RoutePatternPolicyParameterPartNode : NewRoutePatternParameterPartNode
{
    public RoutePatternPolicyParameterPartNode(RoutePatternToken colonToken, RoutePatternToken policyNameToken)
        : base(NewRoutePatternKind.ParameterPolicy)
    {
        Debug.Assert(colonToken.Kind == NewRoutePatternKind.ColonToken);
        Debug.Assert(policyNameToken.Kind == NewRoutePatternKind.PolicyNameToken);
        ColonToken = colonToken;
        PolicyNameToken = policyNameToken;
    }

    public RoutePatternToken ColonToken { get; }
    public RoutePatternToken PolicyNameToken { get; }

    internal override int ChildCount => 2;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => ColonToken,
            1 => PolicyNameToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal sealed class RoutePatternPolicyWithArgumentsParameterPartNode : RoutePatternPolicyParameterPartNode
{
    public RoutePatternPolicyWithArgumentsParameterPartNode(
        RoutePatternToken colonToken, RoutePatternToken policyNameToken, RoutePatternToken openParenToken, RoutePatternToken argumentToken, RoutePatternToken closeParenToken)
        : base(colonToken, policyNameToken)
    {
        Debug.Assert(openParenToken.Kind == NewRoutePatternKind.OpenParenToken);
        Debug.Assert(argumentToken.Kind == NewRoutePatternKind.PolicyArgumentToken);
        Debug.Assert(closeParenToken.Kind == NewRoutePatternKind.CloseParenToken);
        OpenParenToken = openParenToken;
        CloseParenToken = closeParenToken;
        ArgumentToken = argumentToken;
    }

    public RoutePatternToken OpenParenToken { get; }
    public RoutePatternToken ArgumentToken { get; }
    public RoutePatternToken CloseParenToken { get; }

    internal override int ChildCount => base.ChildCount + 3;

    internal override RoutePatternNodeOrToken ChildAt(int index)
        => index switch
        {
            0 => ColonToken,
            1 => PolicyNameToken,
            2 => OpenParenToken,
            3 => ArgumentToken,
            4 => CloseParenToken,
            _ => throw new InvalidOperationException(),
        };

    public override void Accept(INewRoutePatternNodeVisitor visitor)
        => visitor.Visit(this);
}

internal abstract class NewRoutePatternRootPartNode : NewRoutePatternNode
{
    protected NewRoutePatternRootPartNode(NewRoutePatternKind kind)
        : base(kind)
    {
    }
}

internal abstract class NewRoutePatternSegmentPartNode : NewRoutePatternNode
{
    protected NewRoutePatternSegmentPartNode(NewRoutePatternKind kind)
        : base(kind)
    {
    }
}

internal abstract class NewRoutePatternParameterPartNode : NewRoutePatternNode
{
    protected NewRoutePatternParameterPartNode(NewRoutePatternKind kind)
        : base(kind)
    {
    }
}
