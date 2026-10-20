using System;
using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Api.ToMcp.Generator.Models;

namespace Api.ToMcp.Generator.Parsing
{
    internal static class ControllerScanner
    {
        private const string ApiControllerAttributeFullName = "Microsoft.AspNetCore.Mvc.ApiControllerAttribute";
        private const string NonControllerAttributeFullName = "Microsoft.AspNetCore.Mvc.NonControllerAttribute";
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

            if (!IsFirstDeclaration(namedType, classDecl) || !IsController(namedType, context.SemanticModel.Compilation))
                return null;

            var routePrefix = GetRoutePrefix(namedType);
            // [McpExpose] and [McpIgnore] are inherited: a base class's covers this controller, the actions it inherits from
            // that base included.
            var hasMcpExpose = HasNearestAttribute(namedType, McpExposeAttributeFullName);
            var hasMcpIgnore = HasNearestAttribute(namedType, McpIgnoreAttributeFullName);

            var actions = ActionMethodAnalyzer.GetActions(namedType, routePrefix, context.SemanticModel.Compilation, ct);

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

        /// <summary>
        /// Whether MVC serves the class as a controller, by the rules of its <c>ControllerFeatureProvider</c>: a public
        /// top-level class, neither abstract nor static, without type parameters or <c>[NonController]</c>, whose name ends
        /// in "Controller" or that carries <c>[Controller]</c>. Of those, the generator takes the ones that derive from
        /// <c>ControllerBase</c> or carry <c>[ApiController]</c>, and these meet the name rule whatever their name:
        /// <c>ControllerBase</c> carries <c>[Controller]</c>, and <c>[ApiController]</c> derives from it. The attributes
        /// are inherited. An abstract base controller serves its actions only through the concrete controllers that
        /// derive from it, which list them as their own.
        /// </summary>
        private static bool IsController(INamedTypeSymbol type, Compilation compilation)
        {
            if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic || type.IsGenericType)
                return false;

            // MVC's IsPublic, which is false for a nested class whatever its modifiers.
            if (type.DeclaredAccessibility != Accessibility.Public || type.ContainingType != null)
                return false;

            if (HasNearestAttribute(type, NonControllerAttributeFullName))
                return false;

            var controllerBase = compilation.GetTypeByMetadataName(KnownTypeNames.ControllerBase);
            if (controllerBase != null && InheritsFrom(type, controllerBase))
                return true;

            return HasNearestAttribute(type, ApiControllerAttributeFullName);
        }

        /// <summary>
        /// Whether the declaration is the type's first. Each part of a partial class is a declaration of its own that
        /// reaches the scanner, while the type's symbol holds the members of all of them, so only one of them may stand
        /// for the controller; the others would repeat every tool.
        /// </summary>
        private static bool IsFirstDeclaration(INamedTypeSymbol type, ClassDeclarationSyntax declaration)
        {
            var references = type.DeclaringSyntaxReferences;

            return references.Length == 0 ||
                (references[0].SyntaxTree == declaration.SyntaxTree && references[0].Span == declaration.Span);
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

        /// <summary>
        /// Whether the class or one of its base classes carries the attribute or one derived from it, as reflection's
        /// <c>IsDefined</c> finds an inherited attribute by its type.
        /// </summary>
        private static bool HasNearestAttribute(INamedTypeSymbol type, string attributeFullName)
        {
            return ActionMethodAnalyzer.FindNearestAttribute(
                type,
                x => x.BaseType,
                x => x.AttributeClass != null && ResponseTypeResolver.IsOrImplements(x.AttributeClass, attributeFullName)) != null;
        }

        /// <summary>
        /// The template of the nearest <c>[Route]</c>: MVC takes a controller's route attributes from the nearest class
        /// that declares any, so a controller without one of its own is routed by its base class's, and one with its own
        /// replaces the base's.
        /// </summary>
        private static string? GetRoutePrefix(INamedTypeSymbol controller)
        {
            var routeAttr = ActionMethodAnalyzer.FindNearestAttribute(
                controller,
                x => x.BaseType,
                x => x.AttributeClass?.Name == ActionMethodAnalyzer.RouteAttributeName);

            if (routeAttr?.ConstructorArguments.Length > 0)
            {
                return routeAttr.ConstructorArguments[0].Value?.ToString();
            }
            return null;
        }
    }
}
