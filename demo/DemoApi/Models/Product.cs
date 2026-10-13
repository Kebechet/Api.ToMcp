namespace DemoApi.Models;

/// <summary>A product in the catalog.</summary>
public class Product
{
    /// <summary>Unique identifier of the product.</summary>
    public Guid Id { get; set; }

    /// <summary>Display name of the product.</summary>
    public string Name { get; set; } = "";

    /// <summary>Price of the product.</summary>
    public decimal Price { get; set; }

    /// <summary>Longer description of the product, if it has one.</summary>
    public string? Description { get; set; }

    /// <summary>Category the product belongs to, such as Electronics or Kitchen.</summary>
    public string Category { get; set; } = "";
}

public class CreateProductRequest
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string? Description { get; set; }
    public string Category { get; set; } = "";
}
