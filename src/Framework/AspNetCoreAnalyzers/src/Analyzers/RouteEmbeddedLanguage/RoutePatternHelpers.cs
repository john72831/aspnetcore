// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Collections.Immutable;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

using RoutePatternToken = EmbeddedSyntaxToken<NewRoutePatternKind>;

internal static class RoutePatternHelpers
{
    public static RoutePatternToken CreateToken(NewRoutePatternKind kind, AspNetCoreVirtualCharSequence virtualChars)
        => new(kind, virtualChars, ImmutableArray<EmbeddedDiagnostic>.Empty, value: null);

    public static RoutePatternToken CreateMissingToken(NewRoutePatternKind kind)
        => CreateToken(kind, AspNetCoreVirtualCharSequence.Empty);

    // Old
    public static EmbeddedSyntaxToken<RoutePatternKind> CreateToken(RoutePatternKind kind, AspNetCoreVirtualCharSequence virtualChars)
    => new(kind, virtualChars, ImmutableArray<EmbeddedDiagnostic>.Empty, value: null);

    public static EmbeddedSyntaxToken<RoutePatternKind> CreateMissingToken(RoutePatternKind kind)
        => CreateToken(kind, AspNetCoreVirtualCharSequence.Empty);

    ///// <summary>
    ///// Maps an escaped character to the actual character it was escaping.  For something like
    ///// 'a' this will map to actual '\a' char (the bell character).  However, for something like
    ///// '(' this will just map to '(' as that's all that \( does in a regex.
    ///// </summary>
    //public static AspNetCoreVirtualChar MapEscapeChar(AspNetCoreVirtualChar ch)
    //    => ch.Value switch
    //    {
    //        'a' => AspNetCoreVirtualChar.Create(new Rune('\u0007'), ch.Span),    // bell
    //        'b' => AspNetCoreVirtualChar.Create(new Rune('\b'), ch.Span),        // backspace
    //        'e' => AspNetCoreVirtualChar.Create(new Rune('\u001B'), ch.Span),    // escape
    //        'f' => AspNetCoreVirtualChar.Create(new Rune('\f'), ch.Span),        // form feed
    //        'n' => AspNetCoreVirtualChar.Create(new Rune('\n'), ch.Span),        // new line
    //        'r' => AspNetCoreVirtualChar.Create(new Rune('\r'), ch.Span),        // carriage return
    //        't' => AspNetCoreVirtualChar.Create(new Rune('\t'), ch.Span),        // tab
    //        'v' => AspNetCoreVirtualChar.Create(new Rune('\u000B'), ch.Span),    // vertical tab
    //        _ => ch,
    //    };

    //public static bool IsSelfEscape(this RegexSimpleEscapeNode node)
    //{
    //    if (node.TypeToken.VirtualChars.Length > 0)
    //    {
    //        var ch = node.TypeToken.VirtualChars[0];
    //        return MapEscapeChar(ch).DeepEquals(ch);
    //    }

    //    return true;
    //}
}
