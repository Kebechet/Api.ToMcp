using Microsoft.AspNetCore.Mvc;
using DemoApi.Models;
using Api.ToMcp.Abstractions;

namespace DemoApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private static readonly List<Product> _products = new()
    {
        new Product { Id = Guid.NewGuid(), Name = "Laptop", Price = 999.99m, Category = "Electronics", Description = "A powerful laptop" },
        new Product { Id = Guid.NewGuid(), Name = "Headphones", Price = 149.99m, Category = "Electronics", Description = "Wireless headphones" },
        new Product { Id = Guid.NewGuid(), Name = "Coffee Mug", Price = 12.99m, Category = "Kitchen", Description = "A nice coffee mug" }
    };

    /// <summary>
    /// Gets all products with optional category filter.
    /// </summary>
    [HttpGet]
    public Task<IEnumerable<Product>> GetAll([FromQuery] string? category = null)
    {
        var products = category is null
            ? _products
            : _products.Where(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(products);
    }

    /// <summary>
    /// Gets a product by its unique identifier.
    /// </summary>
    [HttpGet("{id:guid}")]
    public Task<ActionResult<Product>> GetById(Guid id)
    {
        var product = _products.FirstOrDefault(p => p.Id == id);
        if (product is null)
            return Task.FromResult<ActionResult<Product>>(NotFound());

        return Task.FromResult<ActionResult<Product>>(product);
    }

    /// <summary>
    /// Creates a new product.
    /// </summary>
    [HttpPost]
    public Task<Product> Create([FromBody] CreateProductRequest request)
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Price = request.Price,
            Description = request.Description,
            Category = request.Category
        };

        _products.Add(product);
        return Task.FromResult(product);
    }

    /// <summary>
    /// Deletes a product. This action is explicitly ignored from MCP exposure.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [McpIgnore]
    public Task<ActionResult> Delete(Guid id)
    {
        var product = _products.FirstOrDefault(p => p.Id == id);
        if (product is null)
            return Task.FromResult<ActionResult>(NotFound());

        _products.Remove(product);
        return Task.FromResult<ActionResult>(NoContent());
    }
}
