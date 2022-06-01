// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System.Text.RegularExpressions;
using Roslyn.Test.Utilities;
using Xunit;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

// These tests were created by trying to enumerate all codepaths in the lexer/parser.
public partial class RoutePatternParserTests
{
    [Fact]
    public void TestEmpty()
    {
        Test(@"""""", @"");
    }

    [Fact]
    public void TestSingleLiteral()
    {
        Test(@"""hello""", @"");
    }

    [Fact]
    public void TestSlashSeperatedLiterals()
    {
        Test(@"""hello/world""", @"");
    }

    [Fact]
    public void TestSlashSeperatedSegments()
    {
        Test(@"""{a}/{b}""", @"");
    }

    [Fact]
    public void TestCatchAllParameterFollowedBySlash()
    {
        Test(@"""{*a}/""", @"");
    }

    [Fact]
    public void TestCatchAllParameterNotLast()
    {
        Test(@"""{*a}/{b}""", @"");
    }

    [Fact]
    public void TestCatchAllParameterComplexSegment()
    {
        Test(@"""a{*a}""", @"");
    }

    [Fact]
    public void TestPeriodSeperatedLiterals()
    {
        Test(@"""hello.world""", @"");
    }

    [Fact]
    public void TestSimpleParameter()
    {
        Test(@"""{id}""", @"");
    }

    [Fact]
    public void TestParameterWithPolicy()
    {
        Test(@"""{id:foo}""", @"");
    }

    [Fact]
    public void TestParameterWithDefault()
    {
        Test(@"""{id=Home}""", @"");
    }

    [Fact]
    public void TestParameterWithDefaultContainingPolicyChars()
    {
        Test(@"""{id=Home=Controller:int()}""", @"");
    }

    [Fact]
    public void TestParameterWithPolicyArgument()
    {
        Test(@"""{id:foo(wee)}""", @"");
    }

    [Fact]
    public void TestParameterWithPolicyArgumentEmpty()
    {
        Test(@"""{id:foo()}""", @"");
    }

    [Fact]
    public void TestParameterOptional()
    {
        Test(@"""{id?}""", @"");
    }

    [Fact]
    public void TestUnbalancedBraces()
    {
        Test(@"""a{foob{bar}c""", @"");
    }

    [Fact]
    public void TestComplexSegment()
    {
        Test(@"""a{foo}b{bar}c""", @"");
    }

    [Fact]
    public void TestConsecutiveParameters()
    {
        Test(@"""{a}{b}""", @"");
    }

    [Fact]
    public void TestParameterWithPolicyAndOptional()
    {
        Test(@"""{id:foo?}""", @"");
    }

    [Fact]
    public void TestParameterWithMultiplePolicies()
    {
        Test(@"""{id:foo:bar}""", @"");
    }

    [Fact]
    public void TestCatchAllParameter()
    {
        Test(@"""{*id}""", @"");
    }

    [Fact]
    public void TestCatchAllUnescapedParameter()
    {
        Test(@"""{**id}""", @"");
    }

    [Fact]
    public void TestEmptyParameter()
    {
        Test(@"""{}""", @"");
    }

    [Fact]
    public void TestParameterWithEscapedPolicyArgument()
    {
        Test(@"""{ssn:regex(^\\d{{3}}-\\d{{2}}-\\d{{4}}$)}""", @"");
    }
}
