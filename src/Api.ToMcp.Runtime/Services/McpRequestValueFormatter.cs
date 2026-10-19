using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;

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

    private static readonly string ParsableInterfaceName = $"{typeof(IParsable<>).Namespace}.{nameof(IParsable<int>)}";

    private static readonly ConcurrentDictionary<Type, bool> IsScalarByType = new();
    private static readonly ConcurrentDictionary<Type, bool> HasDeclaredTypeConverterByType = new();

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
    /// binds nothing and the endpoint silently runs with an empty filter.
    /// </summary>
    public static void AddQueryValue(List<string> queryParts, string name, object? value)
    {
        AddValue(queryParts, name, value, depth: 0);
    }

    private static void AddValue(List<string> queryParts, string key, object? value, int depth)
    {
        if (value is null)
        {
            return;
        }

        if (IsScalar(value.GetType()))
        {
            queryParts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(Format(value))}");
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

                if (IsScalar(item.GetType()))
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

    private static void AddProperties(List<string> queryParts, string prefix, object value, int depth)
    {
        var properties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            AddValue(queryParts, $"{prefix}.{GetBinderModelName(property)}", property.GetValue(value), depth);
        }
    }

    /// <summary>
    /// The key the binder reads a property under: the name a binding attribute such as <c>[FromQuery(Name = "q")]</c>
    /// gives it, else its own name.
    /// </summary>
    private static string GetBinderModelName(PropertyInfo property)
    {
        return property
            .GetCustomAttributes(inherit: true)
            .OfType<IModelNameProvider>()
            .Select(x => x.Name)
            .FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? property.Name;
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
