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

> **Controllers only.** The generator scans classes that inherit from `ControllerBase` (or are annotated with `[ApiController]`), and finds their actions the way MVC does; see [Controllers and Actions](#controllers-and-actions). Minimal API endpoints (`app.MapGet(...)`, `app.MapPost(...)`, etc.) are **not** discovered. See [Limitations](#limitations).

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
- **Request Correlation** - A tool call and the API request it makes share one W3C trace, so their log entries and OpenTelemetry spans can be matched by trace ID

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

If the project has no config when it builds, the build creates a starter `Mcp/generator.json` and warns that it did (`MCP100`). That starter config is in `SelectedOnly` mode with an empty `include`, so no tools are generated until you list controllers or actions in it, and every compile warns with `MCP006` until then. See [Config file location](#config-file-location).

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

### 5. See your tools

Run the app and point MCP Inspector at its MCP endpoint to see the generated tools and try them. See [Debugging & Inspecting Your Tools](#debugging--inspecting-your-tools).

## Controllers and Actions

The generator finds controllers and actions the way MVC does, so every tool calls an endpoint your app serves:

- **Controllers.** A public, top-level class that inherits from `ControllerBase` or carries `[ApiController]`, on itself or a base class, whatever its name. Abstract, generic, static, nested and non-public classes are not controllers, and neither is one marked `[NonController]`, on itself or a base class.
- **Actions.** A public instance method of a controller that MVC serves for GET, POST, PUT, PATCH or DELETE, the HTTP methods a tool sends: with `[HttpGet]`, `[HttpPost]`, `[HttpPut]`, `[HttpPatch]`, `[HttpDelete]` or `[AcceptVerbs]`, or an attribute of your own, as below. Static and generic methods are not actions, and neither is one marked `[NonAction]`, on itself or the base action it overrides, an override of a method of `object` such as `ToString()`, or the implementation of `IDisposable.Dispose`.
- **Routing attributes.** MVC reads routes and HTTP methods through the interfaces `IRouteTemplateProvider` and `IActionHttpMethodProvider`, so the generator takes an attribute derived from `[Route]` or an `[Http*]` attribute, or one that implements those interfaces itself, as MVC does:

  ```csharp
  public sealed class ApiRouteAttribute : RouteAttribute
  {
      public ApiRouteAttribute() : base("api/[controller]") { }
  }

  [ApiRoute]
  public class ProductsController : ControllerBase { ... }   // routed at api/products
  ```

  MVC creates the attribute and asks it for its template; the generator follows the attribute's constructors down to MVC's, or reads its `Template` property, as long as they pass on constants, their own parameters, and concatenations or interpolations of those. The code of an attribute declared in a referenced assembly, such as a shared package, is not in its metadata, so an action routed by one gets no tool and build warning [`MCP009`](#troubleshooting).
- **Inherited actions.** A controller has the actions it inherits from its base classes as well as its own. Each concrete controller gets its own tool for an inherited action, named after that controller, with `[controller]` in the route filled with its name; an abstract base class gets none. An overridden action gets one tool, from the most derived override.
- **Inherited attributes.** What MVC inherits is read from the base classes and from the action an override replaces: `[Route]`, `[ApiController]`, `[NonController]`, `[NonAction]`, the `[Http*]` attributes, `[ActionName]`, `[Produces]` and `[ProducesResponseType]`, as well as [`[McpExpose]`](#mcpexpose) and [`[McpIgnore]`](#mcpignore). A parameter of an override is sent as MVC binds it: with the binding attributes of the parameter at its position in the action it overrides, `[FromQuery(Name = "...")]` included, unless it declares one of the same class itself, which replaces the base's whole, so a bare `[FromQuery]` drops the base's `Name`. Routes follow MVC's rule: `[Route]` and the `[Http*]` attributes count together, taken from the class or action that declares any of them. A controller takes them from the nearest class that does. An override that declares none is routed as its action was first declared, skipping any override in between. An override with only a `[Route]` gets no tool, as MVC serves it for every HTTP method, as it does an action that overrides nothing.

An action inherited from a generic base class takes the controller's type arguments, so its parameters and output schema are typed:

```csharp
[ApiController]
[Route("api/[controller]")]
public abstract class CrudController<T> : ControllerBase
{
    [HttpGet]
    public ActionResult<List<T>> GetAll() => ...;
}

public class ProductsController : CrudController<Product> { }   // Products_GetAll: GET /api/products, a List<Product>
public class OrdersController : CrudController<Order> { }       // Orders_GetAll: GET /api/orders, a List<Order>
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
| `toolNameFormat` | Format string for tool names. Supports `{Controller}` and `{Action}` placeholders. `{Controller}` is the controller that serves the action, also for an action it inherits |
| `removeControllerSuffix` | When `true`, strips "Controller" from the controller name |

### Include/Exclude Patterns

- `"ProductsController"` - Include/exclude entire controller
- `"ProductsController.GetById"` - Include/exclude specific action

Entries are case-sensitive, and the `Controller` suffix may be left out (`"Products.GetById"`). A config that selects none of the project's actions makes the build warn with `MCP006`, and one the generator cannot use as written, such as malformed JSON or an unknown `mode`, with `MCP001`; see [Troubleshooting](#troubleshooting). The file may contain comments, but not trailing commas.

An inherited action is named by the controller that serves it, as MVC names it: `ProductsController.GetAll` for the `GetAll` that `ProductsController` inherits from `CrudController<T>`. The base class's name selects nothing, since it serves no endpoint of its own.

An entry that names no controller or action of the project, such as a misspelled name or a base class's, makes the build warn with `MCP008` at the entry, in either list whatever the mode. An entry naming a controller or action that `[McpIgnore]` or an `/mcp` route keeps from MCP names something, so it gets no `MCP008`: the attribute or route decides, as always. See [Troubleshooting](#troubleshooting).

### Config file location

| MSBuild property | Default | Description |
|------------------|---------|-------------|
| `ApiToMcpConfigPath` | `Mcp\generator.json` in the project directory | The config the build adds to `AdditionalFiles`, and creates when it does not exist. |
| `ApiToMcpAutoCreateConfig` | `true` | `false` stops the build from creating a starter config when there is none. |

On Linux and other case-sensitive file systems the path is matched exactly, so a config in `mcp/` is not at the default `Mcp/generator.json`. When the build finds no config at `ApiToMcpConfigPath` but one whose folder or file name differs from it only in letter case, it gives the generator that file and warns with `MCP101`: set `ApiToMcpConfigPath` to it, or rename it to match. It does so whether or not `ApiToMcpAutoCreateConfig` is on, writes no starter config beside it, and puts it in place of an `AdditionalFiles` item that names the missing configured path.

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

On a controller, it exposes all of its actions, those it inherits included. It is inherited, as reflection reads it: on a base class, it exposes every controller derived from it, and on an action, every override of it. The nearest attribute counts - the action's own, else the one on the action it overrides; the controller's own, else its nearest base class's - and it replaces the base's as a whole. An override with `[McpExpose(Destructive = true)]` therefore drops the base action's `ToolName`, `Title`, `Description` and `Scope`, while an override without an `[McpExpose]` of its own keeps all of them.

```csharp
[McpExpose]
public abstract class AdminControllerBase : ControllerBase { }

public class UsersController : AdminControllerBase   // exposed in SelectedOnly mode, every action of it
{
    [HttpGet]
    public Task<ActionResult<List<UserDto>>> GetAll() { ... }
}
```

> **A `ToolName` on a base action names the tool of every controller that inherits the action.** The MCP SDK keeps only one tool per name and drops the others without an error, so the build warns with [`MCP007`](#troubleshooting). When several controllers inherit the action, give each one's tool a name of its own by overriding the action with its own `[McpExpose(ToolName = ...)]`, or leave `ToolName` off the base action.

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

On a controller, it excludes all of its actions, those it inherits included. It is inherited: on a base class, it excludes every controller that derives from it, and on an action, every override of it. It wins over `[McpExpose]`.

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

An override without a `<summary>` of its own, or with only `<inheritdoc/>`, is described by the `<summary>` of the action it overrides, and a parameter it leaves undocumented takes that action's `<param>` for the parameter in the same position, as an override may rename it.

> **Requires XML documentation generation.** The source generator can only read doc comments when the compiler produces them. Enable it in the project that hosts your controllers, otherwise descriptions fall back to the generic values:
>
> ```xml
> <PropertyGroup>
>   <GenerateDocumentationFile>true</GenerateDocumentationFile>
>   <NoWarn>$(NoWarn);CS1591</NoWarn> <!-- optional: silence "missing XML comment" warnings -->
> </PropertyGroup>
> ```

### Request fields: DTO properties, nested objects, array items

The two rules above run at compile time. The fields *inside* an argument's type - a request DTO's properties, nested objects, array items - get their JSON schema at runtime from the MCP SDK, which reflects over the type and reads only `[Description]`. Api.ToMcp fills that gap: every such property without a `[Description]` gets its XML `<summary>` as its schema `description`, read from the `<AssemblyName>.xml` file next to the assembly that declares the property. That includes a property whose schema is the bare `true` (any value), which has no room for a description: one typed `object`, `JsonElement`, `JsonNode`, `JsonValue`, `JsonDocument` or `dynamic`, or a type written by a custom converter, whether from `[JsonConverter]` or one added to the serializer options. A property with a `[JsonConverter]` of its own gets `true` as well, even when its type is an object or a list: the SDK reads the argument with that converter, such as `"12.50 EUR"` for a `Money` property, not in the type's shape. An enum keeps the schema its converter gives it, such as the names `JsonStringEnumConverter` writes. A documented one gets `{"description": "..."}` instead, which accepts the same values.

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
- A 2xx `[ProducesResponseType]` replaces the return type, which is how an `IActionResult` or `IResult` action names one: `[ProducesResponseType(typeof(Product), 200)]` or `[ProducesResponseType<Product>(200)]`. One without a type means the return type for 200 and 201, and no body for any other status, such as 204. Error statuses do not count.
- A 2xx `[ProducesResponseType]` on the controller applies to every action and adds to its return type, or stands in for a return type that names no body (`IActionResult`, `ActionResult`, `IResult`); the action's own declaration of the same status replaces it. Both are inherited, from base controllers and from the action an override replaces.
- A `[Produces]` that names a type declares a 200 of that type, on the action or on the controller, as a `[ProducesResponseType(typeof(Product), 200)]` in its place would: `[Produces(typeof(Product))]`, `[Produces<Product>]` or `[Produces("application/json", Type = typeof(Product))]`. `[Produces<Product>]` names no content type, which means JSON. An override's or a derived controller's `[Produces<Product>]` does not replace a base's `[Produces("text/csv")]`, though: they are different attribute classes, so MVC applies both, and such an action gets no schema.
- A `[ProducesResponseType]` or `[Produces]` whose `Type` property is set to null declares no response, as in ApiExplorer: `[ProducesResponseType(typeof(Product), 200, Type = null)]` neither replaces the return type nor the controller's 200, whatever its constructor names.
- When the success responses declare different types, the tool gets no schema.

A tool gets no schema, and returns text as before, when the success body is not reliably the JSON of a known type: `void` / `Task`, an untyped `IActionResult`, `ActionResult` or `IResult`, file results and streams, `string` (MVC writes it as `text/plain`), `object`, `JsonElement` / `JsonNode` / `JsonDocument`, `IAsyncEnumerable<T>`, a `[Produces]` or `[ProducesResponseType]` content type other than JSON, even next to JSON (`[Produces("application/json", "text/csv")]`), a `[Produces]` that sets `Order` or is a class derived from `ProducesAttribute` (the generator cannot tell which content types MVC then writes), or a type the generated code cannot reference, such as a private nested class.

A content type counts as JSON when its subtype is `json` or ends in `+json` (`application/json`, `text/json`, `application/problem+json`), so `application/x-ndjson`, whose body is many JSON values, does not.

### The schema matches what your API writes

The schema is built when the host starts, from the `System.Text.Json` options MVC writes responses with, `AddJsonOptions` included: property naming, `JsonStringEnumConverter` and the other settings you gave MVC. Structured content is the response body itself, so a client that validates it against the schema accepts it. A property with a `[JsonConverter]` of its own accepts any value, as MVC writes whatever the converter produces, such as `"12.50 EUR"` for a `Money` object; an enum's `JsonStringEnumConverter` keeps its schema. A property MVC can leave out of the body, through `DefaultIgnoreCondition` or its own `[JsonIgnore(Condition = ...)]`, is never listed as `required`, and a `DateTime` or `TimeOnly` claims no `date-time` or `time` format: those require an offset, which MVC does not write for a `DateTime` of unspecified kind, as read from a database, nor for a `TimeOnly`. Response DTO properties get their XML `<summary>` as descriptions, by the [request field rules](#request-fields-dto-properties-nested-objects-array-items). With `ReferenceHandler.Preserve`, whose `$id`/`$ref`/`$values` metadata no JSON Schema describes, every output schema accepts any value and a warning is logged at startup. With `ReferenceHandler.IgnoreCycles`, MVC writes `null` where an object would contain itself, so a response type in which an object can contain itself, such as an EF Core entity whose navigation properties lead back to it, gets an output schema that accepts any value, and a startup warning names those types. That includes a type whose only way back is nullable, such as `Category? Parent`, although its typed schema would allow that `null`. Every other type keeps its schema, and so does a back reference marked `[JsonIgnore]`, which MVC never writes.

### Successes without a body

MVC answers an action that returns `null` with `204 No Content`, and `NoContent()`, `Ok()` or `Accepted()` send no body either. Such a call returns `"structuredContent": null`, and the schema allows it, `{"anyOf": [<the type's schema>, {"type": "null"}]}`, whenever the action can send one. The generator reads each action to decide. The schema is the plain type only when it is proven that every success has a body: an action returning the value itself does not declare it nullable (`Product?`, `Task<Product?>`), and every value the action's own `return` statements produce is

- a not-null value by the compiler's flow analysis, such as `return product;` after a null check or `new Product { ... }`;
- an `ObjectResult` helper given such a value (`Ok(product)`, `CreatedAtAction(..., product)`), whose status, when the action sets it (`StatusCode(201, product)`), is a constant other than 204 or 205, which never carry a body; or
- an error (`NotFound()`, `BadRequest(...)`, `Problem()`, `StatusCode(404)`, ...).

Anything else makes the schema allow null: `NoContent()`, `Ok()`, `return null`, a maybe-null value, a task returned from another method, a helper method, a redirect, any `IResult` (`Results.NoContent()`, but also `Results.Ok(product)`), an action without nullable annotations, and a `[ProducesResponseType(204)]` declared next to the typed success. So does a value from code compiled without nullable annotations, such as an older library's `Find()`: the compiler treats it as not-null without knowing, so it proves nothing, even when held in a local and checked for null first. Allowing null when the action never sends it only costs precision; the other way round, the call would fail.

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
4. If the tool's required scope (based on HTTP method) isn't granted, the call returns `isError: true` with the reason as its text, such as `Insufficient scope. Required: Write, Granted: Read`

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
    Task<CallToolResult> GetAsync(string route, CancellationToken ct = default);
    Task<CallToolResult> PostAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<CallToolResult> PutAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<CallToolResult> PatchAsync(string route, string? jsonBody, CancellationToken ct = default);
    Task<CallToolResult> DeleteAsync(string route, CancellationToken ct = default);

    // Called by each generated tool before the HTTP call.
    // Validates scope only when a scope mapper is configured (see Scope-Based Access Control).
    Task BeforeInvokeAsync(McpScope requiredScope, CancellationToken ct = default);
}
```

You rarely need to implement this yourself, but you can substitute it — for example to route tool calls in-process instead of over HTTP, or to stub calls in tests — by registering your own implementation after `AddMcpTools`.

### Error handling

The invoker methods return a `CallToolResult`. When the invoked API returns a non-success (non-2xx) status, that result has **`isError: true`** with the status code and body as its content — so the AI client knows the call failed (and can react — e.g. re-authenticate on a `401`) rather than treating an error payload as a valid result. This is done by conditioning on the status, not by throwing, since an HTTP status is an expected outcome. Genuine transport failures (network errors, timeouts) still surface as exceptions, which the MCP server likewise reports as `isError: true`.

## Correlating Tool Calls with API Logs

A tool call reaches your API as two HTTP requests: the MCP client's `POST /mcp`, and the request the tool makes to your controller, such as `GET /api/products/{id}`. Each has its own `HttpContext.TraceIdentifier`, logged as `RequestId`, but both belong to one W3C trace, with nothing to switch on: .NET's `HttpClient` sends the current `Activity` as a `traceparent` header with every request, the invoker's included, and ASP.NET Core continues that trace for the internal request. Because the internal request goes through your real pipeline, it is logged and traced like any other request, and no correlation ID has to be built by hand.

### What the internal request carries

| Header | Where it comes from |
|---|---|
| `traceparent`, and `tracestate` when the MCP request has one | .NET's `HttpClient`, from the current `Activity`: the trace of the MCP request, which continues the client's trace when the MCP request came with a `traceparent` |
| `baggage` when the MCP request has one (`Correlation-Context` on .NET 8 and 9) | .NET's `HttpClient`, from the current `Activity`'s baggage, the same way as `traceparent` |
| `Authorization` | Copied from the MCP request, when it parses as an authentication header |
| `X-MCP-Internal-Call: true` | Added by the invoker, so that `UseMcpLoopPrevention` refuses the request if it targets `/mcp` |

No other header is copied: not `X-Correlation-ID`, `X-Request-ID`, cookies, `Accept-Language`, `User-Agent` or `X-Forwarded-For`. If your API logs a correlation ID taken from a header of its own, the internal request has none; use the trace ID, or have the client send the ID as W3C `baggage` and read it with `Activity.Current?.GetBaggageItem(...)`, which returns it on both requests.

`X-MCP-Internal-Call` also tells a tool's request apart from a direct API call, for example in a middleware that adds it to the log scope. It is loop prevention, not a security boundary: any client can send it, and the API serves such a request as usual (only `/mcp` refuses it), so never let it skip authentication or authorization.

### Logs

The default host already puts `TraceId`, `SpanId` and `ParentId` on the scope of every log entry, and ASP.NET Core adds `RequestPath` and `RequestId`. Two defaults get in the way: the console logger leaves scopes out, and the templates' `"Microsoft.AspNetCore": "Warning"` drops ASP.NET Core's request log, so the internal request writes nothing unless your controller logs. In `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Microsoft.AspNetCore.Hosting.Diagnostics": "Information"
    },
    "Console": {
      "FormatterName": "simple",
      "FormatterOptions": { "IncludeScopes": true }
    }
  }
}
```

Keep `FormatterName`: without it, `FormatterOptions:IncludeScopes` has no effect. `builder.Logging.AddSimpleConsole(o => o.IncludeScopes = true)` does what the `Console` part does, in code, and `"FormatterName": "json"` writes `TraceId`, `SpanId` and `RequestId` as fields of the entries in each log entry's `Scopes` array instead.

A failed call of the demo's `Products_GetById`, abridged: the `ConnectionId` scope and some entries are left out.

```
info: Microsoft.AspNetCore.Hosting.Diagnostics[1]
      => SpanId:75222e282e0e7fb9, TraceId:bbefb1c82eadb8adb003f3616f63f5ba, ParentId:0000000000000000 => RequestPath:/mcp RequestId:0HNPE2I2L3D83:00000001
      Request starting HTTP/1.1 POST http://localhost:5344/mcp - application/json 141
info: System.Net.Http.HttpClient.IMcpHttpInvoker.LogicalHandler[100]
      => SpanId:75222e282e0e7fb9, TraceId:bbefb1c82eadb8adb003f3616f63f5ba, ParentId:0000000000000000 => RequestPath:/mcp RequestId:0HNPE2I2L3D83:00000001 => HTTP GET http://localhost:5344/api/products/d0000000-0000-0000-0000-000000000000
      Start processing HTTP request GET http://localhost:5344/api/products/d0000000-0000-0000-0000-000000000000
info: Microsoft.AspNetCore.Hosting.Diagnostics[2]
      => SpanId:d819f4bb92caee09, TraceId:bbefb1c82eadb8adb003f3616f63f5ba, ParentId:3cbedd64401d9593 => RequestPath:/api/products/d0000000-0000-0000-0000-000000000000 RequestId:0HNPE2I2L3D84:00000001
      Request finished HTTP/1.1 GET http://localhost:5344/api/products/d0000000-0000-0000-0000-000000000000 - 404 - application/problem+json;+charset=utf-8 12.0592ms
warn: Api.ToMcp.Runtime.Services.McpHttpInvoker[0]
      => SpanId:75222e282e0e7fb9, TraceId:bbefb1c82eadb8adb003f3616f63f5ba, ParentId:0000000000000000 => RequestPath:/mcp RequestId:0HNPE2I2L3D83:00000001
      MCP HTTP call failed with status 404: {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,"traceId":"00-bbefb1c82eadb8adb003f3616f63f5ba-d819f4bb92caee09-00"}
info: ModelContextProtocol.Server.McpServer[2065726448]
      => SpanId:75222e282e0e7fb9, TraceId:bbefb1c82eadb8adb003f3616f63f5ba, ParentId:0000000000000000 => RequestPath:/mcp RequestId:0HNPE2I2L3D83:00000001
      "Products_GetById" completed. IsError = True.
info: Microsoft.AspNetCore.Hosting.Diagnostics[2]
      => SpanId:75222e282e0e7fb9, TraceId:bbefb1c82eadb8adb003f3616f63f5ba, ParentId:0000000000000000 => RequestPath:/mcp RequestId:0HNPE2I2L3D83:00000001
      Request finished HTTP/1.1 POST http://localhost:5344/mcp - 200 - text/event-stream 149.0195ms
```

To find the internal request of a tool call:

1. Find the call by its tool name. The MCP SDK logs `"<tool name>" completed. IsError = <True|False>.` (category `ModelContextProtocol.Server.McpServer`, with `ToolName` and `IsError` as fields), and the invoker's `HttpClient` logs the URL, status and duration of its request (category `System.Net.Http.HttpClient.IMcpHttpInvoker.LogicalHandler`). Both log at `Information`, which the templates keep, in the scope of the `POST /mcp` request.
2. Take the `TraceId` of that entry.
3. Search for it. The entries whose `RequestPath` is your API route, under a `RequestId` other than the MCP request's, are the internal request, your controller's own log entries included.

Code that wants the trace ID itself, to put it in its own messages or responses, reads `Activity.Current?.TraceId` from `System.Diagnostics`; it is the same in the MCP request and the internal one.

Match on `TraceId`, not on `ParentId`: without a tracing listener, .NET's `HttpClient` still starts an `Activity` of its own for the outgoing request, so the internal request's `ParentId` is that activity (`3cbedd64401d9593` above), not the `SpanId` of the MCP request.

`POST /mcp` finishes with `200` even when the tool failed, because the failure is `isError: true` inside the JSON-RPC result. Look for `IsError = True` and the invoker's `MCP HTTP call failed with status ...` warning instead. When the API answers with a ProblemDetails body, as `[ApiController]` does for `NotFound()` or `Problem()`, its `traceId` is the internal request's `00-<trace ID>-<span ID>-<flags>`, and it reaches the MCP client in the error text: a user who reports a failed call can hand you the trace ID.

### OpenTelemetry

With tracing on, the link shows as one trace. Add the packages `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http` and an exporter, such as `OpenTelemetry.Exporter.OpenTelemetryProtocol`:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("Experimental.ModelContextProtocol") // optional: a span per MCP request, named after the tool
        .AddOtlpExporter());
```

A call of `Products_GetById` is then one trace:

```
POST mcp/                              server   the MCP request
└─ tools/call Products_GetById         server   only with AddSource("Experimental.ModelContextProtocol")
   └─ GET                              client   the invoker's HttpClient
      └─ GET api/Products/{id:guid}    server   the internal request to the controller
```

`Experimental.ModelContextProtocol` is the MCP SDK's `ActivitySource`, and its name says the SDK may still change it. With that `AddSource`, when the request's `params._meta` carries a `traceparent`, the `tools/call` span continues that trace context instead, and is only linked to the `POST mcp/` span. A client that sends it in the same trace as its `traceparent` header still gets all four spans in one trace. A client that sends only `_meta.traceparent` gets the tool call and the internal request in its own trace and `POST mcp/` in another, joined by that link; the tool call's log entries follow the `tools/call` span, so their `TraceId` still matches the internal request's.

### When the link breaks

With `HttpServerTransportOptions.PerSessionExecutionContext = true` and a stateful session (`SessionMode = HttpServerSessionMode.Stateful`, `StatefulForInitializeClients` for clients that send `initialize`, or the older `Stateless = false`), every tool call runs in the context of the session's `initialize` request. The MCP SDK marks the option obsolete, and the default stateless mode ignores it. The internal request then joins the trace of that `initialize` request, and the invoker's log entries carry its `RequestId`. No `Authorization` header is forwarded at all, because that request's `HttpContext` is gone by then, and with scope validation on every call is refused with `Scope validation needs the HTTP request of the tool call, and none is available. ...`. The host logs a startup warning when the option is set this way. Leave the option at its default, `false`.

Turning off .NET's activity propagation, with the `System.Net.Http.EnableActivityPropagation` switch set to `false` or `DOTNET_SYSTEM_NET_HTTP_ENABLEACTIVITYPROPAGATION=0`, stops `HttpClient` from sending `traceparent` and `baggage`, so the internal request starts a trace of its own.

## Limitations

- **Controllers only.** Minimal API endpoints (`app.MapGet`, `app.MapPost`, …) are not scanned; only `ControllerBase`-derived / `[ApiController]` classes are. Support is tracked in [#11](https://github.com/Kebechet/Api.ToMcp/issues/11).
- **One route per action.** MVC serves an action at every route and HTTP method its attributes give it. Its tool takes the first method a tool sends, in the order of the routes, `GET` for `[HttpHead] [HttpGet]` and `PUT` for `[AcceptVerbs("HEAD", "PUT", "POST")]`, and calls it at the first route serving that method with a template of its own, else at the controller's route: `[HttpGet(Name = "find")] [Route("{id}")]`, which MVC serves for GET at both, is called at `{id}`.
- **No `[FromForm]` / file uploads.** Form-bound parameters and `IFormFile` are not mapped.
- **API versioning is partial.** URL-segment versioning (`[Route("api/v{version:apiVersion}/[controller]")]`) is supported — the concrete version from `[ApiVersion]` is baked into the generated route (defaulting to `1.0` if the attribute is absent), so each tool targets a fixed version. Query-string (`?api-version=`) and header/media-type versioning are **not** sent by the generated tools, so those rely on the API's default version.

## Debugging & Inspecting Your Tools

You don't need Claude, ChatGPT or another AI client to see what the generator produced. The official [MCP Inspector](https://github.com/modelcontextprotocol/inspector) connects to any MCP server: point it at your running app and it shows each tool exactly as a client receives it, and calls it against your API. The commands below were checked with Inspector 2.11.0, which declares Node.js 22.19 or later. They ask for `@latest` because on an older Node a bare `npx @modelcontextprotocol/inspector` runs the deprecated v1 instead, which prints `@modelcontextprotocol/inspector v1 is deprecated` and lacks some of the flags used here. On such a Node, `@latest` still runs; npm only prints `npm warn EBADENGINE Unsupported engine` for the Inspector and for `undici` when it installs them.

### Connecting

The endpoint URL is the app's address plus the pattern passed to `MapMcpEndpoint`, and the transport is Streamable HTTP. Run the app on plain HTTP and start the Inspector with that URL:

```bash
dotnet run --urls http://localhost:5080
npx @modelcontextprotocol/inspector@latest --transport http --server-url http://localhost:5080/mcp
```

The Inspector opens in the browser at `http://127.0.0.1:6274` with the connection filled in. Where it cannot open a browser, open the full URL it prints, which carries the `MCP_INSPECTOR_API_TOKEN` the page asks for. Turn on the server's connection switch, open the **Tools** tab, pick a tool (listed by its title, with its name beneath), fill in its arguments and press **Execute Tool**. The result shows the text content and, under **Structured Output**, the structured content.

**HTTPS.** The Inspector runs on Node, which does not trust the ASP.NET Core development certificate, so an `https://localhost` URL fails with `fetch failed` (cause: `self-signed certificate`). Inspect over HTTP as above, or export the certificate (only the certificate, not its key) and let Node trust it:

```bash
dotnet dev-certs https --export-path ./aspnetcore-dev.pem --format Pem
NODE_EXTRA_CA_CERTS=./aspnetcore-dev.pem npx @modelcontextprotocol/inspector@latest --transport http --server-url https://localhost:5001/mcp
```

### From the command line

The same package has a CLI that prints the raw JSON, which suits scripts and CI:

```bash
npx @modelcontextprotocol/inspector@latest --cli http://localhost:5080/mcp --method tools/list
npx @modelcontextprotocol/inspector@latest --cli http://localhost:5080/mcp --method tools/call --tool-name Products_GetById --tool-arg id=a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

- The transport is inferred from a URL ending in `/mcp`; for another pattern add `--transport http`.
- `--tool-arg` converts each value to the type the tool's input schema declares: numbers and booleans are parsed (`days=3` is a number), and an object or array argument is parsed as JSON, `--tool-arg 'request={"city":"Brno","temperatureC":21}'`. Anything else stays a string. `--tool-args-json` passes the arguments verbatim instead.
- A call whose result is an error exits with code `5`. `--format json` prints one JSON object, with the whole output under a `result` key of its own; that is the CLI's envelope, separate from the `{"result": ...}` wrapping of structured content described below.
- `tools/list` also prints a schema portability summary, and `--strict` its details. The usual warnings are nullable properties written as a type array, `"type": ["string", "null"]`: valid JSON Schema, which a client that allows one type per property may reject or simplify.

### What to check

| Look at | What it tells you |
|---|---|
| The tool list | One tool per selected action, named by `toolNameFormat`, plus every hand-written `[McpServerTool]`. If one is missing, see [A tool is missing](#a-tool-is-missing). |
| `description`, and each argument's | The `<summary>` and `<param>` text, which is what the model reads. `Invokes Controller.Action` or `Parameter: name` is the generic fallback: the XML comment is missing or the project does not generate XML docs. See [Tool Descriptions](#tool-descriptions). |
| `title` and `annotations` | The title derived from the action name, and the read-only, destructive, idempotent and open-world hints derived from the scope and HTTP method. See [Tool annotations](#tool-annotations) and [Tool titles](#tool-titles). |
| `inputSchema` | One property per route, query and body parameter. A request DTO's properties carry their XML `<summary>` as `description`; see [Request fields](#request-fields-dto-properties-nested-objects-array-items). |
| `outputSchema` | The action's response type. A tool without one returns text only; see [Which type](#which-type). |
| A call's `structuredContent` | The response body as JSON, next to the same body as text. |
| A call with `isError: true` | `HTTP <status>: <reason> - <body>` is your API's own answer, for example `HTTP 404: Not Found - {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404,...}`. `An error occurred invoking '<tool>'.` means the tool threw instead; see [A call fails](#a-call-fails). |

The Inspector negotiates protocol `2025-11-25` unless told otherwise, so a tool whose result is not an object, such as a list or a body that can be `null`, shows its `outputSchema` and `structuredContent` wrapped in `{"result": ...}` (see [Clients on an older protocol](#clients-on-an-older-protocol)). Add `--protocol-era modern` to negotiate `2026-07-28` and see them unwrapped. In that mode Inspector 2.11.0 rejects a success without a body: the server sends `"structuredContent": null`, which the schema allows, and the Inspector reports `declares an output schema but returned no structured content`. The default mode shows the same call as `{"result": null}`.

### Auth and scopes

Every tool is listed, whatever the caller is allowed to do: scopes are checked when a tool is called, not when the list is built. To call tools behind auth, send the header your API expects; the generated tools forward it to the API call:

```bash
npx @modelcontextprotocol/inspector@latest --cli http://localhost:5080/mcp --header "Authorization: Bearer <token>" --method tools/call --tool-name Products_GetAll
```

The web UI takes the same `--header` flag, and needs it at launch: a session started with a server URL is read-only, so a header cannot be added in the UI afterwards. If your API uses JWT bearer authentication (`AddAuthentication().AddJwtBearer()`), `dotnet user-jwts create --claim "permissions=mcp:read" -o token`, run in the API project, prints a token the app accepts in the Development environment.

If your app requires authentication on the MCP endpoint itself, such as through a fallback authorization policy, `tools/list` already needs `--header`. Without it the endpoint answers `401`, and the CLI reports `auth_required` and exits with code `3`; in an interactive terminal the Inspector tries to start an OAuth sign-in instead.

A refused call comes back as `isError: true`:

| Situation | Result text | Where the reason is |
|---|---|---|
| No scope validation, and the API refuses the call | `HTTP 401: Unauthorized` | In the result |
| Scope validation on, no token | `User is not authenticated. Scope validation requires authentication.` | In the result |
| The token has no `ClaimName` claim | `Claim 'permissions' not found. Required scope: Read` | In the result |
| The claim grants too little | `Insufficient scope. Required: Write, Granted: Read` | In the result |
| A stateful session with `PerSessionExecutionContext = true` | `Scope validation needs the HTTP request of the tool call, and none is available. ...` | In the result, and a startup warning; see [When the link breaks](#when-the-link-breaks) |

A scope rejection comes back like a failed API call, as an error result whose text is the reason: the scope the tool requires, the scope the caller was granted, or the claim it lacks, never the token or the claim's value. The app logs the same reason as a warning from `Api.ToMcp.Runtime.Services.McpHttpInvoker`: `MCP tool call refused by scope validation: <reason>`. `IMcpHttpInvoker.BeforeInvokeAsync` still throws an `UnauthorizedAccessException`; a call filter that `AddMcpTools` adds to the MCP server turns the default invoker's into that result. See [Scope-Based Access Control](#scope-based-access-control).

### A tool is missing

- The Inspector lists what the running app registered. After changing `generator.json` or an attribute, rebuild, restart the app and connect again.
- The `generator.json` the package creates on the first build is `SelectedOnly` with an empty `include`, so it exposes only `[McpExpose]` actions until you list others or switch to `AllExceptExcluded`. The build that creates it warns with `MCP100`, and `MCP006` follows while no tool is generated.
- A `generator.json` that is not valid JSON (a trailing comma is enough) means the defaults, `SelectedOnly` with nothing included, so again only `[McpExpose]` actions. A `mode` other than those two is read as `SelectedOnly`, with the file's `include` list. Both give build warning `MCP001`, pointing at what could not be read; see [Troubleshooting](#troubleshooting).
- An action whose route contains `/mcp` anywhere, such as `api/settings/mcp-status`, is never exposed (loop prevention).
- An action that is not `public` is skipped without a diagnostic, as is one MVC serves only for HTTP methods a tool does not send (`[HttpHead]`, `[HttpOptions]`, `[AcceptVerbs("HEAD")]`), for every method (a `[Route]` alone), or through conventional routing. Attributes derived from MVC's routing attributes, or implementing their interfaces, count like MVC's own; see [Controllers and Actions](#controllers-and-actions).
- An action declared on a base controller is a tool of each concrete controller that inherits it, named after that controller (`Products_GetAll`, not `Crud_GetAll`); an abstract base class gets none. Classes and methods MVC does not serve get no tool, among them `[NonController]` classes, `[NonAction]` methods, overrides of `ToString`, `Equals` or `GetHashCode`, and the `IDisposable.Dispose` implementation. See [Controllers and Actions](#controllers-and-actions).
- Tools that share a name are listed as one, as the MCP SDK keeps only one of them. The build warns with `MCP007`; see [Troubleshooting](#troubleshooting).
- An `include` entry that names no controller or action, through a typo or a base controller's name, selects nothing. The build warns with `MCP008`; see [Troubleshooting](#troubleshooting).
- An action whose route the generator cannot build, such as one with a route value no parameter fills, gets no tool. The build warns with `MCP009`; see [Troubleshooting](#troubleshooting).
- `AdditionalFiles`, include names and minimal APIs: see [Troubleshooting](#troubleshooting).

To read the generated code, set `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` in the project. A build then writes it to `obj/<Configuration>/<TargetFramework>/generated/Api.ToMcp.Generator/Api.ToMcp.Generator.McpToolGenerator/`, one file per tool, such as `ProductsController_GetByIdTool.g.cs`. A project can move it with `<CompilerGeneratedFilesOutputPath>`: the demo sets `$(BaseIntermediateOutputPath)Generated`, so its files are in `obj/Generated/Api.ToMcp.Generator/Api.ToMcp.Generator.McpToolGenerator/`. A file stays there after its tool is gone; `McpToolsRegistration.g.cs` lists the current tools.

### A call fails

`An error occurred invoking '<tool>'.` means the generated tool threw, and the exception is in the app's log. The usual cause is the tool's HTTP call to your own API at the base URL from [Base URL Resolution](#base-url-resolution): when nothing answers there, the log shows an `HttpRequestException` such as `No connection could be made because the target machine actively refused it. (localhost:5999)`. Check the address it called, `McpTools:BaseUrl` when set and otherwise an address the app listens on, and see [Troubleshooting](#troubleshooting) for reverse proxies and redirects.

`HTTP <status>: ...` means the call reached your API and it answered with that status, so look at the API itself: its logs, auth and validation.

## Troubleshooting

The build reports these warnings:

| Code | From | When |
|------|------|------|
| `MCP001` | Generator | `generator.json` cannot be used as written: malformed JSON, a value of the wrong type, an unknown `mode`, an empty file, or an unreadable one |
| `MCP006` | Generator | The config selects none of the project's actions, so no tools are generated |
| `MCP007` | Generator | Two or more generated tools get the same name, and the MCP SDK keeps only one of them |
| `MCP008` | Generator | An `include` or `exclude` entry names no controller or action of the project, so it selects or excludes nothing |
| `MCP009` | Generator | An action gets no tool, as the generator cannot build its route: a route value no parameter fills, or a routing attribute whose code it cannot read |
| `MCP100` | Build | The build created a starter config, which generates no tools until its `include` is filled |
| `MCP101` | Build | The config was found at a path that differs from `ApiToMcpConfigPath` only in letter case |

Suppress one with `<NoWarn>$(NoWarn);MCP100</NoWarn>`. To keep one a warning while `TreatWarningsAsErrors` or `-warnaserror` turns warnings into errors, list it in `<WarningsNotAsErrors>`.

**No tools are generated.**
- Look for build warning `MCP006`, which names the mode and the cause: an empty `include`, `include` entries that match no controller or action, an `exclude` list that covers every action, or no `generator.json` among the `AdditionalFiles`. A `generator.json` the generator cannot use as written gets `MCP001` instead (see below). `MCP006` points at the config file it read, and comes only when the project has an action that could become a tool, so a project without controllers, or whose actions are all `[McpIgnore]` or under `/mcp`, gets none.
- Confirm `Mcp/generator.json` is registered as an `AdditionalFiles` item in your `.csproj`.
- In `SelectedOnly` mode, only endpoints listed in `include` are exposed — check the names match `Controller.Action`, including their case, where `Controller` is the controller that serves the action, not a base class it inherits the action from.
- Each `include` entry that names no controller or action gets build warning `MCP008`, which says what is wrong with it; `MCP006` then only points there.
- Ensure the endpoint lives on a controller MVC serves: a public, non-abstract, non-generic `ControllerBase`-derived class (or one with `[ApiController]`) without `[NonController]`; see [Controllers and Actions](#controllers-and-actions). Minimal APIs are not supported.
- With `TreatWarningsAsErrors`, `MCP006` fails the build until the config selects an action. To build with no tools on purpose, add `MCP006` to `<NoWarn>` or `<WarningsNotAsErrors>`.

**Build warning `MCP001`: `generator.json cannot be used as written`.**
- The file is not valid JSON (a missing bracket, a trailing comma), has a value of the wrong type (a string where `include` expects a list), names a `mode` other than `SelectedOnly` or `AllExceptExcluded`, is empty or holds only `null`, or could not be read. The warning quotes the parser's error and points at where the parser stopped, or at the unknown `mode`.
- The generator falls back as the warning says: to the default config, `SelectedOnly` mode with an empty `include`, so only `[McpExpose]` actions become tools; or, for an unknown `mode`, to `SelectedOnly` mode with the file's lists. `MCP006` is not reported as well, as its advice would describe the fallback rather than your file.

**Build warning `MCP007: Tool name '...' is given to N tools`.**
- Several generated tools have the same name, and the MCP SDK keeps only one of them, dropping the others without an error. The warning names every action that produces the name and points at its declaration. The generator still generates them all, so a name a client already calls does not change under it.
- The usual causes: a `ToolName` on a base action that several controllers inherit, two actions with the same `ToolName`, overloads (both get `{Controller}_{Action}`), or a `toolNameFormat` without `{Controller}`. Give each tool its own name with `[McpExpose(ToolName = ...)]`, on an override for an inherited action, or exclude the ones you do not need.
- To accept a collision, suppress the warning like any other: `#pragma warning disable MCP007` in the file it points at, `dotnet_diagnostic.MCP007.severity = none` in an `.editorconfig` section for that file, or `<NoWarn>` for the project.

**Build warning `MCP008: The include entry '...' in generator.json names no controller or action of the project`.**
- The entry matches nothing, so an `include` entry selects nothing, and an `exclude` entry hides nothing: in `AllExceptExcluded` mode, what it was meant to hide is exposed. The warning points at the entry in `generator.json`, and covers both lists whichever one the mode reads, as the other takes effect once the mode changes.
- A typo: entries are case-sensitive, so `"products"` or `"ProductsController.getById"` match nothing. The warning names the nearest name the project has, when one differs from the entry by a letter or two, case aside.
- A base controller: an action a controller inherits is matched by that controller's name, not by its base class's. An entry such as `"CrudController"` or `"Crud.Delete"` matched the base's own tools while the generator gave base classes tools, before https://github.com/Kebechet/Api.ToMcp/pull/43, and matches nothing since then, so an `exclude` entry like it now exposes the base's actions under every controller that inherits them. The warning names those controllers and how to write the entry: `"ProductCrud.Delete"` and `"OrderCrud.Delete"` for `"Crud.Delete"`, or a whole controller.
- An entry naming a controller or action that `[McpIgnore]` or an `/mcp` route keeps from MCP, or a controller without `[Http*]` actions, names something and gets no `MCP008`. When no tool is generated, `MCP006` says that it names no action that can become one.
- With `TreatWarningsAsErrors`, `MCP008` fails the build until the entry is fixed or removed. To keep an entry anyway, add `MCP008` to `<NoWarn>` or `<WarningsNotAsErrors>`.

**Build warning `MCP009: No MCP tool is generated for action '...'`.**
- The generator cannot build the request MVC serves the action at, so it generates no tool for it rather than one that would not compile or would call another endpoint. The warning gives the reason and points at the action; the other tools are generated as usual.
- `no parameter or property fills the route value '{id}'`: no parameter of the action, and no property of one, fills a placeholder of its route, such as `[HttpGet("z/{id}")] public IActionResult Z()`, which reads the value from `RouteData` itself. Give the action a parameter for it, `Z(int id)`. An optional route value nothing fills only gets the warning before the end of the route, where MVC needs it; at the end, the URL leaves it out.
- `the route template of its [...] attribute is set in code the generator cannot read` (or `of the [...] attribute on <controller>`, or `the HTTP methods of ...`): a routing attribute of your own gives the template or the HTTP methods through code the generator cannot follow, usually because the attribute is declared in a referenced assembly, whose constructor bodies are not in its metadata, or because its constructor computes them from more than constants and its own parameters. Route the action, or its controller, with MVC's `[Route]` and `[Http*]` attributes, or declare the attribute in the project.
- To keep the action from MCP without the warning, mark it `[McpIgnore]` or exclude it in `generator.json`, or suppress the warning like any other.

**Build warning `MCP100`: `Api.ToMcp created a starter config at ...`.**
- No config existed at `ApiToMcpConfigPath`, so the build wrote a starter one there, which generates no tools until you fill in its `include`. The warning points at that file, and comes only from the build that writes it. `TreatWarningsAsErrors` leaves it a warning, as it covers compiler warnings only, while `-warnaserror` makes it fail that build unless `MCP100` is in `<WarningsNotAsErrors>`.
- If your config is somewhere else, set `ApiToMcpConfigPath` to it and delete the created file. See [Config file location](#config-file-location).

**Build warning `MCP101`: `Api.ToMcp found no config at ... but found ...`.**
- On a case-sensitive file system, the config is at a path that differs from `ApiToMcpConfigPath` only in the letter case of its folder or file name, such as `mcp/generator.json` for the default `Mcp/generator.json`. The build gives the generator that file, and the warning points at it. The build writes no starter config, and replaces an `AdditionalFiles` item that names the missing configured path. It generates the same tools as on Windows or macOS.
- Set `ApiToMcpConfigPath` to that file, or rename it to match, and the warning goes away.

**`InvalidOperationException: Unable to determine base URL`.**
- Set `McpTools:BaseUrl` in configuration, or ensure the server has finished starting before the first tool call. See [Base URL Resolution](#base-url-resolution).

**Self-calls fail or redirect to HTTPS behind a reverse proxy.**
- The invoker derives the correct `https://` base from the incoming request when the server only listens on HTTP, but the most reliable fix is to pin `McpTools:BaseUrl` explicitly.

**`MissingMethodException` on `AddMcpTools` at startup** (e.g. `Method not found: '...McpServerServiceCollectionExtensions.AddMcpServer(...)'`).
- Your app references a different version of `ModelContextProtocol.*` than the one this package targets (`2.2.0`). Remove your explicit reference so the correct version flows transitively, or pin it to the exact same version. See [Requirements](#requirements).

**Startup warning `Assembly '...' has types in MCP tool schemas but no XML documentation file`.**
- A type used in a tool argument or response lives in a project built without `<GenerateDocumentationFile>true</GenerateDocumentationFile>`, comes from a NuGet package while the app lacks `<CopyDocumentationFilesFromPackages>true</CopyDocumentationFilesFromPackages>`, or the `.xml` was not deployed next to the assembly. Enable the matching property and check the publish output; see [Request fields](#request-fields-dto-properties-nested-objects-array-items). Set `options.ArePropertyDescriptionsFromXmlDocs = false` to turn the feature (and the warning) off.
- The variant `... could not be read (...)` means the file is there but damaged; rebuild and redeploy it.

**Startup warning `HttpServerTransportOptions.PerSessionExecutionContext is true and SessionMode is ...`.**
- A stateful session then runs every tool call without the HTTP request that carried it, so the tools forward no `Authorization` header and scope validation refuses every call. Set `PerSessionExecutionContext` to `false`, its default; see [When the link breaks](#when-the-link-breaks).

**Tool calls return `401`/`403`.**
- Auth forwarding only works when the MCP request itself carries an `Authorization` header — it is forwarded to the API call verbatim.
- When scope validation is enabled, the request must be authenticated and the mapped scope must cover the tool's required scope (`Read`/`Write`/`Delete`).

## How It Works

1. **Compile Time**: The source generator scans your controllers for HTTP actions
2. **Code Generation**: For each selected endpoint, a tool class is generated with `[McpServerToolType]` attribute
3. **Startup**: `AddMcpTools` registers every `[McpServerToolType]` class of the assembly (generated and hand-written) and builds the tools and their argument schemas when the host starts, so a tool that cannot be created fails at startup rather than at the first request
4. **Runtime**: When an MCP client calls a tool, it invokes your API via HTTP internally
5. **Loop Prevention**: The `X-MCP-Internal-Call` header prevents MCP endpoints from being called recursively

### Where each argument is sent

A tool takes the action's parameters as its arguments and sends each one where MVC binds it from:

- `[FromRoute]`, `[FromQuery]` and `[FromBody]` decide for themselves. `[FromQuery(Name = "q")]` is sent as `q`, and a `[FromQuery]` object as one key per property (`filter.City=...`). MVC reads a `[FromRoute]` property of a `[FromQuery]` object, such as one a record's constructor parameter `[FromRoute(Name = "orderId")]` sets, only when no query value carries the object's prefix, so when such a property fills the route, the object goes out without its prefix: the property in the route, the others under their own names. A `[FromQuery(Name = "f")]` object MVC always binds under `f.`, where such a property finds no route value and stays at its default. A `[FromRoute]` object fills the route from its properties, by the names they bind under, and sends a `[FromQuery]` property in the query string under its binder name: `c` for `[FromQuery(Name = "c")]`.
- A parameter without a binding attribute goes where MVC infers it from. Named in the route template, it fills the route. Otherwise a **simple** type goes to the query string on every HTTP method.
- In an `[ApiController]`, a **complex** class or struct without a binding attribute, such as a DTO or a `List<T>`, is the JSON body of a POST, PUT or PATCH. `[ApiController]` counts on the controller, a base class or the assembly, as does any attribute implementing `IApiBehaviorMetadata`. On a GET or DELETE, which send no body, the tool cannot fill such a parameter, so a filter object read from the query string needs `[FromQuery]`. An array, an interface such as `IEnumerable<T>`, or `object` is not sent on any method, although `[ApiController]` reads it from the body: mark a collection `[FromBody]` to send it as the body, or `[FromQuery]` to send it in the query string (`?ids=1&ids=2`).
- MVC reads a complex parameter from the route, query and form values instead of the body in a controller without `[ApiController]`, and in an `[ApiController]` when a property of its type, an inherited one included, has a binding attribute of its own that names a binding source, such as `[FromRoute]` or `[FromQuery]`, or its type does: a type with `[ModelBinder(typeof(...))]`, such as a strongly-typed id with a binder of its own, or one MVC binds from a source of its own, `IFormFile`, `IFormFileCollection`, `IFormCollection`, a collection of `IFormFile` or a `CancellationToken`. A nullable struct, `OrderId?`, does not carry the attributes of `OrderId`. `[ModelBinder]` and `[BindProperty]` name one only with a binder type, so `[ModelBinder(Name = "c")]` alone leaves the type in the body. Neither does an attribute on a property that a record's primary constructor sets count, whether it targets the constructor parameter or, with `[property: FromQuery]`, the property: MVC binds such properties through the constructor and still reads the record from the body. When it does read the record from the route and query, it binds such a property with the constructor parameter's attributes, `[FromQuery(Name = "c")]` included, and ignores a `[property: ...]` one, and the tool sends it the same way. The tool then sends the parameter in the query string on every HTTP method, as `filter.City=...`. A `[FromRoute]` property binds only when no value carries the `filter.` prefix, whichever parameter fills that route value, so such a parameter goes out without the prefix: the tool fills the route from the property, unless another parameter does, and sends the other properties under their own names, `City=...` or the name `[FromQuery(Name = "...")]` gives. A property without a binding attribute named like a placeholder reads the route value only without the prefix, but under it reads `filter.Id` from the query string, so the tool drops the prefix only when that property is the one filling the route; otherwise the parameter keeps the prefix and its own value. A property read from a header, a form field or a service is not sent, nor, with the prefix or without it, is one whose getter is not public, which MVC does not bind.
- A placeholder no parameter and no property MVC reads from the route fills, such as `{id}` with a `[FromQuery] Id` property, is filled from a property of that name, a `[FromBody]` parameter's first, so the URL is complete; the property is still sent where MVC reads it. A property MVC does read from the route comes before one of the body: with `([FromBody] Shipment body, AttributedQuery q)` on `{id}`, the route carries `q.Id` and the body `body.Id`.
- A placeholder that nothing fills, neither a parameter nor a property of any name, leaves the action without a tool and gets build warning [`MCP009`](#troubleshooting), as the URL cannot be built. An optional one at the end of the route, `{id?}`, `{id=5}` or a catch-all `{*path}`, is left out of the URL instead, as MVC serves the action without it. A catch-all value (`{*path}`, `{**path}`) keeps its slashes, which MVC reads back from the path.
- A property sent under its own name shares the query string with the action's other parameters and with the properties of other parameters sent that way: a parameter or property of the same name binds the same value, the first one sent. With `Find(AttributedQuery q, string city)`, `city` gets `q.City`. Rename one of them, or bind it with `[FromQuery(Name = "...")]`.
- A simple type is one MVC reads from a single string: `string`, the numeric types, `bool`, `Guid`, the date and time types, enums, `Uri`, `Version`, their nullable forms, and any type with a `[TypeConverter]` that converts from string, on the type, a base class or a public interface, or with a `bool TryParse(string, out T)` or `TryParse(string, IFormatProvider, out T)` that MVC finds: public and static on the type or a base class, an explicit `IParsable<T>` implementation, or a static method with a body that exactly one of its interfaces offers. A strongly-typed id is one, and is sent as one value:

  ```csharp
  [TypeConverter(typeof(OrderIdConverter))]
  public readonly record struct OrderId(Guid Value)
  {
      public override string ToString() => Value.ToString();   // the text OrderIdConverter reads back
  }

  [HttpGet("orders")]
  public Task<ActionResult<Order>> Find(OrderId orderId) { ... }   // sent as ?orderId=0189dfd3-...
  ```

Route and query values are written with the invariant culture. A type with a `[TypeConverter]` is written by the converter's `ConvertTo`, and a converter that implements only `ConvertFrom` leaves that to the type's `ToString()`. A type MVC reads through `TryParse` is written with its `ToString()`, the invariant-culture one when it is `IFormattable`. Either way the text has to be what the converter or `TryParse` reads back. A record's generated `ToString()` (`OrderId { Value = 0189dfd3-... }`) is not: a converter or `TryParse` that rejects that text fails the binding, which an `[ApiController]` answers with 400, and one that accepts any text binds a value made from it. So give such a type a converter that also converts to string, or a `ToString()` that returns the parseable form. A record derived from a record that overrides it gets its own generated `ToString()` again, unless the base override is `public sealed override string ToString()`.

The generator finds the converter in the type's `[TypeConverter]` at compile time. One registered at runtime with `TypeDescriptor.AddAttributes` is invisible to it, so mark such a parameter `[FromQuery]`. What a converter answers MVC is only known when it runs, so the generator counts as simple two types MVC treats as complex: one whose converter overrides `CanConvertFrom` to accept only another source type, such as an `int`, and one whose `[TypeConverter("...")]` names a type that does not exist. Give such a parameter the binding attribute MVC should use.

### Required and optional arguments

An argument is optional when the action's parameter has a default value or a nullable type, and required otherwise. The tool declares the required arguments first, as C# wants, so a nullable parameter before a required one, as in `Find(string name, int? count, OrderId orderId)`, moves behind it; MCP passes arguments by name, so this changes only the order the input schema lists them in. A client that leaves an optional argument out gets the action's default value, which the tool writes into its own signature, constants and enum members included (`OrderStatus status = OrderStatus.Open`). A struct parameter with a default value accepts `null` as well, which sends nothing, so MVC binds the action's default: `Point origin = default` becomes `Point? origin = null`, as a struct's `default` has no constant to write.

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

The tool's arguments keep the names of the action's parameters, as MCP passes them by name. The tool's own parameters and variables, `invoker`, `cancellationToken`, `route`, `queryParts`, `bodyJson` and `route<name>` for each route value, give way to them: one that an action parameter already has gets a number, such as `route2`, and a type the tool refers to, `System`, `JsonSerializer` or `McpScope`, is written as `global::...` when a parameter has its name.

## Requirements

- .NET 8.0 or later
- ASP.NET Core with controller-based endpoints (see [Limitations](#limitations))
- `ModelContextProtocol.AspNetCore` **`2.2.0`** — this is a transitive dependency of the package. **Do not add a different version of any `ModelContextProtocol.*` package to your app.** The MCP SDK makes breaking API changes between versions; a version mismatch causes a `MissingMethodException` at startup (see [Troubleshooting](#troubleshooting)). Either omit the reference entirely and let it flow transitively, or pin the exact same version.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
