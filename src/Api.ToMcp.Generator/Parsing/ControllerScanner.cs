using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Api.ToMcp.Generator.Models;

namespace Api.ToMcp.Generator.Parsing
{
    internal static class ControllerScanner
    {
        private const string ControllerBaseFullName = "Microsoft.AspNetCore.Mvc.ControllerBase";
        private const string ApiControllerAttributeFullName = "Microsoft.AspNetCore.Mvc.ApiControllerAttribute";
        private const string McpExposeAttributeFullName = "Api.ToMcp.Abstractions.McpExposeAttribute";
        private const string McpIgnoreAttributeFullName = "Api.ToMcp.Abstractions.McpIgnoreAttribute";

        public static ControllerInfoModel? TryGetControllerInfo(
            GeneratorSyntaxContext context,
            CancellationToken ct)
        {
            if (!(context.Node is ClassDeclarationSyntax classDecl))
                return null;

            var symbol = context.SemanticModel.GetDeclaredSymbol(classDecl, ct);
            if (!(symbol is INamedTypeSymbol namedType))
                return null;

            if (!IsController(namedType, context.SemanticModel.Compilation))
                return null;

            var routePrefix = GetRoutePrefix(namedType);
            var hasMcpExpose = HasAttribute(namedType, McpExposeAttributeFullName);
            var hasMcpIgnore = HasAttribute(namedType, McpIgnoreAttributeFullName);

            var actions = ActionMethodAnalyzer.GetActions(namedType, routePrefix, ct);

            return new ControllerInfoModel
            {
                Name = namedType.Name,
                Namespace = namedType.ContainingNamespace?.ToDisplayString() ?? "",
                RoutePrefix = routePrefix,
                HasMcpExposeAttribute = hasMcpExpose,
                HasMcpIgnoreAttribute = hasMcpIgnore,
                Actions = actions
            };
        }

        private static bool IsController(INamedTypeSymbol type, Compilation compilation)
        {
            var controllerBase = compilation.GetTypeByMetadataName(ControllerBaseFullName);
            if (controllerBase != null && InheritsFrom(type, controllerBase))
                return true;

            return HasAttribute(type, ApiControllerAttributeFullName);
        }

        private static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
        {
            var current = type.BaseType;
            while (current != null)
            {
                if (SymbolEqualityComparer.Default.Equals(current, baseType))
                    return true;
                current = current.BaseType;
            }
            return false;
        }

        internal static bool HasAttribute(ISymbol symbol, string attributeFullName)
        {
            return symbol.GetAttributes()
                .Any(attr => attr.AttributeClass?.ToDisplayString() == attributeFullName);
        }

        internal static AttributeData? GetAttribute(ISymbol symbol, string attributeFullName)
        {
            return symbol.GetAttributes()
                .FirstOrDefault(attr => attr.AttributeClass?.ToDisplayString() == attributeFullName);
        }

        private static string? GetRoutePrefix(INamedTypeSymbol controller)
        {
            var routeAttr = controller.GetAttributes()
                .FirstOrDefault(attr => attr.AttributeClass?.Name == "RouteAttribute");

            if (routeAttr?.ConstructorArguments.Length > 0)
            {
                return routeAttr.ConstructorArguments[0].Value?.ToString();
            }
            return null;
        }
    }
}
