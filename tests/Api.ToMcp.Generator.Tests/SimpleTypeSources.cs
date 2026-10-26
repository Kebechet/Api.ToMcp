namespace Api.ToMcp.Generator.Tests;

/// <summary>
/// Types MVC binds from one string value - strongly-typed ids with a type converter or a static <c>TryParse</c> - and
/// look-alikes it binds property by property, shared by the tests of where a generated tool sends a parameter.
/// </summary>
internal static class SimpleTypeSources
{
    public const string Usings = @"#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
";

    public const string Types = @"
namespace TestApi
{
    [TypeConverter(typeof(OrderIdConverter))]
    public readonly record struct OrderId(Guid Value)
    {
        // The text OrderIdConverter reads back; the record's generated one would not parse.
        public override string ToString() => Value.ToString();
    }

    public sealed class OrderIdConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            => value is string text ? new OrderId(Guid.Parse(text)) : base.ConvertFrom(context, culture, value);
    }

    [TypeConverter(typeof(CustomerIdConverter))]
    public sealed record CustomerId(int Value);

    public sealed class CustomerIdConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            => value is string text ? new CustomerId(int.Parse(text, CultureInfo.InvariantCulture)) : base.ConvertFrom(context, culture, value);
    }

    // Reads a string through the CanConvertFrom of GuidConverter, which it does not override.
    [TypeConverter(typeof(InvoiceIdConverter))]
    public readonly record struct InvoiceId(Guid Value);

    public sealed class InvoiceIdConverter : GuidConverter
    {
        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            => new InvoiceId((Guid)base.ConvertFrom(context, culture, value)!);
    }

    // The converter of a base record applies to every id derived from it.
    [TypeConverter(typeof(EntityIdConverter))]
    public abstract record EntityId(Guid Value);

    public sealed record ProjectId(Guid Value) : EntityId(Value);

    public sealed class EntityIdConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    // TypeDescriptor takes the converter of a public interface too, after any of the type and its base classes.
    [TypeConverter(typeof(IfaceIdConverter))]
    public interface IIfaceId
    {
    }

    public sealed class IfaceIdConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
            => value is string text ? new IfaceId(int.Parse(text, CultureInfo.InvariantCulture)) : base.ConvertFrom(context, culture, value);
    }

    public readonly record struct IfaceId(int Value) : IIfaceId;

    // Its own converter, which reads no string, wins over the interface's.
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class ExpandableIfaceFilter : IIfaceId
    {
        public string? City { get; set; }
    }

    // TypeDescriptor skips the attributes of an interface that is not public.
    [TypeConverter(typeof(IfaceIdConverter))]
    internal interface IHiddenIfaceId
    {
    }

    public readonly record struct HiddenIfaceId(int Value) : IHiddenIfaceId;

    public readonly record struct Sku(string Code)
    {
        public static bool TryParse(string? text, out Sku sku)
        {
            sku = new Sku(text ?? """");
            return text is not null;
        }
    }

    public sealed record Slug(string Text)
    {
        public static bool TryParse(string? text, IFormatProvider? provider, out Slug? slug)
        {
            slug = text is null ? null : new Slug(text);
            return slug is not null;
        }
    }

    // MVC finds an explicit IParsable<T>.TryParse by the name the compiler gives it.
    public readonly record struct TicketId(int Value) : IParsable<TicketId>
    {
        static TicketId IParsable<TicketId>.Parse(string s, IFormatProvider? provider) => new(int.Parse(s, provider));

        static bool IParsable<TicketId>.TryParse(string? s, IFormatProvider? provider, out TicketId result)
        {
            var isParsed = int.TryParse(s, NumberStyles.Integer, provider, out var value);
            result = new TicketId(value);
            return isParsed;
        }
    }

    // ISpanParsable<T> extends IParsable<T>, whose TryParse is the one MVC looks for.
    public readonly record struct ShelfCode(string Value) : ISpanParsable<ShelfCode>
    {
        static ShelfCode IParsable<ShelfCode>.Parse(string s, IFormatProvider? provider) => new(s);

        static bool IParsable<ShelfCode>.TryParse(string? s, IFormatProvider? provider, out ShelfCode result)
        {
            result = new ShelfCode(s ?? """");
            return s is not null;
        }

        static ShelfCode ISpanParsable<ShelfCode>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => new(s.ToString());

        static bool ISpanParsable<ShelfCode>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ShelfCode result)
        {
            result = new ShelfCode(s.ToString());
            return true;
        }
    }

    // A public static TryParse a base record declares for every id derived from it.
    public abstract record IntId<TSelf>(int Value) where TSelf : IntId<TSelf>
    {
        public static bool TryParse(string? text, out TSelf? id)
        {
            id = null;
            return false;
        }
    }

    public sealed record SeatId(int Value) : IntId<SeatId>(Value);

    // A static TryParse with a body, which an interface gives every id implementing it.
    public interface INumberedId<TSelf> where TSelf : INumberedId<TSelf>
    {
        static virtual bool TryParse(string? text, out TSelf? id)
        {
            id = default;
            return false;
        }
    }

    public interface ICodedId<TSelf> where TSelf : ICodedId<TSelf>
    {
        static virtual bool TryParse(string? text, out TSelf? id)
        {
            id = default;
            return false;
        }
    }

    public readonly record struct GateId(int Value) : INumberedId<GateId>;

    // Two interfaces offer a TryParse, and MVC takes neither.
    public readonly record struct DockId(int Value) : INumberedId<DockId>, ICodedId<DockId>;

    public enum OrderStatus
    {
        Open,
        Closed
    }

    public sealed class ProductFilter
    {
        public string? City { get; set; }
    }

    public struct Point
    {
        public int X { get; set; }

        public int Y { get; set; }
    }

    // A property with a binding source of its own keeps [ApiController] from reading the type from the body.
    public sealed class AttributedQuery
    {
        [FromRoute]
        public int Id { get; set; }

        [FromQuery]
        public string? City { get; set; }

        public string? Note { get; set; }

        [FromHeader]
        public string? Tenant { get; set; }
    }

    public sealed class QueryOnlyFilter
    {
        [FromQuery]
        public string? City { get; set; }

        public string? Note { get; set; }
    }

    public sealed class AliasedRouteQuery
    {
        [FromRoute(Name = ""orderId"")]
        public Guid Order { get; set; }

        [FromQuery(Name = ""c"")]
        public string? City { get; set; }
    }

    public sealed class RouteByNameQuery
    {
        public int Id { get; set; }

        [FromQuery]
        public string? City { get; set; }
    }

    // MVC leaves the properties a record's primary constructor sets out of the binding-source check, so it reads both
    // records from the body, whichever the attribute targets.
    public sealed record RecordPropertyFilter([property: FromQuery] string? City, int Page);

    public sealed record RecordParameterFilter([FromQuery] string? City, int Page);

    // A property the constructor does not set still counts.
    public sealed record RecordWithQueryProperty(string? City)
    {
        [FromQuery]
        public int Page { get; set; }
    }

    // MVC binds a property the primary constructor sets through the constructor parameter, with the parameter's binding
    // attributes; a [property: ...] attribute it ignores. Extra makes MVC read these records from the route and query.
    public sealed record RecordNamedCity([FromQuery(Name = ""c"")] string? City, int Page)
    {
        [FromQuery]
        public int Extra { get; set; }
    }

    public sealed record RecordPropertyNamedCity([property: FromQuery(Name = ""c"")] string? City, int Page)
    {
        [FromQuery]
        public int Extra { get; set; }
    }

    public sealed record RecordRouteOrder([FromRoute(Name = ""orderId"")] Guid Order, [FromQuery(Name = ""c"")] string? City)
    {
        [FromQuery]
        public int Page { get; set; }
    }

    public sealed record RecordQueryId([FromQuery] int Id, string? City);

    public sealed record RecordRouteId([property: FromRoute] int Id, string? City);

    // MVC reads Id from the query string, though the route has an {id} for it to fill.
    public sealed class QueryIdFilter
    {
        [FromQuery]
        public int Id { get; set; }

        public string? City { get; set; }
    }

    // [ModelBinder] and [BindProperty] name a binding source only together with a binder type.
    public sealed class NamedModelBinderFilter
    {
        [ModelBinder(Name = ""c"")]
        public string? City { get; set; }

        public int Page { get; set; }
    }

    public sealed class NamedBindPropertyFilter
    {
        [BindProperty(Name = ""c"")]
        public string? City { get; set; }

        public int Page { get; set; }
    }

    public sealed class CustomBinderFilter
    {
        [ModelBinder(typeof(CityBinder))]
        public string? City { get; set; }

        public int Page { get; set; }
    }

    public sealed class CityBinder : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder
    {
        public Task BindModelAsync(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingContext bindingContext) => Task.CompletedTask;
    }

    // MVC reads a property's binding source from the attributes of its type too, and gives a file type one of its own.
    [ModelBinder(typeof(BinderIdBinder))]
    public sealed record BinderId(int Value)
    {
        public static bool TryParse(string? text, out BinderId? id)
        {
            id = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? new BinderId(value) : null;
            return id is not null;
        }

        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class BinderIdBinder : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder
    {
        public Task BindModelAsync(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingContext bindingContext)
        {
            if (BinderId.TryParse(bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue, out var id))
            {
                bindingContext.Result = Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingResult.Success(id);
            }

            return Task.CompletedTask;
        }
    }

    // A model binder of its own makes MVC bind the parameter from the route, query and form values, under its name,
    // whatever the HTTP method: neither the body nor property by property.
    [ModelBinder(typeof(TextOrderIdBinder))]
    public sealed record TextOrderId(string Value)
    {
        public override string ToString() => Value;
    }

    public sealed class TextOrderIdBinder : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder
    {
        public Task BindModelAsync(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingContext bindingContext)
        {
            if (bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue is { } value)
            {
                bindingContext.Result = Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingResult.Success(new TextOrderId(value));
            }

            return Task.CompletedTask;
        }
    }

    public sealed record PlainCode(string Value)
    {
        public override string ToString() => Value;
    }

    public sealed class PlainCodeBinder : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder
    {
        public Task BindModelAsync(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingContext bindingContext)
        {
            if (bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue is { } value)
            {
                bindingContext.Result = Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingResult.Success(new PlainCode(value));
            }

            return Task.CompletedTask;
        }
    }

    public sealed class TypedBinderQuery
    {
        public BinderId? Ref { get; set; }

        public string? City { get; set; }
    }

    // MVC reads the attributes of Nullable<BinderStructId> for the property, which has none of BinderStructId's.
    [ModelBinder(typeof(BinderIdBinder))]
    public readonly record struct BinderStructId(int Value);

    public sealed class NullableBinderQuery
    {
        public BinderStructId? Ref { get; set; }

        public string? City { get; set; }
    }

    public sealed class FileQuery
    {
        public Microsoft.AspNetCore.Http.IFormFile? Attachment { get; set; }

        public string? City { get; set; }
    }

    public sealed class FileListQuery
    {
        public List<Microsoft.AspNetCore.Http.IFormFile>? Attachments { get; set; }

        public string? City { get; set; }
    }

    public sealed class KeywordQuery
    {
        [FromRoute]
        public int Id { get; set; }

        [FromQuery]
        public string? @event { get; set; }
    }

    // MVC binds only a property whose getter is public.
    public sealed class PrivateGetterQuery
    {
        [FromRoute]
        public int Id { get; set; }

        public string? Note { private get; set; }

        public string? Echo => Note;
    }

    public sealed class PrivateGetterFilter
    {
        public string? City { get; set; }

        public string? Secret { private get; set; }
    }

    public sealed class NotedShipment
    {
        public int Id { get; set; }

        [FromQuery]
        public string? Note { get; set; }
    }

    public sealed class Shipment
    {
        public int Id { get; set; }

        public string? Note { get; set; }
    }

    public class BaseQuery
    {
        [FromQuery]
        public string? City { get; set; }
    }

    public sealed class DerivedQuery : BaseQuery
    {
        public string? Note { get; set; }
    }

    // MVC applies the [ApiController] conventions for any attribute implementing IApiBehaviorMetadata.
    public sealed class ApiBehaviorAttribute : Attribute, Microsoft.AspNetCore.Mvc.Infrastructure.IApiBehaviorMetadata
    {
    }

    // A property grid's converter, which never reads a string.
    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class ExpandableFilter
    {
        public string? City { get; set; }
    }

    // Its TryParse yields an int, not a Rating.
    public readonly record struct Rating(int Stars)
    {
        public static bool TryParse(string? text, out int stars) => int.TryParse(text, out stars);
    }
}
";
}
