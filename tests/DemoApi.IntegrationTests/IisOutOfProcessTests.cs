using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using Api.ToMcp.Runtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DemoApi.IntegrationTests;

/// <summary>
/// A tool's request in an app behind IIS out of process. The ASP.NET Core Module starts the app with the settings
/// <c>PORT</c>, <c>APPL_PATH</c> and <c>TOKEN</c>, which make <c>UseIISIntegration</c> refuse every request without the
/// pairing token, and forwards each request with that token; the test sends the MCP request as the module does.
/// </summary>
public sealed class IisOutOfProcessTests
{
    private const string PortSetting = "PORT";
    private const string AppPathSetting = "APPL_PATH";
    private const string PairingTokenSetting = "TOKEN";
    private const string PairingTokenHeader = "MS-ASPNETCORE-TOKEN";
    private const string WindowsAuthTokenHeader = "MS-ASPNETCORE-WINAUTHTOKEN";
    private const string PairingToken = "a3c1e2f0-pairing-token";
    private const string McpPath = "/mcp";
    private const string Route = "/api/iis-out-of-process";

    [Fact]
    public async Task AToolCall_IsServed_WithThePairingTokenOfTheMcpRequest()
    {
        await using var app = await StartAsync();
        await using var client = await ConnectAsync(app, new PairingTokenHandler(windowsUser: false));

        var result = await client.CallToolAsync("parity_get", new Dictionary<string, object?> { ["route"] = Route });

        Assert.Equal((false, IisOutOfProcessController.Served), (result.IsError == true, ((TextContentBlock)result.Content[0]).Text));
    }

    [Fact]
    public async Task AToolCalledOutsideAnMcpRequest_GetsBadRequest_AsItHasNoPairingTokenToCarry()
    {
        await using var app = await StartAsync();
        using var scope = app.Services.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IMcpHttpInvoker>().GetAsync(Route);

        Assert.Equal((true, "HTTP 400: Bad Request"), (result.IsError == true, ((TextContentBlock)result.Content[0]).Text));
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public async Task AToolsRequest_DoesNotCarryTheWindowsUserIisAuthenticated_SoAnActionThatRequiresItAnswers401()
    {
        await using var app = await StartAsync(mapEndpoints: false);
        string? mcpRequestUser = null;
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(McpPath) && context.User.Identity?.IsAuthenticated == true)
            {
                mcpRequestUser = context.User.Identity.Name;
            }

            return next(context);
        });
        app.UseAuthorization();
        app.MapControllers();
        app.MapMcpEndpoint("mcp", allowAnonymous: true);
        await app.StartAsync();
        await using var client = await ConnectAsync(app, new PairingTokenHandler(windowsUser: true));

        var result = await client.CallToolAsync("parity_get", new Dictionary<string, object?> { ["route"] = Route + "/windows-user" });

        using var currentUser = WindowsIdentity.GetCurrent();
        Assert.Equal(currentUser.Name, mcpRequestUser);
        Assert.Equal((true, "HTTP 401: Unauthorized"), (result.IsError == true, ((TextContentBlock)result.Content[0]).Text));
    }

    /// <summary>
    /// The app as the module starts it, on a port of its own; with <paramref name="mapEndpoints"/>, its endpoints mapped and
    /// started.
    /// </summary>
    private static async Task<WebApplication> StartAsync(bool mapEndpoints = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost
            .UseSetting(PortSetting, "0")
            .UseSetting(AppPathSetting, "/")
            .UseSetting(PairingTokenSetting, PairingToken)
            .UseIISIntegration();
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(IisOutOfProcessController).Assembly);
        builder.Services.AddMcpTools(typeof(ParityProbeTools).Assembly);

        var app = builder.Build();
        if (mapEndpoints)
        {
            app.MapControllers();
            app.MapMcpEndpoint("mcp", allowAnonymous: true);
            await app.StartAsync();
        }

        return app;
    }

    private static Task<McpClient> ConnectAsync(WebApplication app, PairingTokenHandler handler)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(app.Urls.Single() + McpPath), TransportMode = HttpTransportMode.StreamableHttp },
            new HttpClient(handler),
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return McpClient.CreateAsync(transport);
    }

    /// <summary>
    /// Adds what the module adds to each request it forwards: the pairing token, and with <c>windowsUser</c> a handle of
    /// the Windows user's token, which <c>UseIISIntegration</c> turns into the request's user and then closes.
    /// </summary>
    private sealed class PairingTokenHandler : DelegatingHandler
    {
        private readonly bool _windowsUser;

        public PairingTokenHandler(bool windowsUser)
            : base(new HttpClientHandler())
        {
            _windowsUser = windowsUser;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add(PairingTokenHeader, PairingToken);
            if (_windowsUser && OperatingSystem.IsWindows())
            {
                request.Headers.Add(WindowsAuthTokenHeader, TakeWindowsUserHandle());
            }

            return base.SendAsync(request, cancellationToken);
        }

        /// <summary>
        /// A token handle of the current user that the identity gives up, so the app alone closes it, as with the module's.
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static string TakeWindowsUserHandle()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var handle = identity.AccessToken.DangerousGetHandle();
            identity.AccessToken.SetHandleAsInvalid();

            return handle.ToInt32().ToString("X", CultureInfo.InvariantCulture);
        }
    }

    private sealed class WindowsOnlyFactAttribute : FactAttribute
    {
        public WindowsOnlyFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "Windows authentication behind IIS runs only on Windows.";
            }
        }
    }
}

[ApiController]
[Route("api/iis-out-of-process")]
public sealed class IisOutOfProcessController : ControllerBase
{
    public const string Served = "served";

    [HttpGet]
    public IActionResult Serve()
    {
        return Ok(Served);
    }

    [HttpGet("windows-user")]
    [Authorize(AuthenticationSchemes = IISDefaults.AuthenticationScheme)]
    public IActionResult GetWindowsUser()
    {
        return Ok(User.Identity?.Name);
    }
}
