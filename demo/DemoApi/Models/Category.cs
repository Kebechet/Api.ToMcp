namespace DemoApi.Models;

/// <summary>A group of products.</summary>
public class Category
{
    /// <summary>Name of the category, as products name it.</summary>
    public string Name { get; set; } = "";

    /// <summary>What the category holds.</summary>
    public string Description { get; set; } = "";
}
