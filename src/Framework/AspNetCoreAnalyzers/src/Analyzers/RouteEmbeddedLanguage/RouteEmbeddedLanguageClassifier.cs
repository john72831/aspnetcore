// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.ExternalAccess.AspNetCore.EmbeddedLanguages;

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

[ExportAspNetCoreEmbeddedLanguageClassifier(name: "Route", language: LanguageNames.CSharp)]
internal class RouteEmbeddedLanguageClassifier : IAspNetCoreEmbeddedLanguageClassifier
{
    public void RegisterClassifications(AspNetCoreEmbeddedLanguageClassificationContext context)
    {
        //var cancellationToken = context.CancellationToken;
        //var semanticModel = context.SemanticModel;
        //var syntaxToken = context.SyntaxToken;

        //new TextSpan()

        //// whatever logic you want here.  go nuts.
        //context.AddClassification("your-classification-type", theSpanToClassify); // do this as many times as you want.
    }
}
