// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using Microsoft;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

internal enum RoutePatternKind
{
    None = 0,
    EndOfFile,
    Sequence,
    CompilationUnit,
    Text,
    StartAnchor,
    EndAnchor,
    Alternation,
    Wildcard,
    CharacterClass,
    NegatedCharacterClass,
    CharacterClassRange,
    CharacterClassSubtraction,
    PosixProperty,

    ZeroOrMoreQuantifier,
    OneOrMoreQuantifier,
    ZeroOrOneQuantifier,
    ExactNumericQuantifier,
    OpenRangeNumericQuantifier,
    ClosedRangeNumericQuantifier,
    LazyQuantifier,

    SimpleGrouping,
    SimpleOptionsGrouping,
    NestedOptionsGrouping,
    NonCapturingGrouping,
    PositiveLookaheadGrouping,
    NegativeLookaheadGrouping,
    PositiveLookbehindGrouping,
    NegativeLookbehindGrouping,
    AtomicGrouping,
    CaptureGrouping,
    BalancingGrouping,
    ConditionalCaptureGrouping,
    ConditionalExpressionGrouping,

    SimpleEscape,
    AnchorEscape,
    CharacterClassEscape,
    CategoryEscape,
    ControlEscape,
    HexEscape,
    UnicodeEscape,
    OctalEscape,
    CaptureEscape,
    KCaptureEscape,
    BackreferenceEscape,

    // Tokens
    DollarToken,
    OpenBraceToken,
    CloseBraceToken,
    OpenBracketToken,
    CloseBracketToken,
    OpenParenToken,
    CloseParenToken,
    BarToken,
    DotToken,
    CaretToken,
    TextToken,
    QuestionToken,
    AsteriskToken,
    PlusToken,
    CommaToken,
    BackslashToken,
    ColonToken,
    EqualsToken,
    ExclamationToken,
    GreaterThanToken,
    LessThanToken,
    MinusToken,
    SingleQuoteToken,

    // Special multi-character tokens that have to be explicitly requested.
    OptionsToken,
    NumberToken,
    CaptureNameToken,
    EscapeCategoryToken,

    // Trivia
    CommentTrivia,
    WhitespaceTrivia,
}
