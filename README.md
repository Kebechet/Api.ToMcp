[!["Buy Me A Coffee"](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://www.buymeacoffee.com/kebechet)

# Api.ToMcp

[![NuGet Version](https://img.shields.io/nuget/v/Api.ToMcp.Runtime)](https://www.nuget.org/packages/Api.ToMcp.Runtime/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Api.ToMcp.Runtime)](https://www.nuget.org/packages/Api.ToMcp.Runtime/)
![Last updated (main)](https://img.shields.io/github/last-commit/Kebechet/Api.ToMcp/main?label=last%20updated)
[![Twitter](https://img.shields.io/twitter/url/https/twitter.com/samuel_sidor.svg?style=social&label=Follow%20samuel_sidor)](https://x.com/samuel_sidor)

A C# source generator that automatically transforms your ASP.NET Core API endpoints into [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) tools.

## What is this?

Api.ToMcp analyzes your existing ASP.NET Core controllers at compile time and generates MCP-compatible tool classes. This allows AI assistants (like Claude) to interact with your REST API through the MCP protocol without writing any integration code manually.

## Features

- **Automatic Tool Generation** - Scans controllers and generates MCP tools at compile time
- **Attribute Control** - Use `[McpExpose]` and `[McpIgnore]` to fine-tune which endpoints are exposed
- **Flexible Selection** - Choose between allowlist (`SelectedOnly`) or blocklist (`AllExceptExcluded`) modes
- **Customizable Naming** - Configure tool naming format via `generator.json`
- **Loop Prevention** - Built-in middleware prevents infinite recursion when MCP tools call back to the API
- **Auth Forwarding** - Authentication headers from MCP requests are forwarded to API calls

## Quick Start

### 1. Install packages

```bash
dotnet add package Api.ToMcp.Abstractions
dotnet add package Api.ToMcp.Generator
dotnet add package Api.ToMcp.Runtime
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
- ASP.NET Core

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
