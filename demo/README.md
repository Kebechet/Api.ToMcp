# DemoApi

Example project demonstrating Api.ToMcp usage.

## What's Included

### Controllers

**WeatherController** - All actions exposed as MCP tools
- `GET /api/weather` - Get weather forecasts
- `GET /api/weather/{city}` - Get weather for specific city
- `POST /api/weather` - Create weather forecast

**ProductsController** - Selective exposure
- `GET /api/products` - Get all products (exposed)
- `GET /api/products/{id}` - Get product by ID (exposed)
- `DELETE /api/products/{id}` - Delete product (excluded via `[McpIgnore]`)

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
| `Weather_Create` | WeatherController.Create |
| `Products_GetAll` | ProductsController.GetAll |
| `Products_GetById` | ProductsController.GetById |

## Generator Configuration

The `Mcp/generator.json` uses `SelectedOnly` mode with explicit includes:

```json
{
  "mode": "SelectedOnly",
  "include": [
    "WeatherController",
    "ProductsController.GetAll",
    "ProductsController.GetById"
  ]
}
```

This demonstrates:
- Including an entire controller (`WeatherController`)
- Including specific actions (`ProductsController.GetAll`, `ProductsController.GetById`)
- The `[McpIgnore]` attribute on `Delete` action as additional protection

## Testing with MCP Client

Connect your MCP client (e.g., Claude Desktop) to the SSE endpoint:

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
