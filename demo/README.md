# DemoApi

Example project demonstrating Api.ToMcp usage.

## What's Included

### Controllers

**WeatherController** - All actions exposed as MCP tools
- `GET /api/weather` - Get weather forecasts
- `GET /api/weather/{city}` - Get weather for specific city
- `GET /api/weather/on/{date}` - Get the forecast for a day (a `DateOnly` route value)
- `GET /api/weather/search` - Search forecasts (a `[FromQuery]` filter object)
- `POST /api/weather` - Create weather forecast (its request DTO is described from XML docs)

**ProductsController** - Selective exposure
- `GET /api/products` - Get all products (exposed)
- `GET /api/products/{id}` - Get product by ID (exposed)
- `GET /api/products/cheapest` - Cheapest product in a category, `204 No Content` when it has none (exposed; its output schema allows null)
- `DELETE /api/products/{id}` - Delete product (excluded via `[McpIgnore]`)

**CategoriesController** - Inherited actions
- `GET /api/categories` - Get all categories. The controller declares no action: it inherits `GetAll` from the abstract `ListControllerBase<T>`, and its tool is typed with `Category` for `T`. The base class gets no tool, as MVC serves nothing under it.

### Minimal API endpoints

**TodoEndpoints** - Endpoints `Program.cs` maps with `app.MapGroup("/api").MapTodoEndpoints()`, an extension method that maps them in a group of its own, so their routes take both prefixes
- `GET /api/todos` - List the to-do items (a method group, described by its XML docs; `Ok<List<Todo>>` gives the output schema)
- `GET /api/todos/{id}` - Get an item (`Results<Ok<Todo>, NotFound>` gives the output schema `Todo`)
- `POST /api/todos` - Add an item (a lambda, named by `.WithName("CreateTodo")` and described by `.WithDescription(...)`; its request is the body, of an `internal` type, so its tool is an internal class)
- `DELETE /api/todos/{id}` - Delete an item (excluded via `[McpIgnore]` on the lambda)

### Hand-written tool

`Mcp/ServerTools.cs` is an MCP-only tool with no endpoint behind it. `AddMcpTools` registers it alongside the generated tools because the class carries `[McpServerToolType]`; its `IHostEnvironment` parameter is injected, not exposed as an argument.

## Running the Demo

```bash
cd demo/DemoApi
dotnet run
```

The API starts at `https://localhost:56720`

- **Swagger UI**: https://localhost:56720/swagger
- **MCP Endpoint**: https://localhost:56720/mcp

## Generated MCP Tools

Based on `Mcp/generator.json` configuration, these tools are generated:

| Tool Name | Source Action |
|-----------|--------------|
| `Weather_GetAll` | WeatherController.GetAll |
| `Weather_GetByCity` | WeatherController.GetByCity |
| `Weather_GetByDate` | WeatherController.GetByDate |
| `Weather_Search` | WeatherController.Search |
| `Weather_Create` | WeatherController.Create |
| `Products_GetAll` | ProductsController.GetAll |
| `Products_GetById` | ProductsController.GetById |
| `Products_GetCheapest` | ProductsController.GetCheapest |
| `Categories_GetAll` | CategoriesController.GetAll, inherited from `ListControllerBase<Category>` |
| `TodoEndpoints_GetAll` | `GET /api/todos`, handled by TodoEndpoints.GetAll |
| `TodoEndpoints_GetById` | `GET /api/todos/{id}`, handled by TodoEndpoints.GetById |
| `TodoEndpoints_CreateTodo` | `POST /api/todos`, a lambda in TodoEndpoints named by `.WithName("CreateTodo")` |
| `Server_GetEnvironment` | hand-written in `Mcp/ServerTools.cs` |

## Generator Configuration

The `Mcp/generator.json` uses `SelectedOnly` mode with explicit includes:

```json
{
  "mode": "SelectedOnly",
  "include": [
    "WeatherController",
    "ProductsController.GetAll",
    "ProductsController.GetById",
    "ProductsController.GetCheapest",
    "CategoriesController",
    "TodoEndpoints"
  ]
}
```

