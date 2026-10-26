using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Writes tool arguments into route segments and query strings in the form ASP.NET Core model binding
/// reads them back. Route and query values are bound with the invariant culture, so a plain
/// <c>ToString()</c> breaks as soon as the server runs under any real culture: a <see cref="DateOnly"/>
/// becomes "9/28/2026" and the endpoint answers 400.
/// </summary>
public static class McpRequestValueFormatter
{
    /// <summary>
    /// MVC's own binding limit (<c>MvcOptions.MaxModelBindingRecursionDepth</c>, 32 by default): nothing deeper would
    /// bind, so reaching it means a cyclic graph rather than a filter.
    /// </summary>
    private const int MaxObjectDepth = 32;

    private const string TryParseMethodName = "TryParse";

    /// <summary>
    /// The clone method the compiler gives a record, by which MVC tells one (<c>DefaultBindingMetadataProvider</c>).
    /// </summary>
    private const string RecordCloneMethodName = "<Clone>$";

    private static readonly string ParsableInterfaceName = $"{typeof(IParsable<>).Namespace}.{nameof(IParsable<int>)}";

    /// <summary>
    /// The types MVC binds from a source of their own whatever their attributes say (<c>BindingSourceMetadataProvider</c>),
    /// each counting for a type assigned to it: form files for <c>IFormFile</c>, <c>IFormFileCollection</c>,
    /// <c>IFormCollection</c> and any <c>IEnumerable&lt;IFormFile&gt;</c>, the request's own token for a
    /// <c>CancellationToken</c>. A <c>Type</c> it never binds (<c>ExcludeBindingMetadataProvider</c>). None of them comes
    /// from the query string, and reading into one costs: a <c>CancellationToken</c>'s <c>WaitHandle</c> creates an event
    /// that nothing disposes.
    /// </summary>
    private static readonly Type[] TypesNotBoundFromTheQuery =
    [
        typeof(IFormFile),
        typeof(IFormFileCollection),
        typeof(IFormCollection),
        typeof(IEnumerable<IFormFile>),
        typeof(CancellationToken),
        typeof(Type)
    ];

    /// <summary>
    /// The sources an attribute such as <c>[FromHeader]</c> names that the query string never fills.
    /// </summary>
    private static readonly BindingSource[] SourcesNotFilledByTheQuery =
    [
        BindingSource.Header,
        BindingSource.Form,
        BindingSource.FormFile,
        BindingSource.Services,
        BindingSource.Body,
        BindingSource.Special
    ];

    private static readonly ConcurrentDictionary<Type, bool> IsScalarByType = new();
    private static readonly ConcurrentDictionary<Type, bool> HasModelBinderByType = new();
    private static readonly ConcurrentDictionary<Type, bool> HasDeclaredTypeConverterByType = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, ParameterInfo>> ConstructorParametersByType = new();

