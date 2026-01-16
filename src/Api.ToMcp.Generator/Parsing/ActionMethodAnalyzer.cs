using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Api.ToMcp.Generator.Models;

namespace Api.ToMcp.Generator.Parsing
{
    internal static class ActionMethodAnalyzer
    {
        private const string McpExposeAttributeFullName = "Api.ToMcp.Abstractions.McpExposeAttribute";
        private const string McpIgnoreAttributeFullName = "Api.ToMcp.Abstractions.McpIgnoreAttribute";

        private static readonly HashSet<string> HttpMethodAttributes = new HashSet<string>
        {
            "HttpGetAttribute",
            "HttpPostAttribute",
            "HttpPutAttribute",
            "HttpDeleteAttribute",
            "HttpPatchAttribute"
        };

        public static ImmutableArray<ActionInfoModel> GetActions(
            INamedTypeSymbol controller,
            string? routePrefix,
            CancellationToken ct)
        {
            var builder = ImmutableArray.CreateBuilder<ActionInfoModel>();

            foreach (var member in controller.GetMembers())
            {
                ct.ThrowIfCancellationRequested();

                if (!(member is IMethodSymbol method))
                    continue;

                if (method.MethodKind != MethodKind.Ordinary)
                    continue;

                if (method.DeclaredAccessibility != Accessibility.Public)
                    continue;

                var httpInfo = GetHttpMethodInfo(method);
                if (httpInfo == null)
                    continue;

                // v1: Only support GET and POST
                if (httpInfo.Value.Method != "GET" && httpInfo.Value.Method != "POST")
                    continue;

                var parameters = AnalyzeParameters(method, httpInfo.Value.Method);
                var returnType = AnalyzeReturnType(method);

                var fullRoute = BuildFullRoute(routePrefix, httpInfo.Value.Template, controller.Name);

                var mcpExposeAttr = ControllerScanner.GetAttribute(method, McpExposeAttributeFullName);
                string? customToolName = null;
                string? customDescription = null;

                if (mcpExposeAttr != null)
                {
                    foreach (var namedArg in mcpExposeAttr.NamedArguments)
                    {
                        if (namedArg.Key == "ToolName" && namedArg.Value.Value is string tn)
                            customToolName = tn;
                        if (namedArg.Key == "Description" && namedArg.Value.Value is string desc)
                            customDescription = desc;
                    }
                }

                builder.Add(new ActionInfoModel
                {
                    Name = method.Name,
                    ControllerName = controller.Name,
                    HttpMethod = httpInfo.Value.Method,
                    RouteTemplate = fullRoute,
                    Parameters = parameters,
                    ReturnType = returnType,
                    HasMcpExposeAttribute = mcpExposeAttr != null,
                    HasMcpIgnoreAttribute = ControllerScanner.HasAttribute(method, McpIgnoreAttributeFullName),
                    XmlDocSummary = GetXmlDocSummary(method),
                    CustomToolName = customToolName,
                    CustomDescription = customDescription
                });
            }

            return builder.ToImmutable();
        }

        private static (string Method, string? Template)? GetHttpMethodInfo(IMethodSymbol method)
        {
            foreach (var attr in method.GetAttributes())
            {
                var attrName = attr.AttributeClass?.Name;
                if (attrName == null || !HttpMethodAttributes.Contains(attrName))
                    continue;

                var httpMethod = attrName.Replace("Attribute", "").Replace("Http", "").ToUpperInvariant();
                var template = attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value?.ToString()
                    : null;

                return (httpMethod, template);
            }

            return null;
        }

        private static ImmutableArray<ParameterInfoModel> AnalyzeParameters(
            IMethodSymbol method,
            string httpMethod)
        {
            var builder = ImmutableArray.CreateBuilder<ParameterInfoModel>();

            foreach (var param in method.Parameters)
            {
                var source = DetermineParameterSource(param, httpMethod);

                // Skip service-injected parameters
                if (source == ParameterSourceModel.Services)
                    continue;

                // Skip CancellationToken
                if (param.Type.ToDisplayString() == "System.Threading.CancellationToken")
                    continue;

                builder.Add(new ParameterInfoModel
                {
                    Name = param.Name,
                    Type = param.Type.ToDisplayString(),
                    IsNullable = param.Type.NullableAnnotation == NullableAnnotation.Annotated ||
                                 param.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T,
                    HasDefaultValue = param.HasExplicitDefaultValue,
                    DefaultValue = param.HasExplicitDefaultValue ? param.ExplicitDefaultValue : null,
                    Source = source
                });
            }

            return builder.ToImmutable();
        }

