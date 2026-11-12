namespace DemoApi.Models;

/// <summary>An item of the to-do list.</summary>
public class Todo
{
    /// <summary>Unique identifier of the item.</summary>
    public int Id { get; set; }

    /// <summary>What has to be done.</summary>
    public string Title { get; set; } = "";

    /// <summary>Whether the item is done.</summary>
    public bool IsDone { get; set; }
}

/// <summary>An item to add to the to-do list.</summary>
public class CreateTodoRequest
{
    /// <summary>What has to be done.</summary>
    public string Title { get; set; } = "";
}
