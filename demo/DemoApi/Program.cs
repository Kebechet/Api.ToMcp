using System.Reflection;
using Api.ToMcp.Runtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddMcpTools(Assembly.GetExecutingAssembly());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.EnableTryItOutByDefault());
}

app.UseMcpLoopPrevention();
app.MapControllers();
app.MapMcpEndpoint("mcp");

app.Run();
