// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.Immutable;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

internal static class ListExtensions
{
    public static ImmutableArray<T> ToImmutable<T>(this List<T> list) => ImmutableArray.Create<T>(list.ToArray());
}
