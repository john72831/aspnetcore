//// Licensed to the .NET Foundation under one or more agreements.
//// The .NET Foundation licenses this file to you under the MIT license.

//using System;
//using System.Reflection;
//using Microsoft.CodeAnalysis;
//using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;

//namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

//internal static class RoutePatternTestHelper
//{
//    public static RoutePatternTree? TryParse(SyntaxToken syntaxToken)
//    {
//        var csharpVirtualCharServiceType = Type.GetType("Microsoft.CodeAnalysis.CSharp.EmbeddedLanguages.VirtualChars.CSharpVirtualCharService", throwOnError: true);
//        var tryConvertToVirtualCharsMethod = csharpVirtualCharServiceType.GetMethod("TryConvertToVirtualChars", , BindingFlags.Instance | BindingFlags.Public);
//        var aspNetCoreVirtualCharSequenceType = typeof(AspNetCoreVirtualCharSequence);

//        var instanceProperty = csharpVirtualCharServiceType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
//        var instance = instanceProperty.GetValue(null);

//        var sequence = tryConvertToVirtualCharsMethod.Invoke(instance, new object[] { syntaxToken });

//        var aspNetCoreSequence = (AspNetCoreVirtualCharSequence)Activator.CreateInstance(aspNetCoreVirtualCharSequenceType, new object[] { sequence });

//        return RoutePatternParser.TryParse(aspNetCoreSequence, System.Text.RegularExpressions.RegexOptions.None);
//    }
//}
