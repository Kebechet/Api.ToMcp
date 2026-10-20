using Microsoft.AspNetCore.Mvc;

namespace DemoApi.Controllers;

/// <summary>
/// The endpoints of a controller that serves a fixed list, written once for all of them. MVC serves them under each
/// controller that derives from this class, at that controller's route, and never under this abstract class; the
/// generator does the same, so each such controller gets tools of its own.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public abstract class ListControllerBase<T> : ControllerBase
{
    protected abstract IReadOnlyList<T> Items { get; }

    /// <summary>
    /// Lists every item.
    /// </summary>
    [HttpGet]
    public ActionResult<List<T>> GetAll()
    {
        return Items.ToList();
    }
}
