[!["Buy Me A Coffee"](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://www.buymeacoffee.com/kebechet)

# Api.ToMcp

[![NuGet Version](https://img.shields.io/nuget/v/Kebechet.Api.ToMcp)](https://www.nuget.org/packages/Kebechet.Api.ToMcp/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Kebechet.Api.ToMcp)](https://www.nuget.org/packages/Kebechet.Api.ToMcp/)
![Last updated (main)](https://img.shields.io/github/last-commit/Kebechet/Api.ToMcp/main?label=last%20updated)
[![Twitter](https://img.shields.io/twitter/url/https/twitter.com/samuel_sidor.svg?style=social&label=Follow%20samuel_sidor)](https://x.com/samuel_sidor)

A C# source generator that automatically transforms your ASP.NET Core API endpoints into [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) tools.

## What is this?

Api.ToMcp analyzes your existing ASP.NET Core controllers at compile time and generates MCP-compatible tool classes. This allows AI assistants (like Claude) to interact with your REST API through the MCP protocol without writing any integration code manually.

> **Controllers only.** The generator scans classes that inherit from `ControllerBase` (or are annotated with `[ApiController]`). Minimal API endpoints (`app.MapGet(...)`, `app.MapPost(...)`, etc.) are **not** discovered. See [Limitations](#limitations).

## Features

- **Automatic Tool Generation** - Scans controllers and generates MCP tools at compile time
- **Attribute Control** - Use `[McpExpose]` and `[McpIgnore]` to fine-tune which endpoints are exposed
- **Flexible Selection** - Choose between allowlist (`SelectedOnly`) or blocklist (`AllExceptExcluded`) modes
- **Customizable Naming** - Configure tool naming format via `generator.json`
- **Loop Prevention** - Built-in middleware prevents infinite recursion when MCP tools call back to the API
- **Auth Forwarding** - Authentication headers from MCP requests are forwarded to API calls

## Quick Start

### 1. Install package

```bash
dotnet add package Kebechet.Api.ToMcp
```

### 2. Add generator configuration

Create `Mcp/generator.json` in your project:

```json
{
  "schemaVersion": 1,
  "mode": "SelectedOnly",
  "include": [
    "ProductsController.GetAll",
    "ProductsController.GetById"
  ],
  "exclude": [],
  "naming": {
    "toolNameFormat": "{Controller}_{Action}",
    "removeControllerSuffix": true
  }
}
```

Add it to your `.csproj`:

```xml
<ItemGroup>
  <AdditionalFiles Include="Mcp\generator.json" />
</ItemGroup>
```

### 3. Wire up in Program.cs

```csharp
using System.Reflection;
using Api.ToMcp.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddMcpTools(Assembly.GetExecutingAssembly());

var app = builder.Build();

app.UseMcpLoopPrevention();
app.MapControllers();
app.MapMcpEndpoint("mcp");

app.Run();
```

### 4. Use attributes on your controllers

```csharp
[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    [HttpGet]
    public Task<IEnumerable<Product>> GetAll([FromQuery] string? category = null)
    {
        // ...
    }

    [HttpGet("{id:guid}")]
    public Task<ActionResult<Product>> GetById(Guid id)
    {
        // ...
    }

    [HttpDelete("{id:guid}")]
    [McpIgnore]  // Exclude dangerous operations
    public Task<ActionResult> Delete(Guid id)
    {
        // ...
    }
}
```

## Configuration

### Selection Modes

| Mode | Description |
|------|-------------|
| `SelectedOnly` | Only endpoints in the `include` list are exposed (allowlist) |
| `AllExceptExcluded` | All endpoints except those in `exclude` are exposed (blocklist) |

### Naming Options

| Option | Description |
|--------|-------------|
| `toolNameFormat` | Format string for tool names. Supports `{Controller}` and `{Action}` placeholders |
| `removeControllerSuffix` | When `true`, strips "Controller" from the controller name |

### Include/Exclude Patterns

- `"ProductsController"` - Include/exclude entire controller
- `"ProductsController.GetById"` - Include/exclude specific action

## Attributes

### `[McpExpose]`

Forces an endpoint to be exposed as an MCP tool, regardless of configuration mode.

```csharp
[McpExpose(Name = "GetProduct", Description = "Retrieves a product by ID")]
public Task<Product> GetById(Guid id) { ... }
```

### `[McpIgnore]`

Forces an endpoint to be excluded from MCP exposure.

```csharp
[McpIgnore]
public Task<ActionResult> Delete(Guid id) { ... }
```

## Scope-Based Access Control

Api.ToMcp supports optional scope-based access control for MCP tools. Scopes are mapped from HTTP methods:

| Scope  | HTTP Methods       |
|--------|-------------------|
| Read   | GET, HEAD, OPTIONS |
| Write  | POST, PUT, PATCH   |
| Delete | DELETE             |

### Default Behavior

By default, **no scope checking is performed** - all generated tools are accessible.

### Enabling Scope Validation

To enable scope validation, configure a claim-to-scope mapper:

```csharp
using Api.ToMcp.Abstractions.Scopes;

builder.Services.AddMcpTools(Assembly.GetExecutingAssembly(), options =>
{
    options.ClaimName = "permissions"; // JWT claim name
    options.ClaimToScopeMapper = claimValue =>
    {
        var scope = McpScope.None;
        if (claimValue.Contains("read")) scope |= McpScope.Read;
        if (claimValue.Contains("write")) scope |= McpScope.Write;
        if (claimValue.Contains("delete")) scope |= McpScope.Delete;
        return scope;
    };
});
```

### How It Works

1. MCP request arrives with JWT containing claims
2. The configured `ClaimName` is read from the user's claims
3. `ClaimToScopeMapper` converts the claim value to `McpScope`
4. If the tool's required scope (based on HTTP method) isn't granted, an error is returned

### Example JWT Claim

```json
{ "permissions": "mcp:read mcp:write" }
```

This grants `Read` and `Write` scopes but not `Delete`.

## Base URL Resolution

Generated tools invoke your API over HTTP internally, so the runtime needs to know the base URL to call. This is handled by `ISelfBaseUrlProvider`. The default implementation (`DefaultBaseUrlProvider`, registered automatically by `AddMcpTools`) resolves the URL in this order:

1. The `McpTools:BaseUrl` configuration value, if set (e.g. in `appsettings.json`).
2. The first `https://` address the server is bound to.
3. The first address of any scheme, with wildcard hosts (`+`, `*`, `0.0.0.0`, `[::]`) normalized to `localhost`.

The result is cached after the first resolution. If none of the above yields an address, `GetBaseUrl()` throws with guidance to set `McpTools:BaseUrl`.

**Pinning the base URL** (recommended behind a reverse proxy or in containers):

```json
{
  "McpTools": {
    "BaseUrl": "https://localhost:5001"
  }
}
```

**Providing your own resolver** — register a replacement after `AddMcpTools` (the last registration wins):

```csharp
builder.Services.AddMcpTools(Assembly.GetExecutingAssembly());
builder.Services.AddSingleton<ISelfBaseUrlProvider, MyBaseUrlProvider>();
```

```csharp
public sealed class MyBaseUrlProvider : ISelfBaseUrlProvider
{
    public string GetBaseUrl() => "https://api.internal.example.com";
}
```

## Custom HTTP Invocation

Every generated tool depends on `IMcpHttpInvoker`, the contract that actually performs the API call. The default implementation (`McpHttpInvoker`) builds the URL from `ISelfBaseUrlProvider`, forwards the incoming `Authorization` header, adds the `X-MCP-Internal-Call` loop-prevention header, and runs scope validation via `BeforeInvokeAsync`.

```csharp
public interface IMcpHttpInvoker
{
    Task<string> GetAsync(string route, CancellationToken ct = default);
    Task<string> PostAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<string> PutAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<string> PatchAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<string> DeleteAsync(string route, CancellationToken ct = default);

    // Called by each generated tool before the HTTP call.
    // Validates scope only when a scope mapper is configured (see Scope-Based Access Control).
    Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default);
}
```

You rarely need to implement this yourself, but you can substitute it — for example to route tool calls in-process instead of over HTTP, or to stub calls in tests — by registering your own implementation after `AddMcpTools`.

## Limitations

- **Controllers only.** Minimal API endpoints (`app.MapGet`, `app.MapPost`, …) are not scanned; only `ControllerBase`-derived / `[ApiController]` classes are.
- **One HTTP verb per action.** If an action carries multiple verb attributes (e.g. `[HttpGet]` and `[HttpHead]`), only the first is used.
- **No `[FromForm]` / file uploads.** Form-bound parameters and `IFormFile` are not mapped.
- **Complex `[FromQuery]` objects are not flattened** into individual query parameters (only route- and body-bound complex types have their properties expanded).
- **Tools return the raw response body as a string.** No typed output schema is emitted from the action's return type.

## Troubleshooting

**No tools are generated.**
- Confirm `Mcp/generator.json` is registered as an `AdditionalFiles` item in your `.csproj`.
- In `SelectedOnly` mode, only endpoints listed in `include` are exposed — check the names match `Controller.Action`.
- Ensure the endpoint lives on a `ControllerBase`-derived class (or one with `[ApiController]`); minimal APIs are not supported.

**`InvalidOperationException: Unable to determine base URL`.**
- Set `McpTools:BaseUrl` in configuration, or ensure the server has finished starting before the first tool call. See [Base URL Resolution](#base-url-resolution).

**Self-calls fail or redirect to HTTPS behind a reverse proxy.**
- The invoker derives the correct `https://` base from the incoming request when the server only listens on HTTP, but the most reliable fix is to pin `McpTools:BaseUrl` explicitly.

**Tool calls return `401`/`403`.**
- Auth forwarding only works when the MCP request itself carries an `Authorization` header — it is forwarded to the API call verbatim.
- When scope validation is enabled, the request must be authenticated and the mapped scope must cover the tool's required scope (`Read`/`Write`/`Delete`).

## How It Works

1. **Compile Time**: The source generator scans your controllers for HTTP actions
2. **Code Generation**: For each selected endpoint, a tool class is generated with `[McpServerToolType]` attribute
3. **Runtime**: When an MCP client calls a tool, it invokes your API via HTTP internally
4. **Loop Prevention**: The `X-MCP-Internal-Call` header prevents MCP endpoints from being called recursively

### Generated Code Example

For `ProductsController.GetById(Guid id)`, the generator creates:

```csharp
[McpServerToolType]
public static class ProductsController_GetByIdTool
{
    [McpServerTool(Name = "Products_GetById")]
    [Description("Invokes ProductsController.GetById")]
    public static async Task<string> InvokeAsync(
        IMcpHttpInvoker invoker,
        [Description("Parameter: id")] Guid id)
    {
        var route = $"/api/products/{Uri.EscapeDataString(id.ToString())}";
        return await invoker.GetAsync(route);
    }
}
```

## Requirements

- .NET 8.0 or later
- ASP.NET Core with controller-based endpoints (see [Limitations](#limitations))

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