This demonstrates:
- Including an entire controller (`WeatherController`)
- Including specific actions (`ProductsController.GetAll`, `ProductsController.GetById`, `ProductsController.GetCheapest`)
- Including a controller whose actions are inherited, by its own name (`CategoriesController`), not its base class's
- Including every Minimal API endpoint whose handler a class declares (`TodoEndpoints`), as a controller's actions are included
- The `[McpIgnore]` attribute on the `Delete` action, and on the lambda that deletes a to-do item, as additional protection

## Testing with MCP Client

Connect your MCP client (e.g., Claude Desktop) to the MCP endpoint, which uses the Streamable HTTP transport:

```
https://localhost:56720/mcp
```

The tools will be available for the AI to call against your running API.

### Testing with Claude Web / ChatGPT Web

To test with web-based AI clients, you need to expose your local API publicly. Use [DevTunnel](https://learn.microsoft.com/en-us/azure/developer/dev-tunnels/):

```bash
devtunnel host -p 56720 --allow-anonymous
```

This gives you a public URL like `https://xxxxx.devtunnels.ms` that you can use as the MCP endpoint in web clients.

## Trying OAuth Sign-In Discovery

The demo can run with sign-in, so you can see what an MCP client that signs a user in gets: the `401` challenge, the protected resource metadata and the scope challenges. It is off by default, and the demo then runs without authentication, as above. See [OAuth Sign-In](../README.md#oauth-sign-in-claudeai-chatgpt-mcp-inspector) in the main README for what each part does.

⚠️ This is a local stand-in, not an OAuth server. The demo validates JWT bearer tokens signed with a key it makes when it starts, and a development-only endpoint, `POST /dev/token`, hands out such tokens with whatever scopes you ask for. Nobody signs in, and a client that follows the metadata to the demo's address finds no authorization server there. Use it to look at the challenges with curl or a script, not to connect Claude.ai or ChatGPT.

Turn it on with `DemoAuth:Enabled`:

```bash
DemoAuth__Enabled=true dotnet run --project demo/DemoApi --urls http://127.0.0.1:5303
```

It sets up, in `DemoAuth.cs`:

- JWT bearer authentication for tokens whose issuer is the demo's address and whose audience is its MCP endpoint, `http://127.0.0.1:5303/mcp`;
- `ScopeNames` `mcp:read`, `mcp:write` and `mcp:delete`, and a `ClaimToScopeMapper` that reads the token's `scope` claim;
- `ResourceMetadata`, whose authorization server is the demo's own address;
- in Development only, `POST /dev/token?scope=...`, which returns a token valid for an hour.

Then call the endpoint as a client would:

```bash
# 401 with WWW-Authenticate: Bearer resource_metadata="http://127.0.0.1:5303/.well-known/oauth-protected-resource/mcp"
curl -i -X POST http://127.0.0.1:5303/mcp -H "Accept: application/json, text/event-stream" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# The metadata: resource, authorization_servers and scopes_supported
curl http://127.0.0.1:5303/.well-known/oauth-protected-resource/mcp

# A read-only token
TOKEN=$(curl -s -X POST "http://127.0.0.1:5303/dev/token?scope=mcp:read" | sed 's/.*"access_token":"\([^"]*\)".*/\1/')

# A write tool with that token: 403 with error="insufficient_scope", scope="mcp:read mcp:write"
curl -i -X POST http://127.0.0.1:5303/mcp -H "Accept: application/json, text/event-stream" -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"Weather_Create","arguments":{"request":{"city":"Brno","date":"2026-11-12T00:00:00","temperatureC":21}}}}'
```

A token from `/dev/token?scope=mcp:read%20mcp:write` calls `Weather_Create`. Add `DemoAuth__AllowAnonymous=true` to list the tools without a token; a tool call without one then gets the `401` sign-in challenge with the scope it needs.
