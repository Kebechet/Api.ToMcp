using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Builder;
using Microsoft.AspNetCore.Http.Features;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// Hands <see cref="InProcessApiPipeline"/> the request pipeline the host builds for the server. The host creates the
/// app's builder here, lets every startup filter and the app add their middleware to it, and starts the server with what
/// it builds, so the pipeline captured from it is the one every served request passes.
/// </summary>
/// <remarks>
/// The pipeline is taken here rather than from the server, which would hand over the hosting layer around it as well,
/// because only replacing the registered <c>IServer</c> would reach it: IIS in-process hosting refuses to start with any
/// server but its own type, and a test that reads its <c>TestServer</c> back from the services would get the wrapper.
/// </remarks>
internal sealed class PipelineCapturingApplicationBuilderFactory : IApplicationBuilderFactory
{
    private readonly IApplicationBuilderFactory _inner;
    private readonly InProcessApiPipeline _pipeline;

    public PipelineCapturingApplicationBuilderFactory(IApplicationBuilderFactory inner, InProcessApiPipeline pipeline)
    {
        _inner = inner;
        _pipeline = pipeline;
    }

    public IApplicationBuilder CreateBuilder(IFeatureCollection serverFeatures)
    {
        var builder = _inner.CreateBuilder(serverFeatures);

        // As the first component, it is handed everything added after it as its next, which is the whole pipeline, and
        // passes requests straight to it.
        builder.Use(next =>
        {
            _pipeline.Capture(next);
            return next;
        });

        return builder;
    }
}
