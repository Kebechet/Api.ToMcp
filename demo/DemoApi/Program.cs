using Api.ToMcp.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddMcpTools();

var app = builder.Build();

app.UseMcpLoopPrevention();
app.MapControllers();
app.MapMcpEndpoint("mcp");

app.Run();
