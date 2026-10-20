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
    public readonly record struct OrderId(Guid Value);

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