        private static ParameterSourceModel DetermineParameterSource(
            IParameterSymbol param,
            string httpMethod)
        {
            foreach (var attr in param.GetAttributes())
            {
                var attrName = attr.AttributeClass?.Name;
                switch (attrName)
                {
                    case "FromRouteAttribute":
                        return ParameterSourceModel.Route;
                    case "FromQueryAttribute":
                        return ParameterSourceModel.Query;
                    case "FromBodyAttribute":
                        return ParameterSourceModel.Body;
                    case "FromHeaderAttribute":
                        return ParameterSourceModel.Header;
                    case "FromServicesAttribute":
                        return ParameterSourceModel.Services;
                }
            }

            // Auto-infer based on HTTP method and type
            if (httpMethod == "POST" && IsComplexType(param.Type))
                return ParameterSourceModel.Body;

            return ParameterSourceModel.Auto;
        }

        private static bool IsComplexType(ITypeSymbol type)
        {
            if (type.SpecialType != SpecialType.None)
                return false;

            var fullName = type.ToDisplayString();

            // Common primitive types
            if (fullName.StartsWith("System."))
            {
                var primitives = new[]
                {
                    "System.String", "System.Int32", "System.Int64", "System.Boolean",
                    "System.Double", "System.Decimal", "System.DateTime", "System.DateTimeOffset",
                    "System.Guid", "System.TimeSpan"
                };
                if (primitives.Any(p => fullName.StartsWith(p)))
                    return false;
            }

            return type.TypeKind == TypeKind.Class || type.TypeKind == TypeKind.Struct;
        }

        private static ReturnTypeInfoModel AnalyzeReturnType(IMethodSymbol method)
        {
            var returnType = method.ReturnType;
            var isAsync = false;
            ITypeSymbol? innerType = returnType;

            // Unwrap Task<T> or ValueTask<T>
            if (returnType is INamedTypeSymbol namedType)
            {
                var typeName = namedType.OriginalDefinition.ToDisplayString();
                if (typeName == "System.Threading.Tasks.Task<TResult>" ||
                    typeName == "System.Threading.Tasks.ValueTask<TResult>")
                {
                    isAsync = true;
                    innerType = namedType.TypeArguments[0];
                }
                else if (typeName == "System.Threading.Tasks.Task" ||
                         typeName == "System.Threading.Tasks.ValueTask")
                {
                    isAsync = true;
                    innerType = null;
                }

                // Unwrap ActionResult<T>
                if (innerType is INamedTypeSymbol innerNamed)
                {
                    var innerTypeName = innerNamed.OriginalDefinition.ToDisplayString();
                    if (innerTypeName == "Microsoft.AspNetCore.Mvc.ActionResult<TValue>")
                    {
                        innerType = innerNamed.TypeArguments[0];
                    }
                }
            }

            return new ReturnTypeInfoModel
            {
                FullTypeName = returnType.ToDisplayString(),
                IsAsync = isAsync,
                UnwrappedType = innerType?.ToDisplayString() ?? "void"
            };
        }

        private static string BuildFullRoute(string? prefix, string? template, string controllerName)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(prefix))
            {
                var normalizedPrefix = prefix.TrimStart('/').TrimEnd('/');
                // Replace [controller] placeholder
                var controllerBaseName = controllerName.EndsWith("Controller")
                    ? controllerName.Substring(0, controllerName.Length - "Controller".Length).ToLowerInvariant()
                    : controllerName.ToLowerInvariant();
                normalizedPrefix = normalizedPrefix.Replace("[controller]", controllerBaseName);
                parts.Add(normalizedPrefix);
            }

            if (!string.IsNullOrEmpty(template))
                parts.Add(template.TrimStart('/').TrimEnd('/'));

            return "/" + string.Join("/", parts);
        }

        private static string? GetXmlDocSummary(IMethodSymbol method)
        {
            var xml = method.GetDocumentationCommentXml();
            if (string.IsNullOrEmpty(xml))
                return null;

            var start = xml.IndexOf("<summary>");
            var end = xml.IndexOf("</summary>");
            if (start >= 0 && end > start)
            {
                var summary = xml.Substring(start + 9, end - start - 9).Trim();
                // Clean up whitespace
                summary = string.Join(" ", summary.Split(new[] { '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries));
                return summary;
            }
            return null;
        }
    }
}
