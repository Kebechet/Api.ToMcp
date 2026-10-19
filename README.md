[!["Buy Me A Coffee"](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://www.buymeacoffee.com/kebechet)

# Api.ToMcp

[![NuGet Version](https://img.shields.io/nuget/v/Kebechet.Api.ToMcp)](https://www.nuget.org/packages/Kebechet.Api.ToMcp/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Kebechet.Api.ToMcp)](https://www.nuget.org/packages/Kebechet.Api.ToMcp/)
![Last updated (main)](https://img.shields.io/github/last-commit/Kebechet/Api.ToMcp/main?label=last%20updated)
[![Twitter](https://img.shields.io/twitter/url/https/twitter.com/samuel_sidor.svg?style=social&label=Follow%20samuel_sidor)](https://x.com/samuel_sidor)

A C# source generator that automatically transforms your ASP.NET Core API endpoints into [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) tools.

📝 Read the story behind the package: [Your ASP.NET API Already Speaks MCP - It Just Doesn't Know It Yet](https://dev.to/kebechet/your-aspnet-api-already-speaks-mcp-it-just-doesnt-know-it-yet-2870)

## What is this?

Api.ToMcp analyzes your existing ASP.NET Core controllers at compile time and generates MCP-compatible tool classes. This allows AI assistants (like Claude) to interact with your REST API through the MCP protocol without writing any integration code manually.

> **For APIs you own the source of.** Api.ToMcp is a source generator — it runs inside your API project's compilation, which is what lets it read your XML doc comments for tool descriptions, fail at build time instead of runtime, and route tool calls through your real middleware pipeline (auth, validation, filters). If you only have an OpenAPI spec — a third-party or legacy API you can't recompile — this package is not the right fit; use an OpenAPI-to-MCP proxy instead.

> **Controllers only.** The generator scans classes that inherit from `ControllerBase` (or are annotated with `[ApiController]`). Minimal API endpoints (`app.MapGet(...)`, `app.MapPost(...)`, etc.) are **not** discovered. See [Limitations](#limitations).

## Features

- **Automatic Tool Generation** - Scans controllers and generates MCP tools at compile time
- **Descriptions from XML docs** - Tool, argument and request-field descriptions come from the `<summary>` and `<param>` comments you already write
- **Typed results** - Each tool advertises its action's response type as an output schema and returns the response as structured content
- **Tool annotations** - Every tool states its read-only, destructive, idempotent and open-world hints, derived from its scope and HTTP method
- **Tool titles** - Every tool has a human-readable title for clients to show in place of its name, derived from the action name
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

### Structured Output

| Option | Description |
|--------|-------------|
| `structuredOutput` | `true` (the default) gives every tool whose action returns JSON of a known type an output schema and structured content; `false` turns both off. See [Output Schema and Structured Content](#output-schema-and-structured-content). |

## Attributes

### `[McpExpose]`

Forces an endpoint to be exposed as an MCP tool, regardless of configuration mode. Use `ToolName`, `Title` and `Description` to override the generated tool name, title and description.

```csharp
[McpExpose(ToolName = "GetProduct", Title = "Get a product", Description = "Retrieves a product by ID")]
public Task<Product> GetById(Guid id) { ... }
```

`Scope` overrides the scope derived from the HTTP method (see [Scope-Based Access Control](#scope-based-access-control)). Use it when the verb says something other than what the endpoint does - a search that takes its filter as a POST body and changes nothing would otherwise require `Write`:

```csharp
[McpExpose(Scope = McpScope.Read)]
[HttpPost("search")]
public Task<IEnumerable<Product>> Search([FromBody] ProductFilter filter) { ... }
```

On a controller, `Scope` applies to every action that names none of its own; an action's own `Scope` wins, and an action that sets `Scope = McpScope.None` explicitly follows its HTTP method again. Flags combine, and a combined scope requires all of them: `McpScope.Read | McpScope.Write` admits only a grant that has both.

#### Tool annotations

Every generated tool states all four [tool annotations](https://modelcontextprotocol.io/specification/2025-06-18/server/tools#tool-annotations) explicitly. A client reads a hint that is left out as its spec default - not read-only, destructive, open-world - and directories such as ChatGPT's refuse a tool that omits any of them. The generator derives them from the tool's scope and HTTP method:

| Hint | Derived value |
|---|---|
| `ReadOnly` | true when the scope is read only (a GET, or a POST with `Scope = McpScope.Read`). Never derived for PUT, PATCH or DELETE: a scope is a permission, and a loose one must not make a state-changing method look safe to run unconfirmed |
| `Destructive` | true when the tool is not read-only and is a PUT, PATCH or DELETE, or its scope includes `Delete`: the spec reserves false for purely additive updates, which of the write methods only POST makes |
| `Idempotent` | true when the tool is read-only, or for PUT and DELETE |
| `OpenWorld` | false: a generated tool calls your own API, a closed system |

Set a hint on `[McpExpose]` only where the derived value is wrong. An action's own value wins over its controller's, and a hint it does not set follows the controller, then the derivation. As with `Scope`, the nearest `[McpExpose]` replaces a base controller's as a whole: a derived controller's attribute hides every hint of its base's, including those it does not set.

```csharp
[McpExpose(Destructive = true)]           // wipes the user's progress, so a client should confirm first
[HttpPost("{id}/reset")]
public Task<ActionResult> ResetProgress(Guid id) { ... }

[McpExpose(OpenWorld = true)]             // posts to a URL the caller chooses
[HttpPost("webhooks/test")]
public Task<ActionResult> SendTestWebhook([FromBody] WebhookTest test) { ... }
```

#### Tool titles

Every generated tool has a `title`, sent as `annotations.title`. Clients show it in place of the tool name: in a connector directory listing, and when asking the user to approve a call. The Claude directory flags every tool without one.

The title is derived from the action name, split into words and read as a sentence: `LogWorkout` becomes "Log workout", `GetXRepMax` becomes "Get X rep max", and the `Async` suffix is dropped. Letters outside ASCII are kept (`GetMétéo` reads "Get météo"). With a custom `ToolName`, the title follows that name instead, because it is what the client lists: its part after the last underscore when that part is PascalCase (`Reports_GetSummary` reads "Get summary"), and the whole name otherwise (`get_report_summary` reads "Get report summary").

Set `Title` where the derived one reads badly. It is read from the action, or the base action it overrides, and never from a controller: one title cannot name every action of it.

```csharp
[McpExpose(Title = "Delete a city's forecasts")]
[HttpDelete("{city}")]
public Task<ActionResult> DeleteByCity(string city) { ... }
```

### `[McpIgnore]`

Forces an endpoint to be excluded from MCP exposure.

```csharp
[McpIgnore]
public Task<ActionResult> Delete(Guid id) { ... }
```

## Tool Descriptions

Good descriptions matter for MCP — they're what the AI client reads to decide which tool to call and how to fill its arguments. Api.ToMcp pulls these from your existing XML doc comments automatically:

- `<summary>` becomes the tool's description.
- `<param name="...">` becomes that parameter's description.

```csharp
/// <summary>Gets a product by its unique identifier.</summary>
/// <param name="id">The unique identifier of the product to retrieve.</param>
[HttpGet("{id:guid}")]
public Task<ActionResult<Product>> GetById(Guid id) { ... }
```

Precedence: `[McpExpose(ToolName/Description)]` overrides → XML doc → a generic fallback (`Invokes Controller.Action` / `Parameter: name`). Titles are derived from the action name instead; see [Tool titles](#tool-titles).

> **Requires XML documentation generation.** The source generator can only read doc comments when the compiler produces them. Enable it in the project that hosts your controllers, otherwise descriptions fall back to the generic values:
>
> ```xml
> <PropertyGroup>
>   <GenerateDocumentationFile>true</GenerateDocumentationFile>
>   <NoWarn>$(NoWarn);CS1591</NoWarn> <!-- optional: silence "missing XML comment" warnings -->
> </PropertyGroup>
> ```

### Request fields: DTO properties, nested objects, array items

The two rules above run at compile time. The fields *inside* an argument's type - a request DTO's properties, nested objects, array items - get their JSON schema at runtime from the MCP SDK, which reflects over the type and reads only `[Description]`. Api.ToMcp fills that gap: every such property without a `[Description]` gets its XML `<summary>` as its schema `description`, read from the `<AssemblyName>.xml` file next to the assembly that declares the property.

```csharp
public class CreateWeatherForecastRequest
{
    /// <summary>Name of the city the forecast is for.</summary>
    public string City { get; set; } = "";

    /// <summary>Temperature in degrees Celsius.</summary>
    [Description("Air temperature in degrees Celsius.")] // wins over the <summary>
    public int TemperatureC { get; set; }
}
```

Precedence: `[Description]` on the property (or on the record constructor parameter it comes from) → the property's XML `<summary>` → a `[Description]` on the property's type → no description. A property inherited from a base class takes the base class's `<summary>`. `<see cref="..."/>` renders as the member's name, `<c>`, `<paramref>` and `<typeparamref>` as their text or name, `<para>` as a separator; `<inheritdoc/>` is not resolved, so a property documented only that way stays undescribed. Framework types (`System.*`, `Microsoft.*`) and the MCP SDK's own are never looked up.

This is on by default; `options.ArePropertyDescriptionsFromXmlDocs = false` in `AddMcpTools` turns it off.

> **Every project whose types appear in tool arguments or responses needs `<GenerateDocumentationFile>true</GenerateDocumentationFile>`** - not only the one with the controllers; a separate contracts project included. The compiler writes the docs to `<AssemblyName>.xml` next to the assembly, and that file has to be there at runtime:
>
> - `dotnet build` and `dotnet publish` copy the app's own `.xml` and the `.xml` of every `ProjectReference` that generates one into the output and publish folders. Verified with SDK 10.0.302 for a `net10.0` app (the demo) and a `net8.0` app; it is MSBuild's standard handling of a reference's related files (`AllowedReferenceRelatedFileExtensions` includes `.xml`), so nothing has to be added to the project.
> - The `.xml` shipped inside a NuGet **package** is *not* copied to the output by default. For DTOs that come from a package, set `<CopyDocumentationFilesFromPackages>true</CopyDocumentationFilesFromPackages>` in the app's project; the SDK then copies every package's `.xml` into both the build and the publish output (verified with SDK 10.0.302).
>
> When an assembly whose types appear in a tool schema has no `.xml` next to it, the host logs **one warning per assembly at startup**, naming the assembly and the path it looked at, so a missing file shows up in the log instead of in a bare schema. A file that exists but cannot be read (cut short, malformed, locked) is logged the same way with the reason, and never stops the host: its properties simply stay undescribed.

**Responses are described the same way.** A generated tool's output schema (see [Output Schema and Structured Content](#output-schema-and-structured-content)) gets the XML descriptions of the response DTO's properties by these rules.

A hand-written `[McpServerTool]` that returns your own type is described too: the SDK builds a schema for its return type as well, and the same XML descriptions are applied to it. Clients see that schema only when the tool sets `UseStructuredContent = true`, but the return type's assembly counts either way, so without its `.xml` it is named in the startup warning.

## Output Schema and Structured Content

A generated tool advertises its action's success response type as the tool's `outputSchema` in `tools/list`, and returns a successful call's body as `structuredContent` next to the text content. Clients and models then know what a tool returns without guessing from a JSON blob. A failed call is unchanged: `isError: true` with the status and body as text, and no structured content (see [Error handling](#error-handling)).

### Which type

The type is decided the way ApiExplorer, and so Swagger, decides it:

- The return type without `Task<T>` / `ValueTask<T>` and `ActionResult<T>`: `ActionResult<Product>`, `Task<Product>` and `Product` all mean `Product`.
- A 2xx `[ProducesResponseType]` replaces the return type, which is how an `IActionResult` action names one: `[ProducesResponseType(typeof(Product), 200)]` or `[ProducesResponseType<Product>(200)]`. One without a type means the return type for 200 and 201, and no body for any other status, such as 204. Error statuses do not count.
- A 2xx `[ProducesResponseType]` on the controller applies to every action and adds to its return type, or stands in for a return type that names no body (`IActionResult`, `ActionResult`, `IResult`); the action's own declaration of the same status replaces it. Both are inherited, from base controllers and from the action an override replaces.
- When the success responses declare different types, the tool gets no schema.

A tool gets no schema, and returns text as before, when the success body is not reliably the JSON of a known type: `void` / `Task`, an untyped `IActionResult` or `ActionResult`, `IResult`, file results and streams, `string` (MVC writes it as `text/plain`), `object`, `JsonElement` / `JsonNode` / `JsonDocument`, `IAsyncEnumerable<T>`, a `[Produces]` or `[ProducesResponseType]` content type other than JSON, even next to JSON (`[Produces("application/json", "text/csv")]`), or a type the generated code cannot reference, such as a private nested class.

A content type counts as JSON when its subtype is `json` or ends in `+json` (`application/json`, `text/json`, `application/problem+json`), so `application/x-ndjson`, whose body is many JSON values, does not.

### The schema matches what your API writes

The schema is built when the host starts, from the `System.Text.Json` options MVC writes responses with, `AddJsonOptions` included: property naming, `JsonStringEnumConverter` and the other settings you gave MVC. Structured content is the response body itself, so a client that validates it against the schema accepts it. A property MVC can leave out of the body, through `DefaultIgnoreCondition` or its own `[JsonIgnore(Condition = ...)]`, is never listed as `required`, and a `DateTime` or `TimeOnly` claims no `date-time` or `time` format: those require an offset, which MVC does not write for a `DateTime` of unspecified kind, as read from a database, nor for a `TimeOnly`. Response DTO properties get their XML `<summary>` as descriptions, by the [request field rules](#request-fields-dto-properties-nested-objects-array-items). With `ReferenceHandler.Preserve`, whose `$id`/`$ref`/`$values` metadata no JSON Schema describes, every output schema accepts any value and a warning is logged at startup.

### Successes without a body

MVC answers an action that returns `null` with `204 No Content`, and `NoContent()`, `Ok()` or `Accepted()` send no body either. Such a call returns `"structuredContent": null`, and the schema allows it, `{"anyOf": [<the type's schema>, {"type": "null"}]}`, whenever the action can send one. The generator reads each action to decide. The schema is the plain type only when it is proven that every success has a body: an action returning the value itself does not declare it nullable (`Product?`, `Task<Product?>`), and every value the action's own `return` statements produce is

- a not-null value by the compiler's flow analysis, such as `return product;` after a null check or `new Product { ... }`;
- an `ObjectResult` helper given such a value (`Ok(product)`, `CreatedAtAction(..., product)`), whose status, when the action sets it (`StatusCode(201, product)`), is a constant other than 204 or 205, which never carry a body; or
- an error (`NotFound()`, `BadRequest(...)`, `Problem()`, `StatusCode(404)`, ...).

Anything else makes the schema allow null: `NoContent()`, `Ok()`, `return null`, a maybe-null value, a task returned from another method, a helper method, a redirect, an action without nullable annotations, and a `[ProducesResponseType(204)]` declared next to the typed success. So does a value from code compiled without nullable annotations, such as an older library's `Find()`: the compiler treats it as not-null without knowing, so it proves nothing, even when held in a local and checked for null first. Allowing null when the action never sends it only costs precision; the other way round, the call would fail.

```csharp
[HttpGet("{id:guid}")]
public ActionResult<Product> GetById(Guid id)   // output schema: Product
{
    var product = Find(id);
    if (product is null)
        return NotFound();
    return product;
}

[HttpGet("cheapest")]
public ActionResult<Product> GetCheapest(string category)   // output schema: Product or null
{
    var product = FindCheapest(category);
    if (product is null)
        return NoContent();
    return product;
}
```

### Clients on an older protocol

Before protocol version `2026-07-28`, an output schema had to describe an object and structured content had to be one. For a client that negotiates an older version, the MCP SDK sends any other schema (an array, a number, or one that also allows null) wrapped as `{"type": "object", "properties": {"result": <schema>}, "required": ["result"]}`, and the content as `{"result": <value>}`. An object schema is sent as it is.

### Turning it off

Set `"structuredOutput": false` in `generator.json`, and every generated tool goes back to text-only results. Do this when the API serializes with Newtonsoft.Json (`AddNewtonsoftJson`): the schema follows `System.Text.Json` and cannot see Newtonsoft's attributes and settings.

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

### Error handling

The invoker methods return a `CallToolResult`. When the invoked API returns a non-success (non-2xx) status, that result has **`isError: true`** with the status code and body as its content — so the AI client knows the call failed (and can react — e.g. re-authenticate on a `401`) rather than treating an error payload as a valid result. This is done by conditioning on the status, not by throwing, since an HTTP status is an expected outcome. Genuine transport failures (network errors, timeouts) still surface as exceptions, which the MCP server likewise reports as `isError: true`.

## Limitations

- **Controllers only.** Minimal API endpoints (`app.MapGet`, `app.MapPost`, …) are not scanned; only `ControllerBase`-derived / `[ApiController]` classes are. Support is tracked in [#11](https://github.com/Kebechet/Api.ToMcp/issues/11).
- **One HTTP verb per action.** If an action carries multiple verb attributes (e.g. `[HttpGet]` and `[HttpHead]`), only the first is used.
- **No `[FromForm]` / file uploads.** Form-bound parameters and `IFormFile` are not mapped.
- **API versioning is partial.** URL-segment versioning (`[Route("api/v{version:apiVersion}/[controller]")]`) is supported — the concrete version from `[ApiVersion]` is baked into the generated route (defaulting to `1.0` if the attribute is absent), so each tool targets a fixed version. Query-string (`?api-version=`) and header/media-type versioning are **not** sent by the generated tools, so those rely on the API's default version.

## Troubleshooting

**No tools are generated.**
- Confirm `Mcp/generator.json` is registered as an `AdditionalFiles` item in your `.csproj`.
- In `SelectedOnly` mode, only endpoints listed in `include` are exposed — check the names match `Controller.Action`.
- Ensure the endpoint lives on a `ControllerBase`-derived class (or one with `[ApiController]`); minimal APIs are not supported.

**`InvalidOperationException: Unable to determine base URL`.**
- Set `McpTools:BaseUrl` in configuration, or ensure the server has finished starting before the first tool call. See [Base URL Resolution](#base-url-resolution).

**Self-calls fail or redirect to HTTPS behind a reverse proxy.**
- The invoker derives the correct `https://` base from the incoming request when the server only listens on HTTP, but the most reliable fix is to pin `McpTools:BaseUrl` explicitly.

**`MissingMethodException` on `AddMcpTools` at startup** (e.g. `Method not found: '...McpServerServiceCollectionExtensions.AddMcpServer(...)'`).
- Your app references a different version of `ModelContextProtocol.*` than the one this package targets (`2.2.0`). Remove your explicit reference so the correct version flows transitively, or pin it to the exact same version. See [Requirements](#requirements).

**Startup warning `Assembly '...' has types in MCP tool schemas but no XML documentation file`.**
- A type used in a tool argument or response lives in a project built without `<GenerateDocumentationFile>true</GenerateDocumentationFile>`, comes from a NuGet package while the app lacks `<CopyDocumentationFilesFromPackages>true</CopyDocumentationFilesFromPackages>`, or the `.xml` was not deployed next to the assembly. Enable the matching property and check the publish output; see [Request fields](#request-fields-dto-properties-nested-objects-array-items). Set `options.ArePropertyDescriptionsFromXmlDocs = false` to turn the feature (and the warning) off.
- The variant `... could not be read (...)` means the file is there but damaged; rebuild and redeploy it.

**Tool calls return `401`/`403`.**
- Auth forwarding only works when the MCP request itself carries an `Authorization` header — it is forwarded to the API call verbatim.
- When scope validation is enabled, the request must be authenticated and the mapped scope must cover the tool's required scope (`Read`/`Write`/`Delete`).

## How It Works

1. **Compile Time**: The source generator scans your controllers for HTTP actions
2. **Code Generation**: For each selected endpoint, a tool class is generated with `[McpServerToolType]` attribute
3. **Startup**: `AddMcpTools` registers every `[McpServerToolType]` class of the assembly (generated and hand-written) and builds the tools and their argument schemas when the host starts, so a tool that cannot be created fails at startup rather than at the first request
4. **Runtime**: When an MCP client calls a tool, it invokes your API via HTTP internally
5. **Loop Prevention**: The `X-MCP-Internal-Call` header prevents MCP endpoints from being called recursively

### Generated Code Example

For `ProductsController.GetById(Guid id)` in the demo, the generator creates:

```csharp
[McpServerToolType]
public static class ProductsController_GetByIdTool
{
    [McpServerTool(Name = "Products_GetById", Title = "Get by id", UseStructuredContent = true, OutputSchemaType = typeof(global::DemoApi.Models.Product), ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [global::Api.ToMcp.Runtime.McpGeneratedTool(IsOutputNullable = false)]
    [Description("Gets a product by its unique identifier.")]
    public static async Task<object> InvokeAsync(
        IMcpHttpInvoker invoker,
        [Description("The unique identifier of the product to retrieve.")] System.Guid id,
        System.Threading.CancellationToken cancellationToken = default
    )
    {
        var routeid = System.Uri.EscapeDataString(global::Api.ToMcp.Runtime.Services.McpRequestValueFormatter.Format(id));
        var route = $"/api/products/{routeid}";
        await invoker.BeforeInvokeAsync(McpScope.Read, cancellationToken);
        return global::Api.ToMcp.Runtime.Services.McpStructuredOutput.FromResult(await invoker.GetAsync(route, cancellationToken));
    }
}
```

On success `McpStructuredOutput.FromResult` returns the parsed body, which the SDK sends as structured content; on an error it returns the invoker's `CallToolResult` unchanged. `[McpGeneratedTool]` is for `AddMcpTools`, which builds the output schema from it. A tool without an output schema returns the invoker's `CallToolResult` directly, as `Task<CallToolResult>`.

## Requirements

- .NET 8.0 or later
- ASP.NET Core with controller-based endpoints (see [Limitations](#limitations))
- `ModelContextProtocol.AspNetCore` **`2.2.0`** — this is a transitive dependency of the package. **Do not add a different version of any `ModelContextProtocol.*` package to your app.** The MCP SDK makes breaking API changes between versions; a version mismatch causes a `MissingMethodException` at startup (see [Troubleshooting](#troubleshooting)). Either omit the reference entirely and let it flow transitively, or pin the exact same version.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
