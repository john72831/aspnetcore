// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Collections.Immutable;
using Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage.Common;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

internal sealed class NewRoutePatternTree : EmbeddedSyntaxTree<NewRoutePatternKind, NewRoutePatternNode, RoutePatternCompilationUnit>
{
    public readonly ImmutableDictionary<string, TextSpan> CaptureNamesToSpan;
    public readonly ImmutableDictionary<int, TextSpan> CaptureNumbersToSpan;

    public NewRoutePatternTree(
        AspNetCoreVirtualCharSequence text,
        RoutePatternCompilationUnit root,
        ImmutableArray<EmbeddedDiagnostic> diagnostics,
        ImmutableDictionary<string, TextSpan> captureNamesToSpan,
        ImmutableDictionary<int, TextSpan> captureNumbersToSpan)
        : base(text, root, diagnostics)
    {
        CaptureNamesToSpan = captureNamesToSpan;
        CaptureNumbersToSpan = captureNumbersToSpan;
    }
}
