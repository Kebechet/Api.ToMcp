namespace Api.ToMcp.Generator.Models;

/// <summary>
/// Types the generator recognizes by name, spelled as Roslyn's <c>ToDisplayString()</c> spells them: fully qualified,
/// a generic definition with its type parameter names. Constants rather than <c>typeof(...).FullName</c>: ASP.NET
/// Core's types cannot be referenced from a netstandard2.0 source generator, and a generic definition's display string
/// (<c>Task&lt;TResult&gt;</c>) is not its <c>FullName</c> (<c>Task`1</c>).
/// </summary>
internal static class KnownTypeNames
{
    public const string Task = "System.Threading.Tasks.Task";
    public const string TaskOfTResult = "System.Threading.Tasks.Task<TResult>";
    public const string ValueTask = "System.Threading.Tasks.ValueTask";
    public const string ValueTaskOfTResult = "System.Threading.Tasks.ValueTask<TResult>";
    public const string CancellationToken = "System.Threading.CancellationToken";
    public const string ControllerBase = "Microsoft.AspNetCore.Mvc.ControllerBase";
    public const string Controller = "Microsoft.AspNetCore.Mvc.Controller";
    public const string ActionResultOfTValue = "Microsoft.AspNetCore.Mvc.ActionResult<TValue>";
    public const string IActionResult = "Microsoft.AspNetCore.Mvc.IActionResult";
    public const string ActionResult = "Microsoft.AspNetCore.Mvc.ActionResult";
    public const string IResult = "Microsoft.AspNetCore.Http.IResult";
    public const string IConvertToActionResult = "Microsoft.AspNetCore.Mvc.Infrastructure.IConvertToActionResult";
    public const string AttributeUsage = "System.AttributeUsageAttribute";
    public const string CompilerGenerated = "System.Runtime.CompilerServices.CompilerGeneratedAttribute";
    public const string FormFile = "Microsoft.AspNetCore.Http.IFormFile";
    public const string FormFileCollection = "Microsoft.AspNetCore.Http.IFormFileCollection";
    public const string FormCollection = "Microsoft.AspNetCore.Http.IFormCollection";
}
