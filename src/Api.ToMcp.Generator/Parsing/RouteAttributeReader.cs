using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Api.ToMcp.Generator.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Api.ToMcp.Generator.Parsing
{
    /// <summary>
    /// Reads the routing attributes of a controller or an action as MVC reads them: by type, through
    /// <c>IRouteTemplateProvider</c> and <c>IActionHttpMethodProvider</c>, so an app's own attribute counts as well as
    /// MVC's <c>[Route]</c>, <c>[Http*]</c> and <c>[AcceptVerbs]</c>. MVC instantiates the attribute and asks it; the
    /// generator follows its constructor down to the MVC attribute it derives from, or reads the properties of one that
    /// implements the interfaces itself, as far as their code passes on constants and constructor parameters, and strings
    /// joined from them. A value it cannot work out carries the reason, for MCP009's message.
    /// </summary>
    internal static class RouteAttributeReader
    {
        private const string AttributeSuffix = "Attribute";

        /// <summary>
        /// Far more constructors than any attribute chains through, so a cycle the compiler reports cannot loop.
        /// </summary>
        private const int MaxConstructorChainLength = 32;

        /// <summary>
        /// The HTTP method each of MVC's <c>[Http*]</c> attributes serves, fixed by its constructors.
        /// </summary>
        private static readonly Dictionary<string, string> HttpMethodByAttribute = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RoutingAttributeNames.HttpGet] = HttpMethodNames.Get,
            [RoutingAttributeNames.HttpPost] = HttpMethodNames.Post,
            [RoutingAttributeNames.HttpPut] = HttpMethodNames.Put,
            [RoutingAttributeNames.HttpPatch] = HttpMethodNames.Patch,
            [RoutingAttributeNames.HttpDelete] = HttpMethodNames.Delete,
            [RoutingAttributeNames.HttpHead] = HttpMethodNames.Head,
            [RoutingAttributeNames.HttpOptions] = HttpMethodNames.Options
        };

        /// <summary>
        /// The value of a constructor parameter no argument is passed for, which the compiler does not allow.
        /// </summary>
        private static readonly UnknownValue NoArgument = new UnknownValue("the generator finds no argument for a parameter of its constructor");

        /// <summary>
        /// Whether MVC reads a route template from the attribute. MVC's own attributes are also matched by their names, as
        /// a project may declare stand-ins for them.
        /// </summary>
        public static bool IsRouteTemplateProvider(AttributeData attribute)
        {
            return attribute.AttributeClass is { } attributeClass &&
                (ResponseTypeResolver.IsOrImplements(attributeClass, RoutingAttributeNames.RouteTemplateProvider) ||
                    ResponseTypeResolver.IsOrImplements(attributeClass, RoutingAttributeNames.Route) ||
                    IsMvcHttpMethodAttribute(attributeClass));
        }

        /// <summary>
        /// Whether MVC reads HTTP methods from the attribute.
        /// </summary>
        public static bool IsHttpMethodProvider(AttributeData attribute)
        {
            return attribute.AttributeClass is { } attributeClass &&
                (ResponseTypeResolver.IsOrImplements(attributeClass, RoutingAttributeNames.ActionHttpMethodProvider) ||
                    IsMvcHttpMethodAttribute(attributeClass));
        }

        public static RouteAttributeInfo Read(AttributeData attribute, Compilation compilation, CancellationToken ct)
        {
            var attributeClass = attribute.AttributeClass!;
            var isRouteTemplateProvider = IsRouteTemplateProvider(attribute);
            var isHttpMethodProvider = IsHttpMethodProvider(attribute);

            var (template, httpMethods, name, order) = DerivesFromMvcAttribute(attributeClass)
                ? ReadThroughConstructors(attribute, compilation, ct)
                : ReadThroughProperties(attributeClass, isRouteTemplateProvider, isHttpMethodProvider, compilation, ct);

            var templateProblem = isRouteTemplateProvider ? (template as UnknownValue)?.Reason : null;
            var methods = ToHttpMethods(httpMethods);
            var httpMethodsProblem = !isHttpMethodProvider || methods != null
                ? null
                : (httpMethods as UnknownValue)?.Reason ?? "it gives no list of method names";

            return new RouteAttributeInfo(
                GetDisplayName(attributeClass),
                isRouteTemplateProvider,
                isHttpMethodProvider,
                templateProblem,
                templateProblem is null ? template as string : null,
                // MVC gives such an attribute a route of its own (a selector) when it sets a template, a name or an order.
                isRouteTemplateProvider && (templateProblem != null || template != null || name != null || order != null),
                httpMethodsProblem,
                methods ?? new List<string>());
        }

        private static bool IsMvcHttpMethodAttribute(INamedTypeSymbol attributeClass)
        {
            return ResponseTypeResolver.IsOrImplements(attributeClass, RoutingAttributeNames.HttpMethod) ||
                ResponseTypeResolver.IsOrImplements(attributeClass, RoutingAttributeNames.AcceptVerbs) ||
                HttpMethodByAttribute.Keys.Any(x => ResponseTypeResolver.IsOrImplements(attributeClass, x));
        }

        private static bool DerivesFromMvcAttribute(INamedTypeSymbol attributeClass)
        {
            return ResponseTypeResolver.IsOrImplements(attributeClass, RoutingAttributeNames.Route) || IsMvcHttpMethodAttribute(attributeClass);
        }

        /// <summary>
        /// The template and HTTP methods an attribute derived from one of MVC's passes to it, followed from the
        /// constructor the attribute is applied with through each constructor it calls, and the name and order its
        /// application sets.
        /// </summary>
        private static (object? Template, object? HttpMethods, object? Name, object? Order) ReadThroughConstructors(
            AttributeData attribute,
            Compilation compilation,
            CancellationToken ct)
        {
            var name = attribute.NamedArguments.FirstOrDefault(x => x.Key == RoutingAttributeNames.NamePropertyName).Value.Value;
            var order = attribute.NamedArguments.Any(x => x.Key == RoutingAttributeNames.OrderPropertyName) ? (object)true : null;

            var constructor = attribute.AttributeConstructor;
            var arguments = attribute.ConstructorArguments.Select(ToValue).ToArray();
            for (var i = 0; i < MaxConstructorChainLength && constructor is { } current; i++)
            {
                var typeName = current.ContainingType.OriginalDefinition.ToDisplayString();
                if (typeName == RoutingAttributeNames.Route)
                {
                    return (GetArgument(arguments, 0), null, name, order);
                }

                if (HttpMethodByAttribute.TryGetValue(typeName, out var httpMethod))
                {
                    return (arguments.Length > 0 ? arguments[0] : null, new object?[] { httpMethod }, name, order);
                }

                if (typeName == RoutingAttributeNames.HttpMethod)
                {
                    return (arguments.Length > 1 ? arguments[1] : null, GetArgument(arguments, 0), name, order);
                }

                if (typeName == RoutingAttributeNames.AcceptVerbs)
                {
                    // Sealed, so applied as itself, and its template is a property.
                    var route = attribute.NamedArguments.FirstOrDefault(x => x.Key == RoutingAttributeNames.AcceptVerbsRoutePropertyName).Value.Value;
                    return (route, GetArgument(arguments, 0), name, order);
                }

                UnknownValue? failure;
                (constructor, arguments, failure) = ReadBaseConstructorCall(current, arguments, compilation, ct);
                if (failure != null)
                {
                    return (failure, failure, name, order);
                }
            }

            var tooLong = new UnknownValue("its constructors call each other further than the generator follows them");
            return (tooLong, tooLong, name, order);
        }

        /// <summary>
        /// The constructor the constructor calls, <c>base(...)</c> or <c>this(...)</c>, with the values it passes, or why
        /// the generator cannot tell, as for a constructor without source, of a referenced assembly, whose body metadata
        /// does not hold.
        /// </summary>
        private static (IMethodSymbol? Constructor, object?[] Arguments, UnknownValue? Failure) ReadBaseConstructorCall(
            IMethodSymbol constructor,
            object?[] arguments,
            Compilation compilation,
            CancellationToken ct)
        {
            if (constructor.DeclaringSyntaxReferences.IsEmpty)
            {
                if (constructor.IsImplicitlyDeclared && constructor.ContainingType.Locations.Any(x => x.IsInSource))
                {
                    var (baseConstructor, baseArguments) = GetImplicitBaseConstructorCall(constructor);
                    return (baseConstructor, baseArguments, null);
                }

                return (null, Array.Empty<object?>(), new UnknownValue(
                    $"the referenced assembly '{constructor.ContainingAssembly?.Name}' holds no code of the constructor of {constructor.ContainingType.Name}"));
            }

            var declaration = constructor.DeclaringSyntaxReferences[0].GetSyntax(ct);
            var body = compilation.GetSemanticModel(declaration.SyntaxTree).GetOperation(declaration, ct) as IConstructorBodyOperation;
            var initializer = body?.Initializer is IExpressionStatementOperation statement ? statement.Operation : body?.Initializer;
            if (!(initializer is IInvocationOperation call))
            {
                return (null, Array.Empty<object?>(), new UnknownValue(
                    $"the generator finds no constructor call in the constructor of {constructor.ContainingType.Name}"));
            }

            var parameterValues = new Dictionary<IParameterSymbol, object?>(SymbolEqualityComparer.Default);
            for (var i = 0; i < constructor.Parameters.Length; i++)
            {
                parameterValues[constructor.Parameters[i]] = GetArgument(arguments, i);
            }

            var calledArguments = Enumerable.Repeat<object?>(NoArgument, call.TargetMethod.Parameters.Length).ToArray();
            foreach (var argument in call.Arguments.Where(x => x.Parameter != null))
            {
                calledArguments[argument.Parameter!.Ordinal] = Evaluate(argument.Value, parameterValues);
            }

            return (call.TargetMethod, calledArguments, null);
        }

        /// <summary>
        /// The parameterless call to its base type's constructor that the compiler gives a class without a constructor.
        /// </summary>
        private static (IMethodSymbol? Constructor, object?[] Arguments) GetImplicitBaseConstructorCall(IMethodSymbol constructor)
        {
            var baseConstructor = constructor.ContainingType.BaseType?.InstanceConstructors
                .Where(x => x.Parameters.All(parameter => parameter.IsOptional || parameter.IsParams))
                .OrderBy(x => x.Parameters.Length)
                .FirstOrDefault();

            var arguments = baseConstructor?.Parameters
                .Select(x => x.IsParams ? new object?[0] : x.HasExplicitDefaultValue ? x.ExplicitDefaultValue : null)
                .ToArray();

            return (baseConstructor, arguments ?? Array.Empty<object?>());
        }

        /// <summary>
        /// The template, HTTP methods, name and order an attribute that implements MVC's interfaces itself returns from
        /// its properties, when each returns a value its code spells out.
        /// </summary>
        private static (object? Template, object? HttpMethods, object? Name, object? Order) ReadThroughProperties(
            INamedTypeSymbol attributeClass,
            bool isRouteTemplateProvider,
            bool isHttpMethodProvider,
            Compilation compilation,
            CancellationToken ct)
        {
            object? ReadRouteProperty(string propertyName)
            {
                return isRouteTemplateProvider
                    ? ReadInterfaceProperty(attributeClass, RoutingAttributeNames.RouteTemplateProvider, propertyName, compilation, ct)
                    : null;
            }

            var httpMethods = isHttpMethodProvider
                ? ReadInterfaceProperty(attributeClass, RoutingAttributeNames.ActionHttpMethodProvider, RoutingAttributeNames.HttpMethodsPropertyName, compilation, ct)
                : null;

            return (
                ReadRouteProperty(RoutingAttributeNames.TemplatePropertyName),
                httpMethods,
                ReadRouteProperty(RoutingAttributeNames.NamePropertyName),
                ReadRouteProperty(RoutingAttributeNames.OrderPropertyName));
        }

        private static object? ReadInterfaceProperty(
            INamedTypeSymbol attributeClass,
            string interfaceName,
            string propertyName,
            Compilation compilation,
            CancellationToken ct)
        {
            var interfaceProperty = attributeClass.AllInterfaces
                .FirstOrDefault(x => x.ToDisplayString() == interfaceName)?
                .GetMembers(propertyName)
                .OfType<IPropertySymbol>()
                .FirstOrDefault();
            var property = interfaceProperty is null ? null : attributeClass.FindImplementationForInterfaceMember(interfaceProperty) as IPropertySymbol;

            return property is null
                ? new UnknownValue($"the generator finds no {propertyName} property of {attributeClass.Name}")
                : ReadPropertyValue(property, compilation, ct);
        }

        /// <summary>
        /// The value a property's getter returns, from an expression body, a body with one <c>return</c>, or the
        /// initializer of an auto-property.
        /// </summary>
        private static object? ReadPropertyValue(IPropertySymbol property, Compilation compilation, CancellationToken ct)
        {
            var propertyName = $"{property.ContainingType.Name}.{property.Name}";
            if (!(property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(ct) is PropertyDeclarationSyntax declaration))
            {
                return new UnknownValue(property.Locations.Any(x => x.IsInSource)
                    ? $"the generator cannot read {propertyName}"
                    : $"the referenced assembly '{property.ContainingAssembly?.Name}' holds no code of {propertyName}");
            }

            var getter = declaration.AccessorList?.Accessors.FirstOrDefault(x => x.IsKind(SyntaxKind.GetAccessorDeclaration));
            var returnedExpression = declaration.ExpressionBody?.Expression ??
                getter?.ExpressionBody?.Expression ??
                (getter?.Body?.Statements.Count == 1 && getter.Body.Statements[0] is ReturnStatementSyntax returnStatement ? returnStatement.Expression : null) ??
                (getter is { Body: null, ExpressionBody: null } ? declaration.Initializer?.Value : null);

            if (returnedExpression is null)
            {
                return new UnknownValue($"{propertyName} returns it from more than one expression, of which the generator reads only one");
            }

            var operation = compilation.GetSemanticModel(declaration.SyntaxTree).GetOperation(returnedExpression, ct);

            return operation is null
                ? new UnknownValue($"the generator cannot read {propertyName}")
                : Evaluate(operation, new Dictionary<IParameterSymbol, object?>(SymbolEqualityComparer.Default));
        }

        /// <summary>
        /// The value of an expression made of constants, the calling constructor's parameters, string concatenation and
        /// interpolation, and arrays of those; anything else is an <see cref="UnknownValue"/>.
        /// </summary>
        private static object? Evaluate(IOperation operation, Dictionary<IParameterSymbol, object?> parameterValues)
        {
            if (operation.ConstantValue.HasValue)
            {
                return operation.ConstantValue.Value;
            }

            switch (operation)
            {
                case IParameterReferenceOperation parameterReference when parameterValues.TryGetValue(parameterReference.Parameter, out var value):
                    return value;

                case IConversionOperation conversion when !conversion.Conversion.IsUserDefined:
                    return Evaluate(conversion.Operand, parameterValues);

                case IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } addition when addition.Type?.SpecialType == SpecialType.System_String:
                    var left = ToText(addition.LeftOperand, parameterValues);
                    var right = ToText(addition.RightOperand, parameterValues);
                    return left as UnknownValue ?? right as UnknownValue ?? (object)((string)left! + (string)right!);

                case IInterpolatedStringOperation interpolatedString:
                    return EvaluateInterpolatedString(interpolatedString, parameterValues);

                case IArrayCreationOperation { Initializer: { } initializer }:
                    var elements = initializer.ElementValues.Select(x => Evaluate(x, parameterValues)).ToArray();
                    return elements.OfType<UnknownValue>().FirstOrDefault() ?? (object)elements;

                default:
                    return new UnknownValue($"it is computed by '{GetText(operation)}', which the generator cannot evaluate");
            }
        }

        private static object? EvaluateInterpolatedString(IInterpolatedStringOperation interpolatedString, Dictionary<IParameterSymbol, object?> parameterValues)
        {
            var text = new StringBuilder();
            foreach (var part in interpolatedString.Parts)
            {
                var value = part switch
                {
                    IInterpolatedStringTextOperation literal => ToText(literal.Text, parameterValues),
                    IInterpolationOperation { Alignment: null, FormatString: null } interpolation => ToText(interpolation.Expression, parameterValues),
                    _ => new UnknownValue($"it is computed by '{GetText(part)}', which the generator cannot evaluate")
                };

                if (value is UnknownValue)
                {
                    return value;
                }

                text.Append((string)value);
            }

            return text.ToString();
        }

        /// <summary>
        /// The text C# makes of an operand of string concatenation or interpolation, as far as it is the same in every
        /// culture. C# writes a number with the culture the app runs under, so the generator writes only an integer that
        /// is not negative, whose digits no culture changes; a negative one takes the culture's minus sign, and a
        /// fraction its decimal separator. <c>bool</c> and <c>char</c> are written alike everywhere.
        /// </summary>
        private static object ToText(IOperation operand, Dictionary<IParameterSymbol, object?> parameterValues)
        {
            var value = Evaluate(operand, parameterValues);
            switch (value)
            {
                case UnknownValue:
                    return value;
                case null:
                    return "";
                case string text:
                    return text;
                case char character:
                    return character.ToString();
                case bool boolean:
                    return boolean ? bool.TrueString : bool.FalseString;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    return Convert.ToDecimal(value, CultureInfo.InvariantCulture) >= 0
                        ? Convert.ToString(value, CultureInfo.InvariantCulture)!
                        : new UnknownValue($"it is computed from '{GetText(operand)}', a negative number, which C# writes with the minus sign of the culture the app runs under");
                case float or double or decimal:
                    return new UnknownValue($"it is computed from '{GetText(operand)}', a {GetTypeName(operand)}, which C# writes in the culture the app runs under");
                default:
                    return new UnknownValue($"it is computed from '{GetText(operand)}', which the generator cannot write as text");
            }
        }

        /// <summary>
        /// The source of an operation, without the conversions the compiler adds around it.
        /// </summary>
        private static string GetText(IOperation operation)
        {
            while (operation is IConversionOperation { IsImplicit: true } conversion)
            {
                operation = conversion.Operand;
            }

            return operation.Syntax.ToString();
        }

        private static string GetTypeName(IOperation operation)
        {
            while (operation is IConversionOperation { IsImplicit: true } conversion)
            {
                operation = conversion.Operand;
            }

            return operation.Type?.ToDisplayString() ?? "";
        }

        private static object? ToValue(TypedConstant constant)
        {
            return constant.Kind switch
            {
                TypedConstantKind.Array => constant.IsNull ? null : constant.Values.Select(ToValue).ToArray(),
                TypedConstantKind.Error => new UnknownValue("an argument of the attribute does not compile"),
                _ => constant.Value
            };
        }

        private static object? GetArgument(object?[] arguments, int index)
        {
            return index < arguments.Length ? arguments[index] : NoArgument;
        }

        /// <summary>
        /// A value the generator cannot work out, as distinct from <c>null</c>, with the reason, as the clause that ends
        /// MCP009's "the generator cannot read the route template of its [...] attribute, as ...".
        /// </summary>
        private sealed class UnknownValue
        {
            public UnknownValue(string reason)
            {
                Reason = reason;
            }

            public string Reason { get; }
        }

        /// <summary>
        /// The HTTP methods as MVC compares them, ignoring case, or null when they are unknown.
        /// </summary>
        private static List<string>? ToHttpMethods(object? value)
        {
            var methods = value switch
            {
                string method => new object?[] { method },
                object?[] array => array,
                _ => null
            };

            return methods != null && methods.All(x => x is string)
                ? methods.Select(x => ((string)x!).ToUpperInvariant()).ToList()
                : null;
        }

        private static string GetDisplayName(INamedTypeSymbol attributeClass)
        {
            var name = attributeClass.Name;

            return name.EndsWith(AttributeSuffix, StringComparison.Ordinal) && name.Length > AttributeSuffix.Length
                ? name.Substring(0, name.Length - AttributeSuffix.Length)
                : name;
        }
    }

    /// <summary>
    /// What a routing attribute tells MVC: a route template, HTTP methods, or both, and whether the generator could read
    /// them.
    /// </summary>
    internal sealed class RouteAttributeInfo
    {
        public RouteAttributeInfo(
            string displayName,
            bool isRouteTemplateProvider,
            bool isHttpMethodProvider,
            string? templateProblem,
            string? template,
            bool definesRoute,
            string? httpMethodsProblem,
            IReadOnlyList<string> httpMethods)
        {
            DisplayName = displayName;
            IsRouteTemplateProvider = isRouteTemplateProvider;
            IsHttpMethodProvider = isHttpMethodProvider;
            TemplateProblem = templateProblem;
            Template = template;
            DefinesRoute = definesRoute;
            HttpMethodsProblem = httpMethodsProblem;
            HttpMethods = httpMethods;
        }

        /// <summary>
        /// The attribute as it is applied, such as <c>HttpGet</c>.
        /// </summary>
        public string DisplayName { get; }

        public bool IsRouteTemplateProvider { get; }
        public bool IsHttpMethodProvider { get; }

        /// <summary>
        /// Why the generator cannot read the template, as the clause that ends MCP009's "the generator cannot read the
        /// route template of its [...] attribute, as ...", or null when it can.
        /// </summary>
        public string? TemplateProblem { get; }

        public bool IsTemplateKnown => TemplateProblem is null;
        public string? Template { get; }

        /// <summary>
        /// Whether MVC gives the attribute a route of its own rather than adding it to another attribute's: it sets a
        /// template, a name or an order, or the generator cannot tell.
        /// </summary>
        public bool DefinesRoute { get; }

        /// <summary>
        /// Why the generator cannot read the HTTP methods, as <see cref="TemplateProblem"/> says it of the template.
        /// </summary>
        public string? HttpMethodsProblem { get; }

        public bool AreHttpMethodsKnown => HttpMethodsProblem is null;
        public IReadOnlyList<string> HttpMethods { get; }
    }
}
