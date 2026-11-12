using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// A request as the server handed it to the app's request pipeline, before any middleware changed it.
/// </summary>
internal sealed class PipelineEntryFeature
{
    public PipelineEntryFeature(HttpContext context)
    {
        Path = context.Request.Path;
        AllowSynchronousIO = context.Features.Get<IHttpBodyControlFeature>()?.AllowSynchronousIO ?? false;
    }

    /// <summary>
    /// The path, of which <c>Map</c> or <c>UsePathBase</c> may have moved a part into the path base since.
    /// </summary>
    public PathString Path { get; }

    /// <summary>
    /// Whether the server allows synchronous body I/O, as Kestrel's and IIS's <c>AllowSynchronousIO</c> options set it.
    /// </summary>
    public bool AllowSynchronousIO { get; }
}
