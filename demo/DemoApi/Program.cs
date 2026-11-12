using System.Reflection;
using Api.ToMcp.Runtime;
using DemoApi;
using DemoApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);
var isDemoAuthEnabled = DemoAuth.IsEnabled(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddMcpTools(Assembly.GetExecutingAssembly());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
if (isDemoAuthEnabled)
{
    builder.AddDemoAuth();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.EnableTryItOutByDefault());
}

app.UseMcpLoopPrevention();
if (isDemoAuthEnabled)
{
    app.UseDemoAuth();
}

app.MapControllers();
app.MapGroup("/api").MapTodoEndpoints();
app.MapMcpEndpoint("mcp", allowAnonymous: DemoAuth.AllowsAnonymousCallers(app.Configuration));

app.Run();
