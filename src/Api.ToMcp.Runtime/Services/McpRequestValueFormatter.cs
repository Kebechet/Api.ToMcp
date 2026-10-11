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
    /// Query-bound filters are shallow; anything deeper than this is a cyclic graph, not a filter.
    /// </summary>
    private const int MaxObjectDepth = 8;

    private const string TryParseMethodName = "TryParse";

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
                AddValue(queryParts, $"{key}[{index}]", item, depth + 1);
                index++;
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
    /// The binder's own rule for a simple type: one it can read from a single string, through a type converter or a
    /// static <c>TryParse</c>. A strongly-typed id is such a type, and expanding it into its properties instead
    /// binds nothing. Every other type is complex and bound property by property.
    /// </summary>
    private static bool IsBoundFromOneValue(Type type)
    {
        return TypeDescriptor.GetConverter(type).CanConvertFrom(typeof(string)) ||
            type
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(x => x.Name == TryParseMethodName && IsTryParseSignature(x, type));
    }

    private static bool IsTryParseSignature(MethodInfo method, Type type)
    {
        var parameterTypes = method
            .GetParameters()
            .Select(x => x.ParameterType)
            .ToList();
        var resultType = type.MakeByRefType();

        return method.ReturnType == typeof(bool) &&
            (
                parameterTypes.SequenceEqual([typeof(string), resultType]) ||
                parameterTypes.SequenceEqual([typeof(string), typeof(IFormatProvider), resultType])
            );
    }
}
