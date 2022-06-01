// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

namespace Microsoft.AspNetCore.Analyzers.RouteEmbeddedLanguage;

internal interface INewRoutePatternNodeVisitor
{
    void Visit(RoutePatternCompilationUnit node);
    void Visit(RoutePatternSegmentNode node);
    void Visit(RoutePatternReplacementNode node);
    void Visit(RoutePatternParameterNode node);
    void Visit(RoutePatternLiteralNode node);
    void Visit(RoutePatternSegmentSeperatorNode node);
    void Visit(RoutePatternOptionalSeperatorNode node);
    void Visit(RoutePatternCatchAllParameterPartNode node);
    void Visit(RoutePatternNameParameterPartNode node);
    void Visit(RoutePatternPolicyParameterPartNode node);
    void Visit(RoutePatternPolicyWithArgumentsParameterPartNode node);
    void Visit(RoutePatternOptionalParameterPartNode node);
    void Visit(RoutePatternDefaultValueParameterPartNode node);
}
