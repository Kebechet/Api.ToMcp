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

    public enum OrderStatus
    {
        Open,
        Closed
    }

    public sealed class ProductFilter
    {
        public string? City { get; set; }
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
