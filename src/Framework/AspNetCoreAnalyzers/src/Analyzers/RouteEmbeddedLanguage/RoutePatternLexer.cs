// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common;
using Microsoft.CodeAnalysis.EmbeddedLanguages.VirtualChars;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

using static RoutePatternHelpers;

using RoutePatternToken = EmbeddedSyntaxToken<RoutePatternKind>;

/// <summary>
/// Produces tokens from the sequence of <see cref="VirtualChar"/> characters.  Unlike the
/// native C# and VB lexer, this lexer is much more tightly controlled by the parser.  For
/// example, while C# can have trivia on virtual every token, the same is not true for
/// RegexTokens.  As such, instead of automatically lexing out tokens to make them available for
/// the parser, the parser asks for each token as necessary passing the right information to
/// indicate which types and shapes of tokens are allowed.
///
/// The tight coupling means that the parser is allowed direct control of the position of the
/// lexer.
///
/// Note: most of the time, tokens returned are just a single character long, including for long
/// sequences of text characters (like ```"goo"```).  This is just three <see
/// cref="RegexTextNode"/>s in a row (each containing a <see cref="RoutePatternKind.TextToken"/> a
/// single character long).
///
/// There are multi-character tokens though.  For example ```10``` in ```a{10,}``` or ```name```
/// in ```\k'name'```
/// </summary>
internal struct RoutePatternLexer
{
    public readonly AspNetCoreVirtualCharSequence Text;
    public int Position;

    public RoutePatternLexer(AspNetCoreVirtualCharSequence text) : this()
        => Text = text;

    public AspNetCoreVirtualChar CurrentChar => Position < Text.Length ? Text[Position] : default;

    public AspNetCoreVirtualCharSequence GetSubPatternToCurrentPos(int start)
        => GetSubPattern(start, Position);

    public AspNetCoreVirtualCharSequence GetSubPattern(int start, int end)
        => Text.GetSubSequence(TextSpan.FromBounds(start, end));

    public RoutePatternToken ScanNextToken()
    {
        if (Position == Text.Length)
        {
            return CreateToken(RoutePatternKind.EndOfFile, AspNetCoreVirtualCharSequence.Empty);
        }

        var ch = CurrentChar;
        Position++;

        return CreateToken(GetKind(ch), Text.GetSubSequence(new TextSpan(Position - 1, 1)));
    }

    private static RoutePatternKind GetKind(AspNetCoreVirtualChar ch)
        => ch.Value switch
        {
            '|' => RoutePatternKind.BarToken,
            '*' => RoutePatternKind.AsteriskToken,
            '+' => RoutePatternKind.PlusToken,
            '?' => RoutePatternKind.QuestionToken,
            '{' => RoutePatternKind.OpenBraceToken,
            '}' => RoutePatternKind.CloseBraceToken,
            '\\' => RoutePatternKind.BackslashToken,
            '[' => RoutePatternKind.OpenBracketToken,
            ']' => RoutePatternKind.CloseBracketToken,
            '.' => RoutePatternKind.DotToken,
            '^' => RoutePatternKind.CaretToken,
            '$' => RoutePatternKind.DollarToken,
            '(' => RoutePatternKind.OpenParenToken,
            ')' => RoutePatternKind.CloseParenToken,
            ',' => RoutePatternKind.CommaToken,
            ':' => RoutePatternKind.ColonToken,
            '=' => RoutePatternKind.EqualsToken,
            '!' => RoutePatternKind.ExclamationToken,
            '<' => RoutePatternKind.LessThanToken,
            '>' => RoutePatternKind.GreaterThanToken,
            '-' => RoutePatternKind.MinusToken,
            '\'' => RoutePatternKind.SingleQuoteToken,
            _ => RoutePatternKind.TextToken,
        };

    public TextSpan GetTextSpan(int startInclusive, int endExclusive)
        => TextSpan.FromBounds(Text[startInclusive].Span.Start, Text[endExclusive - 1].Span.End);

    public bool IsAt(string val)
        => TextAt(Position, val);

