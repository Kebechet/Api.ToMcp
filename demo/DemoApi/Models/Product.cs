namespace DemoApi.Models;

public class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string? Description { get; set; }
    public string Category { get; set; } = "";
}

public class CreateProductRequest
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string? Description { get; set; }
    public string Category { get; set; } = "";
}
