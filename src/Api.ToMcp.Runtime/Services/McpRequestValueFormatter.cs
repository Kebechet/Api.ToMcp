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
/// Writes tool arguments into route segments, query strings, forms and headers in the form ASP.NET Core model binding
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

    private const char PathSeparator = RouteTokens.PathSeparator;

    /// <summary>
    /// The path segments that URI normalization removes (RFC 3986, section 5.2.4), <c>..</c> with the segment before it.
    /// </summary>
    private const string CurrentSegment = ".";
    private const string ParentSegment = "..";

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

    /// <summary>
    /// The sources an attribute such as <c>[FromQuery]</c> names that the fields of an
    /// <c>application/x-www-form-urlencoded</c> form never fill.
    /// </summary>
    private static readonly BindingSource[] SourcesNotFilledByTheForm =
    [
        BindingSource.Header,
        BindingSource.Query,
        BindingSource.Path,
        BindingSource.FormFile,
        BindingSource.Services,
        BindingSource.Body,
        BindingSource.Special
    ];

    /// <summary>
    /// The characters besides visible ASCII ones that an HTTP header value may hold (RFC 9110, section 5.5); Kestrel
    /// refuses a request with any other, and <c>HttpClient</c> sends none. Kestrel drops them around a value.
    /// </summary>
    private static readonly char[] HeaderWhitespace = [' ', '\t'];

    /// <summary>
    /// The separator MVC splits a header's value at to read an array (<c>GetCommaSeparatedValues</c>), and the quote
    /// that keeps an item holding it whole, which MVC removes.
    /// </summary>
    private const char HeaderValueSeparator = ',';
    private const char HeaderValueQuote = '"';

    /// <summary>
    /// The standard format of a date in a culture, as <c>DateOnlyConverter</c> writes and reads it.
    /// </summary>
    private const string ShortDateFormat = "d";

    private static readonly ConcurrentDictionary<Type, bool> IsScalarByType = new();
    private static readonly ConcurrentDictionary<Type, bool> HasModelBinderByType = new();
    private static readonly ConcurrentDictionary<Type, bool> HasDeclaredTypeConverterByType = new();
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, ParameterInfo>> ConstructorParametersByType = new();
    private static readonly ConcurrentDictionary<Type, bool> IsNotBoundFromTheQueryByType = new();

    /// <summary>
    /// The query string and the form, each with what MVC reads of each type's properties from it, decided on the type's
    /// first call, so that a call only reads the values. Like the caches above, each holds one entry per type a tool
    /// sends, not per call.
    /// </summary>
    private static readonly RequestPart Query = new(SourcesNotFilledByTheQuery, Format);
    private static readonly RequestPart Form = new(SourcesNotFilledByTheForm, FormatFormValue);

    /// <summary>
    /// Whether a route value, as a tool writes it into the URL, sends the request to another path: empty where the route
    /// needs a value, which leaves its segment out, or a <c>.</c> or <c>..</c> segment, which <c>System.Uri</c> removes
    /// before the request is sent, and Kestrel when it receives one. Escaping the dots does not help: <c>System.Uri</c>
    /// decodes <c>%2E</c> before it removes them. The path then reaches another action of the same HTTP method, one the
    /// app keeps out of MCP included. A catch-all
    /// value spans several segments, each of which counts; any other value has its slashes escaped, so it is one.
    /// </summary>
    /// <param name="routeValue">The value as it goes into the URL, escaped.</param>
    /// <param name="isRequired">Whether the route needs the value, so an empty one changes the path too.</param>
    public static bool ChangesThePath(string routeValue, bool isRequired)
    {
        if (routeValue.Length == 0)
        {
            return isRequired;
        }

        return routeValue.Split(PathSeparator).Any(x => x is CurrentSegment or ParentSegment);
    }

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
        AddValue(queryParts, name, value, depth: 0, Query);
    }

    /// <summary>
    /// Adds a field of an <c>application/x-www-form-urlencoded</c> form, in the form <see cref="AddQueryValue"/> adds a
    /// query parameter, which is the form's encoding too, so the parts joined with <c>&amp;</c> are the form. An object
    /// adds each property MVC reads from the form, under the <c>name.Property</c> keys its form binding reads: not one
    /// that a binding attribute such as <c>[FromQuery]</c> or <c>[FromHeader]</c> binds from elsewhere, nor a form file.
    /// MVC reads a form with the culture of the request, not the invariant one, so a number and a date are written in
    /// <see cref="CultureInfo.CurrentCulture"/>, which <see cref="McpHttpRequest.SendAsync"/> names in the request's
    /// <c>Accept-Language</c> for <c>UseRequestLocalization</c> to pick. False when a value is a date outside the calendar
    /// that culture writes dates in, which no text in it names, such as 0001-01-01, the date a property left out gets, in
    /// the Persian calendar of <c>fa-IR</c>; the fields added before it stay.
    /// </summary>
    public static bool TryAddFormValue(List<string> formParts, string name, object? value)
    {
        return AddValue(formParts, name, value, depth: 0, Form);
    }

    /// <summary>
    /// Adds a request header, its value written as a query value is, without the spaces and tabs around it, which Kestrel
    /// drops, so a value arrives the same in process and over HTTP. A null value adds nothing, and a collection one value
    /// per item, which MVC reads back as the items of an array once it splits the header at its commas: an item that
    /// holds a comma goes in quotes, which MVC removes. False, and nothing added, when a value holds a character an HTTP
    /// header cannot carry, anything but visible ASCII characters, spaces and tabs, or when an item is one MVC would read
    /// back otherwise: an empty one, which it drops, or one with a quote, which it removes or splits at.
    /// </summary>
    public static bool TryAddHeaderValue(List<KeyValuePair<string, string>> headers, string name, object? value)
    {
        if (value is null)
        {
            return true;
        }

        var items = value as IEnumerable;
        if (items is null || IsScalar(value.GetType()))
        {
            var text = TrimHeaderValue(Format(value));
            if (!IsHeaderValue(text))
            {
                return false;
            }

            headers.Add(new KeyValuePair<string, string>(name, text));
            return true;
        }

        var values = new List<string>();
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            var text = TrimHeaderValue(Format(item));
            if (text.Length == 0 || text.Contains(HeaderValueQuote) || !IsHeaderValue(text))
            {
                return false;
            }

            values.Add(text.Contains(HeaderValueSeparator) ? HeaderValueQuote + text + HeaderValueQuote : text);
        }

        headers.AddRange(values.Select(x => new KeyValuePair<string, string>(name, x)));
        return true;
    }

    internal static string TrimHeaderValue(string value)
    {
        return value.Trim(HeaderWhitespace);
    }

    /// <summary>
    /// Whether HTTP lets a header carry the value: visible ASCII characters, spaces and tabs only.
    /// </summary>
    internal static bool IsHeaderValue(string value)
    {
        return value.All(x => x is >= '!' and <= '~' || HeaderWhitespace.Contains(x));
    }

    /// <summary>
    /// A form value as MVC's form binding reads it back with <see cref="CultureInfo.CurrentCulture"/>: a number and a
    /// date in that culture, whose decimal separator, digit grouping, sign and calendar MVC applies; any other value as
    /// <see cref="Format"/> writes it, whose text every culture reads the same. Null for a date outside the culture's
    /// calendar.
    /// </summary>
    private static string? FormatFormValue(object value)
    {
        var culture = CultureInfo.CurrentCulture;

        return value switch
        {
            DateOnly date => IsInCalendar(date, culture.DateTimeFormat.Calendar) ? date.ToString(ShortDateFormat, culture) : null,
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Half or Int128 or UInt128 =>
                ((IFormattable)value).ToString(null, culture),
            _ => Format(value)
        };
    }

    /// <summary>
    /// Whether the calendar holds the date: the Persian calendar of <c>fa-IR</c> starts in 622 and the Umm al-Qura one of
    /// <c>ar-SA</c> spans 1900 to 2077, and <c>ToString</c> throws for a date outside them.
    /// </summary>
    private static bool IsInCalendar(DateOnly date, Calendar calendar)
    {
        var dateTime = date.ToDateTime(TimeOnly.MinValue);

        return dateTime >= calendar.MinSupportedDateTime && dateTime <= calendar.MaxSupportedDateTime;
    }

    /// <returns>False when the part cannot hold a value, which <see cref="RequestPart.Format"/> tells.</returns>
    private static bool AddValue(List<string> queryParts, string key, object? value, int depth, RequestPart part)
    {
        if (value is null || IsNotBoundFromTheQuery(value.GetType()))
        {
            return true;
        }

        if (IsScalar(value.GetType()))
        {
            return AddWholeValue(queryParts, key, value, part);
        }

        if (HasModelBinder(value.GetType()))
        {
            return AddBinderValue(queryParts, key, value, part);
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
                    if (!AddValue(queryParts, key, item, depth + 1, part))
                    {
                        return false;
                    }

                    continue;
                }

                // An object item expands into Items[0].Property keys, a nested collection repeats Items[0] per value.
                // An item that adds nothing (only null properties, an empty collection) takes no index.
                var queryPartCountBeforeItem = queryParts.Count;
                if (!AddValue(queryParts, $"{key}[{index}]", item, depth + 1, part))
                {
                    return false;
                }

                if (queryParts.Count > queryPartCountBeforeItem)
                {
                    index++;
                }
            }

            return true;
        }

        return AddProperties(queryParts, key, value, depth + 1, part);
    }

    private static bool AddWholeValue(List<string> queryParts, string key, object value, RequestPart part)
    {
        var text = part.Format(value);
        if (text is null)
        {
            return false;
        }

        queryParts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(text)}");
        return true;
    }

    /// <summary>
    /// A value a model binder of its own reads from the query string under its key: one value, or for a collection
    /// each item under the same key, all of which the binder finds in the key's <c>Values</c>.
    /// </summary>
    private static bool AddBinderValue(List<string> queryParts, string key, object value, RequestPart part)
    {
        if (value is not IEnumerable items || IsScalar(value.GetType()))
        {
            return AddWholeValue(queryParts, key, value, part);
        }

        foreach (var item in items)
        {
            if (item is not null && !AddWholeValue(queryParts, key, item, part))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AddProperties(List<string> queryParts, string prefix, object value, int depth, RequestPart part)
    {
        foreach (var property in GetQueryProperties(value.GetType(), part))
        {
            // An empty name adds no segment to the key, as MVC joins it (ModelNames.CreatePropertyModelName).
            var key = property.Key.Length == 0 ? prefix : $"{prefix}.{property.Key}";
            var propertyValue = property.Property.GetValue(value);
            var isAdded = propertyValue is not null && property.HasModelBinder
                ? AddBinderValue(queryParts, key, propertyValue, part)
                : AddValue(queryParts, key, propertyValue, depth, part);
            if (!isAdded)
            {
                return false;
            }
        }

        return true;
    }

    private static QueryProperty[] GetQueryProperties(Type type, RequestPart part)
    {
        return part.PropertiesByType.GetOrAdd(type, static (x, unfilledSources) => FindQueryProperties(x, unfilledSources), part.UnfilledSources);
    }

    /// <summary>
    /// The properties of the type MVC can read from the query string, or from the form, in the order the type gives them,
    /// each with the key it reads it under and whether a model binder of its own reads it.
    /// </summary>
    private static QueryProperty[] FindQueryProperties(Type type, BindingSource[] unfilledSources)
    {
        var queryProperties = new List<QueryProperty>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // MVC binds only a property whose getter is public (PropertyHelper.GetVisibleProperties). A value it never
            // binds would still carry the prefix, which makes MVC bind the whole parameter under it.
            if (property.GetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            // Decided before the property is read, so a value that is not sent is never read into either.
            var (attributes, isConstructorParameter) = GetBindingAttributes(type, property);
            if (!IsBoundFrom(type, property.PropertyType, attributes, isConstructorParameter, unfilledSources))
            {
                continue;
            }

            queryProperties.Add(new QueryProperty(
                property,
                GetBinderModelName(attributes, property),
                attributes.OfType<IBinderTypeProviderMetadata>().Any(x => x.BinderType is not null)));
        }

        return queryProperties.ToArray();
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
    /// Whether MVC can read the property from the query string, or from the form: not when its type is bound from a
    /// source of its own, when <c>[BindNever]</c> on it or on its class excludes it, or when a binding attribute names a
    /// source of <paramref name="unfilledSources"/> or <c>[FromKeyedServices]</c>: for the query string
    /// <c>[FromHeader]</c>, <c>[FromForm]</c>, <c>[FromServices]</c> or <c>[FromBody]</c>. A property with a model binder of
    /// its own reads its key from the query string and the form as well.
    /// </summary>
    private static bool IsBoundFrom(Type type, Type propertyType, object[] attributes, bool isConstructorParameter, BindingSource[] unfilledSources)
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

        return bindingSource is null || !unfilledSources.Contains(bindingSource);
    }

    /// <summary>
    /// The name the binder reads a property under: the one a binding attribute such as <c>[FromQuery(Name = "q")]</c>
    /// gives it, else the one an attribute of its type gives, such as <c>[Bind(Prefix = "pf")]</c>, else its own. MVC
    /// reads the type's attributes after the property's, and takes the first name that is not null, an empty one
    /// included (<c>ModelAttributes.GetAttributesForProperty</c>, <c>DefaultBindingMetadataProvider</c>).
    /// </summary>
    private static string GetBinderModelName(object[] attributes, PropertyInfo property)
    {
        return attributes
            .Concat(property.PropertyType.GetCustomAttributes(inherit: true))
            .OfType<IModelNameProvider>()
            .Select(x => x.Name)
            .FirstOrDefault(x => x is not null) ?? property.Name;
    }

    internal static bool IsNotBoundFromTheQuery(Type type)
    {
        return IsNotBoundFromTheQueryByType.GetOrAdd(type, x => TypesNotBoundFromTheQuery.Any(notBound => notBound.IsAssignableFrom(x)));
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

    /// <summary>
    /// A property MVC can read from the query string, or from the form.
    /// </summary>
    /// <param name="Property">The property, read on each call.</param>
    /// <param name="Key">The key MVC reads it under after the prefix: the name a binding attribute gives it, else its own.</param>
    /// <param name="HasModelBinder">Whether a model binder of its own reads it, which takes its value whole.</param>
    private sealed record QueryProperty(PropertyInfo Property, string Key, bool HasModelBinder);

    /// <summary>
    /// A part of the request a value goes into, by the binding sources that never fill it and how a value is written into
    /// it, or null for a value it cannot hold, with what MVC reads of each type's properties from it.
    /// </summary>
    private sealed class RequestPart(BindingSource[] unfilledSources, Func<object, string?> format)
    {
        public BindingSource[] UnfilledSources { get; } = unfilledSources;

        public Func<object, string?> Format { get; } = format;

        public ConcurrentDictionary<Type, QueryProperty[]> PropertiesByType { get; } = new();
    }
}
