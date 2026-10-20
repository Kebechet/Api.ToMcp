using DemoApi.Models;

namespace DemoApi.Controllers;

/// <summary>
/// Declares no action: it serves the GetAll it inherits, at GET /api/categories.
/// </summary>
public class CategoriesController : ListControllerBase<Category>
{
    private static readonly List<Category> _categories = new()
    {
        new Category { Name = "Electronics", Description = "Devices and their accessories" },
        new Category { Name = "Kitchen", Description = "Things to cook and serve with" }
    };

    protected override IReadOnlyList<Category> Items => _categories;
}