    private bool TextAt(int position, string val)
    {
        for (var i = 0; i < val.Length; i++)
        {
            if (position + i >= Text.Length ||
                Text[position + i].Value != val[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBlank(AspNetCoreVirtualChar ch)
    {
        // List taken from the native regex parser.
        switch (ch.Value)
        {
            case '\u0009':
            case '\u000A':
            case '\u000C':
            case '\u000D':
            case ' ':
                return true;
            default:
                return false;
        }
    }

    public RoutePatternToken? TryScanEscapeCategory()
    {
        var start = Position;
        while (Position < Text.Length &&
               IsEscapeCategoryChar(CurrentChar))
        {
            Position++;
        }

        if (Position == start)
        {
            return null;
        }

        var token = CreateToken(RoutePatternKind.EscapeCategoryToken, GetSubPatternToCurrentPos(start));
        var category = token.VirtualChars.CreateString();

        if (!RegexCharClass.IsEscapeCategory(category))
        {
            token = token.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                string.Format(FeaturesResources.Unknown_property_0, category),
                token.GetSpan()));
        }

        return token;
    }

    private static bool IsEscapeCategoryChar(AspNetCoreVirtualChar ch)
        => ch.Value is '-' or
           >= 'a' and <= 'z' or
           >= 'A' and <= 'Z';

    public RoutePatternToken? TryScanNumber()
    {
        if (Position == Text.Length)
        {
            return null;
        }

        const int MaxValueDiv10 = int.MaxValue / 10;
        const int MaxValueMod10 = int.MaxValue % 10;

        var value = 0;
        var start = Position;
        var error = false;
        while (Position < Text.Length && CurrentChar is var ch && IsDecimalDigit(ch))
        {
            Position++;

            unchecked
            {
                var charVal = ch.Value - '0';
                if (value > MaxValueDiv10 || value == MaxValueDiv10 && charVal > MaxValueMod10)
                {
                    error = true;
                }

                value *= 10;
                value += charVal;
            }
        }

        if (Position == start)
        {
            return null;
        }

        var token = CreateToken(RoutePatternKind.NumberToken, GetSubPatternToCurrentPos(start));
        token = token.With(value: value);

        if (error)
        {
            token = token.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Capture_group_numbers_must_be_less_than_or_equal_to_Int32_MaxValue,
                token.GetSpan()));
        }

        return token;
    }

    public RoutePatternToken? TryScanCaptureName()
    {
        if (Position == Text.Length)
        {
            return null;
        }

        var start = Position;
        while (Position < Text.Length && RegexCharClass.IsBoundaryWordChar(CurrentChar))
        {
            Position++;
        }

        if (Position == start)
        {
            return null;
        }

        var token = CreateToken(RoutePatternKind.CaptureNameToken, GetSubPatternToCurrentPos(start));
        token = token.With(value: token.VirtualChars.CreateString());
        return token;
    }

    public RoutePatternToken? TryScanNumberOrCaptureName()
        => TryScanNumber() ?? TryScanCaptureName();

    public RoutePatternToken? TryScanOptions()
    {
        var start = Position;
        while (Position < Text.Length && IsOptionChar(CurrentChar))
        {
            Position++;
        }

        return start == Position
            ? null
            : CreateToken(RoutePatternKind.OptionsToken, GetSubPatternToCurrentPos(start));
    }

    private static bool IsOptionChar(AspNetCoreVirtualChar ch)
    {
        switch (ch.Value)
        {
            case '+':
            case '-':
            case 'i':
            case 'I':
            case 'm':
            case 'M':
            case 'n':
            case 'N':
            case 's':
            case 'S':
            case 'x':
            case 'X':
                return true;
            default:
                return false;
        }
    }

    public RoutePatternToken ScanHexCharacters(int count)
    {
        var start = Position;
        var beforeSlash = start - 2;

        // Make sure we're right after the \x or \u.
        Debug.Assert(Text[beforeSlash].Value == '\\');
        Debug.Assert(Text[beforeSlash + 1].Value is 'x' or 'u');

        for (var i = 0; i < count; i++)
        {
            if (Position < Text.Length && IsHexChar(CurrentChar))
            {
                Position++;
            }
        }

        var result = CreateToken(RoutePatternKind.TextToken, GetSubPatternToCurrentPos(start));

        var length = Position - start;
        if (length != count)
        {
            result = result.AddDiagnosticIfNone(new EmbeddedDiagnostic(
                FeaturesResources.Insufficient_hexadecimal_digits,
                GetTextSpan(beforeSlash, Position)));
        }

        return result;
    }

    public static bool IsHexChar(AspNetCoreVirtualChar ch)
        => IsDecimalDigit(ch) ||
           ch.Value >= 'a' && ch.Value <= 'f' ||
           ch.Value >= 'A' && ch.Value <= 'F';

    private static bool IsDecimalDigit(AspNetCoreVirtualChar ch)
        => ch.Value is >= '0' and <= '9';

    private static bool IsOctalDigit(AspNetCoreVirtualChar ch)
        => ch.Value is >= '0' and <= '7';

    public RoutePatternToken ScanOctalCharacters(RegexOptions options)
    {
        var start = Position;
        var beforeSlash = start - 1;

        // Make sure we're right after the \
        // And we only should have been called if we were \octal-char 
        Debug.Assert(Text[beforeSlash].Value == '\\');
        Debug.Assert(IsOctalDigit(Text[start]));

        const int maxChars = 3;
        var currentVal = 0;

        for (var i = 0; i < maxChars; i++)
        {
            if (Position < Text.Length && IsOctalDigit(CurrentChar))
            {
                var octalVal = CurrentChar.Value - '0';
                Debug.Assert(octalVal is >= 0 and <= 7);
                currentVal *= 8;
                currentVal += octalVal;

                Position++;
            }
        }

        Debug.Assert(Position - start > 0);

        var result = CreateToken(RoutePatternKind.TextToken, GetSubPatternToCurrentPos(start));

        return result;
    }
}
