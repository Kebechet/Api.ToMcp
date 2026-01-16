namespace Api.ToMcp.Abstractions;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class McpIgnoreAttribute : Attribute
{
}
