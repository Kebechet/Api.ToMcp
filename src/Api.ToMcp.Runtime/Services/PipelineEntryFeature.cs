using Microsoft.AspNetCore.Http;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// A request as the server handed it to the app's request pipeline, before any middleware changed it.
/// </summary>
internal sealed class PipelineEntryFeature
{
    public PipelineEntryFeature(HttpContext context)
    {
        Path = context.Request.Path;
    }

    /// <summary>
    /// The path, of which <c>Map</c> or <c>UsePathBase</c> may have moved a part into the path base since.
    /// </summary>
    public PathString Path { get; }
}
