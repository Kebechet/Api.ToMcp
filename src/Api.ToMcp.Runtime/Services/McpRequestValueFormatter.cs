using System.Collections;
using System.Globalization;
using System.Reflection;

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
            Enum enumValue => enumValue.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    /// <summary>
    /// Adds a query parameter. A null value adds nothing, a collection repeats the key once per item, and an
    /// object (a <c>[FromQuery]</c> filter) adds each property under the <c>name.Property</c> and
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

        if (value is IEnumerable items)
        {
            var index = 0;
            foreach (var item in items)
            {
                if (item is not null && !IsScalar(item.GetType()))
                {
                    AddProperties(queryParts, $"{key}[{index}]", item, depth + 1);
                }
                else
                {
                    AddValue(queryParts, key, item, depth + 1);
                }

                index++;
            }

            return;
        }

        AddProperties(queryParts, key, value, depth + 1);
    }

    private static void AddProperties(List<string> queryParts, string prefix, object value, int depth)
    {
        if (depth > MaxObjectDepth)
        {
            throw new InvalidOperationException(
                $"Query object '{value.GetType().Name}' is nested deeper than {MaxObjectDepth} levels; a [FromQuery] parameter cannot hold a cyclic graph.");
        }

        var properties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var property in properties)
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            AddValue(queryParts, $"{prefix}.{property.Name}", property.GetValue(value), depth);
        }
    }

    private static bool IsScalar(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        return underlyingType.IsPrimitive ||
            underlyingType.IsEnum ||
            underlyingType == typeof(string) ||
            underlyingType == typeof(decimal) ||
            underlyingType == typeof(Guid) ||
            underlyingType == typeof(DateTime) ||
            underlyingType == typeof(DateTimeOffset) ||
            underlyingType == typeof(DateOnly) ||
            underlyingType == typeof(TimeOnly) ||
            underlyingType == typeof(TimeSpan) ||
            underlyingType == typeof(Uri);
    }
}