    /// <summary>
    /// Formats a single route or query value with the invariant culture and ISO 8601 dates.
    /// </summary>
    public static string Format(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            // MVC's ByteArrayModelBinder reads a byte[] from one Base64 value.
            byte[] bytes => Convert.ToBase64String(bytes),
            bool boolean => boolean ? "true" : "false",
            DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),
            // The binder reads such a type back through the converter it declares, whose text can differ from the
            // type's own IFormattable output.
            _ when HasDeclaredTypeConverter(value.GetType()) => ConvertToInvariantString(value),
            Enum enumValue => enumValue.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            // The binder reads a type with a converter back through that converter, which a record's ToString() would not round-trip.
            _ => ConvertToInvariantString(value)
        };
    }

    /// <summary>
    /// Adds a query parameter. A null value adds nothing, a value the binder reads from one string (a type with a
    /// converter or a <c>TryParse</c>, a strongly-typed id among them) is sent whole, a collection repeats the key
    /// once per item, and any other object (a <c>[FromQuery]</c> filter) adds each property under the <c>name.Property</c> and
    /// <c>name.Items[0].Property</c> keys the binder expects - sending the object's <c>ToString()</c> instead
    /// binds nothing and the endpoint silently runs with an empty filter. A value of a type with a model binder of its
    /// own (<c>[ModelBinder(typeof(...))]</c>) is sent whole as well, as that binder reads its key, and a value MVC binds
    /// from a source of its own, such as a <c>CancellationToken</c> or an <c>IFormFile</c>, not at all.
    /// </summary>
    public static void AddQueryValue(List<string> queryParts, string name, object? value)
    {
        AddValue(queryParts, name, value, depth: 0);
    }

    private static void AddValue(List<string> queryParts, string key, object? value, int depth)
    {
        if (value is null || IsNotBoundFromTheQuery(value.GetType()))
        {
            return;
        }

        if (IsScalar(value.GetType()))
        {
            AddWholeValue(queryParts, key, value);
            return;
        }

        if (HasModelBinder(value.GetType()))
        {
            AddBinderValue(queryParts, key, value);
            return;
        }

        if (depth > MaxObjectDepth)
        {
            throw new InvalidOperationException(
                $"Query object '{value.GetType().Name}' is nested deeper than {MaxObjectDepth} levels; a [FromQuery] parameter cannot hold a cyclic graph.");
        }

        if (value is IEnumerable items)
        {
            // Only the objects actually sent are numbered: the binder reads Items[0], Items[1], ... and stops at the
            // first missing index, so a null item must not leave a gap.
            var index = 0;
            foreach (var item in items)
            {
                if (item is null)
                {
                    continue;
                }

                if (IsScalar(item.GetType()) || HasModelBinder(item.GetType()))
                {
                    AddValue(queryParts, key, item, depth + 1);
                    continue;
                }

                // An object item expands into Items[0].Property keys, a nested collection repeats Items[0] per value.
                // An item that adds nothing (only null properties, an empty collection) takes no index.
                var queryPartCountBeforeItem = queryParts.Count;
                AddValue(queryParts, $"{key}[{index}]", item, depth + 1);
                if (queryParts.Count > queryPartCountBeforeItem)
                {
                    index++;
                }
            }

            return;
        }

        AddProperties(queryParts, key, value, depth + 1);
    }

    private static void AddWholeValue(List<string> queryParts, string key, object value)
    {
        queryParts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(Format(value))}");
    }

    /// <summary>
    /// A value a model binder of its own reads from the query string under its key: one value, or for a collection
    /// each item under the same key, all of which the binder finds in the key's <c>Values</c>.
    /// </summary>
    private static void AddBinderValue(List<string> queryParts, string key, object value)
    {
        if (value is not IEnumerable items || IsScalar(value.GetType()))
        {
            AddWholeValue(queryParts, key, value);
            return;
        }

        foreach (var item in items)
        {
            if (item is not null)
            {
                AddWholeValue(queryParts, key, item);
            }
        }
    }

    private static void AddProperties(List<string> queryParts, string prefix, object value, int depth)
    {
        var type = value.GetType();
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            // MVC binds only a property whose getter is public (PropertyHelper.GetVisibleProperties). A value it never
            // binds would still carry the prefix, which makes MVC bind the whole parameter under it.
            if (property.GetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            // Decided before the property is read, so a value that is not sent is never read into either.
            var (attributes, isConstructorParameter) = GetBindingAttributes(type, property);
            if (!IsBoundFromTheQuery(type, property.PropertyType, attributes, isConstructorParameter))
            {
                continue;
            }

            var key = $"{prefix}.{GetBinderModelName(attributes, property)}";
            var propertyValue = property.GetValue(value);
            if (propertyValue is not null && attributes.OfType<IBinderTypeProviderMetadata>().Any(x => x.BinderType is not null))
            {
                AddBinderValue(queryParts, key, propertyValue);
                continue;
            }

            AddValue(queryParts, key, propertyValue, depth);
        }
    }

    /// <summary>
    /// The attributes MVC binds a property with, and whether it binds it as a constructor parameter. For a property a
    /// record's primary constructor sets, those of the constructor parameter it binds the property through; one on the
    /// property it ignores.
    /// </summary>
    private static (object[] Attributes, bool IsConstructorParameter) GetBindingAttributes(Type type, PropertyInfo property)
    {
        return GetConstructorParameters(type).TryGetValue(property.Name, out var parameter)
            ? (parameter.GetCustomAttributes(inherit: true), true)
            : (property.GetCustomAttributes(inherit: true), false);
    }

    /// <summary>
    /// Whether MVC can read the property from the query string: not when its type is bound from a source of its own,
    /// when <c>[BindNever]</c> on it or on its class excludes it, or when a binding attribute names a source the query
    /// string never fills, <c>[FromHeader]</c>, <c>[FromForm]</c>, <c>[FromServices]</c>, <c>[FromKeyedServices]</c> or
    /// <c>[FromBody]</c>. A property with a model binder of its own reads its key from the query string as well.
    /// </summary>
    private static bool IsBoundFromTheQuery(Type type, Type propertyType, object[] attributes, bool isConstructorParameter)
    {
        if (IsNotBoundFromTheQuery(propertyType))
        {
            return false;
        }

        // MVC falls back to the class's [BindNever] for a property, but a constructor parameter's binding behavior only
        // its own attributes decide (DefaultBindingMetadataProvider.FindBindingBehavior).
        var bindingBehavior = attributes.OfType<BindingBehaviorAttribute>().FirstOrDefault() ??
            (isConstructorParameter ? null : type.GetCustomAttributes(typeof(BindingBehaviorAttribute), inherit: true).OfType<BindingBehaviorAttribute>().FirstOrDefault());
        if (bindingBehavior?.Behavior == BindingBehavior.Never || attributes.OfType<FromKeyedServicesAttribute>().Any())
        {
            return false;
        }

        if (attributes.OfType<IBinderTypeProviderMetadata>().Any(x => x.BinderType is not null))
        {
            return true;
        }

        var bindingSource = attributes.OfType<IBindingSourceMetadata>().Select(x => x.BindingSource).FirstOrDefault(x => x is not null);

        return bindingSource is null || !SourcesNotFilledByTheQuery.Contains(bindingSource);
    }

    /// <summary>
    /// The key the binder reads a property under: the name a binding attribute such as <c>[FromQuery(Name = "q")]</c>
    /// gives it, else its own name.
    /// </summary>
    private static string GetBinderModelName(object[] attributes, PropertyInfo property)
    {
        return attributes
            .OfType<IModelNameProvider>()
            .Select(x => x.Name)
            .FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? property.Name;
    }

    internal static bool IsNotBoundFromTheQuery(Type type)
    {
        return TypesNotBoundFromTheQuery.Any(x => x.IsAssignableFrom(type));
    }

    /// <summary>
    /// Whether the type names a model binder of its own, <c>[ModelBinder(typeof(...))]</c>, which MVC hands the value
    /// under the type's key, as one string, rather than binding the type's properties. A nullable <c>T?</c> does not
    /// carry the attributes of <c>T</c>, so MVC binds it from no form of query value; one is sent whole all the same.
    /// </summary>
    private static bool HasModelBinder(Type type)
    {
        return HasModelBinderByType.GetOrAdd(
            type,
            x => x.GetCustomAttributes(inherit: true).OfType<IBinderTypeProviderMetadata>().Any(attribute => attribute.BinderType is not null));
    }

    private static IReadOnlyDictionary<string, ParameterInfo> GetConstructorParameters(Type type)
    {
        return ConstructorParametersByType.GetOrAdd(type, FindConstructorParameters);
    }

    /// <summary>
    /// The parameters of the primary constructor MVC binds a record through, by the property each sets
    /// (<c>DefaultBindingMetadataProvider.GetBoundConstructor</c>): of a record class that is not abstract and has exactly
    /// one public constructor, every parameter of which has a property of the same name and type. Empty for any other type.
    /// </summary>
    private static IReadOnlyDictionary<string, ParameterInfo> FindConstructorParameters(Type type)
    {
        var none = new Dictionary<string, ParameterInfo>();
        if (type.IsAbstract || type.IsValueType || type.IsInterface)
        {
            return none;
        }

        var constructors = type.GetConstructors();
        var cloneMethod = type.GetMethod(RecordCloneMethodName, BindingFlags.Public | BindingFlags.Instance);
        var isRecord = cloneMethod is not null && (cloneMethod.ReturnType == type || cloneMethod.ReturnType == type.BaseType);
        if (!isRecord || constructors.Length != 1)
        {
            return none;
        }

        var parameters = constructors[0].GetParameters();
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => x.GetMethod is { IsPublic: true } && x.GetIndexParameters().Length == 0)
            .ToList();
        var parametersByProperty = new Dictionary<string, ParameterInfo>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            var isMapped = properties.Any(x => string.Equals(x.Name, parameter.Name, StringComparison.Ordinal) && x.PropertyType == parameter.ParameterType);
            if (!isMapped)
            {
                // Not a primary constructor: MVC binds the record's properties like any other type's.
                return none;
            }

            parametersByProperty[parameter.Name!] = parameter;
        }

        return parametersByProperty;
    }

    private static bool HasDeclaredTypeConverter(Type type)
    {
        return HasDeclaredTypeConverterByType.GetOrAdd(type, x => x.IsDefined(typeof(TypeConverterAttribute), inherit: true));
    }

    private static string ConvertToInvariantString(object value)
    {
        return TypeDescriptor.GetConverter(value.GetType()).ConvertToInvariantString(value) ?? string.Empty;
    }

    private static bool IsScalar(Type type)
    {
        return IsScalarByType.GetOrAdd(Nullable.GetUnderlyingType(type) ?? type, IsBoundFromOneValue);
    }

    /// <summary>
    /// The binder's own rule for a simple type: one it can read from a single string, through a type converter, a
    /// static <c>TryParse</c> or, for a <c>byte[]</c>, Base64. A strongly-typed id is such a type, and expanding it into its properties instead
    /// binds nothing. Every other type is complex and bound property by property.
    /// </summary>
    private static bool IsBoundFromOneValue(Type type)
    {
        return type == typeof(byte[]) ||
            TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string)) ||
            HasTryParse(type);
    }

    /// <summary>
    /// Whether MVC finds a <c>TryParse</c> to read the type with (<c>ParameterBindingMethodCache.FindTryParseMethod</c>):
    /// an explicit <c>IParsable&lt;T&gt;.TryParse</c> the type declares, else a public static
    /// <c>bool TryParse(string, IFormatProvider, out T)</c> or <c>bool TryParse(string, out T)</c> that the type or a
    /// base class declares, or that exactly one of its interfaces implements with a body.
    /// </summary>
    private static bool HasTryParse(Type type)
    {
        return DeclaresExplicitParsableTryParse(type) ||
            HasTryParse(type, [typeof(string), typeof(IFormatProvider), type.MakeByRefType()]) ||
            HasTryParse(type, [typeof(string), type.MakeByRefType()]);
    }

    /// <summary>
    /// MVC looks the explicit implementation up by the name the compiler gives it, which holds the type's full name with
    /// dots between nested types. A generic type's never matches, as the compiler names it after the type parameters.
    /// </summary>
    private static bool DeclaresExplicitParsableTryParse(Type type)
    {
        var explicitTryParseName = $"{ParsableInterfaceName}<{type.FullName?.Replace('+', '.')}>.{TryParseMethodName}";

        return type.GetMethod(explicitTryParseName, BindingFlags.Static | BindingFlags.NonPublic) is not null;
    }

    private static bool HasTryParse(Type type, Type[] parameterTypes)
    {
        var declaredTryParse = type.GetMethod(TryParseMethodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy, parameterTypes);
        if (IsTryParse(declaredTryParse))
        {
            return true;
        }

        // When two interfaces offer one, MVC takes neither.
        return type
            .GetInterfaces()
            .Count(x => IsTryParse(x.GetMethod(TryParseMethodName, BindingFlags.Public | BindingFlags.Static, parameterTypes))) == 1;
    }

    private static bool IsTryParse(MethodInfo? method)
    {
        return method is { IsAbstract: false } && method.ReturnType == typeof(bool);
    }
}
