using System.ComponentModel;
using Api.ToMcp.Abstractions;
using DemoApi.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DemoApi.Endpoints;

/// <summary>
/// Minimal API endpoints of a to-do list. The generator gives them tools as it does controller actions, named after this
/// class, which declares their handlers, and routed under the group <c>Program.cs</c> maps them in.
/// </summary>
public static class TodoEndpoints
{
    private static readonly Lock TodosLock = new();

    private static readonly List<Todo> Todos =
    [
        new Todo { Id = 1, Title = "Write the release notes", IsDone = false },
        new Todo { Id = 2, Title = "Water the plants", IsDone = true }
    ];

    public static RouteGroupBuilder MapTodoEndpoints(this RouteGroupBuilder api)
    {
        var todos = api.MapGroup("/todos").WithTags("Todos");

        todos.MapGet("/", GetAll);
        todos.MapGet("/{id:int}", GetById);
        todos.MapPost("/", ([Description("The item to add.")] CreateTodoRequest request) => Create(request))
            .WithName("CreateTodo")
            .WithDescription("Adds an item to the to-do list.");
        todos.MapDelete("/{id:int}", [McpIgnore] (int id) => Delete(id));

        return api;
    }

    /// <summary>
    /// Lists the to-do items, optionally only the done or the open ones.
    /// </summary>
    /// <param name="isDone">True for the done items only, false for the open ones only; all items when omitted.</param>
    private static Ok<List<Todo>> GetAll(bool? isDone = null)
    {
        lock (TodosLock)
        {
            return TypedResults.Ok(Todos.Where(x => isDone is null || x.IsDone == isDone).ToList());
        }
    }

    /// <summary>
    /// Gets a to-do item by its id.
    /// </summary>
    /// <param name="id">The id of the to-do item.</param>
    private static Results<Ok<Todo>, NotFound> GetById(int id)
    {
        lock (TodosLock)
        {
            var todo = Todos.FirstOrDefault(x => x.Id == id);
            if (todo is null)
            {
                return TypedResults.NotFound();
            }

            return TypedResults.Ok(todo);
        }
    }

    private static Created<Todo> Create(CreateTodoRequest request)
    {
        lock (TodosLock)
        {
            var todo = new Todo { Id = Todos.Select(x => x.Id).DefaultIfEmpty().Max() + 1, Title = request.Title };
            Todos.Add(todo);

            return TypedResults.Created($"/api/todos/{todo.Id}", todo);
        }
    }

    private static NoContent Delete(int id)
    {
        lock (TodosLock)
        {
            Todos.RemoveAll(x => x.Id == id);

            return TypedResults.NoContent();
        }
    }
}
