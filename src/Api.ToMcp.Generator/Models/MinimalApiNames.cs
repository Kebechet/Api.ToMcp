namespace Api.ToMcp.Generator.Models;

/// <summary>
/// The Minimal API methods, attributes and types the generator recognizes, as Roslyn's <c>ToDisplayString()</c> spells
/// them. A netstandard2.0 source generator cannot reference ASP.NET Core's types.
/// </summary>
internal static class MinimalApiNames
{
    public const string EndpointRouteBuilderExtensions = "Microsoft.AspNetCore.Builder.EndpointRouteBuilderExtensions";
    public const string MapGet = "MapGet";
    public const string MapPost = "MapPost";
    public const string MapPut = "MapPut";
    public const string MapPatch = "MapPatch";
    public const string MapDelete = "MapDelete";
    public const string MapMethods = "MapMethods";
    public const string MapGroup = "MapGroup";

    /// <summary>
    /// The position of the route pattern among the parameters of every <c>Map*</c> method, after the builder.
    /// </summary>
    public const int PatternParameter = 1;

    /// <summary>
    /// The position of <c>MapMethods</c>' HTTP methods, between the pattern and the handler.
    /// </summary>
    public const int HttpMethodsParameter = 2;

    public const string EndpointRoutingExtensions = "Microsoft.AspNetCore.Builder.EndpointRoutingApplicationBuilderExtensions";

    /// <summary>
    /// The older <c>app.UseEndpoints(endpoints =&gt; ...)</c>, whose callback gets the app's root route builder.
    /// </summary>
    public const string UseEndpoints = "UseEndpoints";

    public const string WebApplication = "Microsoft.AspNetCore.Builder.WebApplication";

    /// <summary>
    /// <c>HttpMethods</c>, whose fields, not constants, an app passes to <c>MapMethods</c>: the field <c>Get</c> holds
    /// <c>GET</c>, and so on.
    /// </summary>
    public const string HttpMethods = "Microsoft.AspNetCore.Http.HttpMethods";

    public const string RoutingConventions = "Microsoft.AspNetCore.Builder.RoutingEndpointConventionBuilderExtensions";
    public const string OpenApiConventions = "Microsoft.AspNetCore.Http.OpenApiRouteHandlerBuilderExtensions";
    public const string WithName = "WithName";
    public const string WithDescription = "WithDescription";
    public const string WithSummary = "WithSummary";

    /// <summary>
    /// The namespace of ASP.NET Core's endpoint conventions, such as <c>RequireAuthorization</c> or <c>WithTags</c>,
    /// which return the builder they are called on.
    /// </summary>
    public const string AspNetCoreNamespacePrefix = "Microsoft.AspNetCore.";

    public const string EndpointNameAttribute = "Microsoft.AspNetCore.Routing.EndpointNameAttribute";
    public const string EndpointSummaryAttribute = "Microsoft.AspNetCore.Http.EndpointSummaryAttribute";
    public const string EndpointDescriptionAttribute = "Microsoft.AspNetCore.Http.EndpointDescriptionAttribute";
    public const string DescriptionAttribute = "System.ComponentModel.DescriptionAttribute";
    public const string AsParametersAttribute = "Microsoft.AspNetCore.Http.AsParametersAttribute";
    public const string FromKeyedServicesAttribute = "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute";

    public const string FromRouteMetadata = "Microsoft.AspNetCore.Http.Metadata.IFromRouteMetadata";
    public const string FromQueryMetadata = "Microsoft.AspNetCore.Http.Metadata.IFromQueryMetadata";
    public const string FromHeaderMetadata = "Microsoft.AspNetCore.Http.Metadata.IFromHeaderMetadata";
    public const string FromBodyMetadata = "Microsoft.AspNetCore.Http.Metadata.IFromBodyMetadata";
    public const string FromFormMetadata = "Microsoft.AspNetCore.Http.Metadata.IFromFormMetadata";
    public const string FromServiceMetadata = "Microsoft.AspNetCore.Http.Metadata.IFromServiceMetadata";

    /// <summary>
    /// The <c>Name</c> of <c>IFromQueryMetadata</c> and the others, which renames the key a value is bound from.
    /// </summary>
    public const string MetadataNamePropertyName = "Name";

    public const string HttpContext = "Microsoft.AspNetCore.Http.HttpContext";
    public const string HttpRequest = "Microsoft.AspNetCore.Http.HttpRequest";
    public const string HttpResponse = "Microsoft.AspNetCore.Http.HttpResponse";
    public const string ClaimsPrincipal = "System.Security.Claims.ClaimsPrincipal";
    public const string Stream = "System.IO.Stream";
    public const string PipeReader = "System.IO.Pipelines.PipeReader";
    public const string BindableFromHttpContext = "Microsoft.AspNetCore.Http.IBindableFromHttpContext<TSelf>";
    public const string BindAsyncMethodName = "BindAsync";
    public const string StringValues = "Microsoft.Extensions.Primitives.StringValues";

    /// <summary>
    /// The type a tool declares for a <c>StringValues</c> parameter, which <c>System.Text.Json</c> cannot fill.
    /// </summary>
    public const string StringValuesToolType = "string[]";

    /// <summary>
    /// Entity Framework's base class, whose subclasses an app registers as services and takes as handler parameters.
    /// </summary>
    public const string DbContext = "Microsoft.EntityFrameworkCore.DbContext";

    public const string JsonNode = "System.Text.Json.Nodes.JsonNode";

    public const string HttpClient = "System.Net.Http.HttpClient";
    public const string UserManager = "Microsoft.AspNetCore.Identity.UserManager<TUser>";
    public const string SignInManager = "Microsoft.AspNetCore.Identity.SignInManager<TUser>";
    public const string RoleManager = "Microsoft.AspNetCore.Identity.RoleManager<TRole>";

    /// <summary>
    /// Classes of the framework that an app registers as services and takes in a handler. Any other class of the
    /// framework may be a body, such as <c>JsonPatchDocument&lt;T&gt;</c>, <c>ProblemDetails</c> or Identity's
    /// <c>LoginRequest</c>.
    /// </summary>
    public static readonly string[] FrameworkServiceClasses =
    {
        HttpClient,
        UserManager,
        SignInManager,
        RoleManager
    };

    public const string ListInterface = "System.Collections.Generic.IList<T>";
    public const string CollectionInterface = "System.Collections.Generic.ICollection<T>";
    public const string ReadOnlyListInterface = "System.Collections.Generic.IReadOnlyList<T>";
    public const string ReadOnlyCollectionInterface = "System.Collections.Generic.IReadOnlyCollection<T>";
    public const string DictionaryInterface = "System.Collections.Generic.IDictionary<TKey, TValue>";
    public const string ReadOnlyDictionaryInterface = "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>";
    public const string SetInterface = "System.Collections.Generic.ISet<T>";

    /// <summary>
    /// The generic collection interfaces <c>System.Text.Json</c> reads a body into, which the container does not resolve
    /// as services. It resolves <c>IEnumerable&lt;T&gt;</c> for any <c>T</c>, so that one is a service.
    /// </summary>
    public static readonly string[] BodyCollectionInterfaces =
    {
        ListInterface,
        CollectionInterface,
        ReadOnlyListInterface,
        ReadOnlyCollectionInterface,
        DictionaryInterface,
        ReadOnlyDictionaryInterface,
        SetInterface
    };

    public const string HttpResultsNamespace = "Microsoft.AspNetCore.Http.HttpResults";

    /// <summary>
    /// <c>Results&lt;TResult1, ...&gt;</c>, the union of the typed results a handler declares it returns.
    /// </summary>
    public const string ResultsUnion = "Results";

    /// <summary>
    /// The typed results of a success, by name, each with its status; the generic form carries a body of its type
    /// argument, the other none.
    /// </summary>
    public const string Ok = "Ok";
    public const string Created = "Created";
    public const string CreatedAtRoute = "CreatedAtRoute";
    public const string Accepted = "Accepted";
    public const string AcceptedAtRoute = "AcceptedAtRoute";
    public const string NoContent = "NoContent";

    public const string BadRequest = "BadRequest";
    public const string NotFound = "NotFound";
    public const string Conflict = "Conflict";
    public const string UnprocessableEntity = "UnprocessableEntity";
    public const string ValidationProblem = "ValidationProblem";
    public const string ProblemHttpResult = "ProblemHttpResult";
    public const string UnauthorizedHttpResult = "UnauthorizedHttpResult";
    public const string ForbidHttpResult = "ForbidHttpResult";
    public const string ChallengeHttpResult = "ChallengeHttpResult";
    public const string InternalServerError = "InternalServerError";

    /// <summary>
    /// The typed results of an error, which a tool reports as one and which so never need a body.
    /// </summary>
    public static readonly string[] ErrorResults =
    {
        BadRequest,
        NotFound,
        Conflict,
        UnprocessableEntity,
        ValidationProblem,
        ProblemHttpResult,
        UnauthorizedHttpResult,
        ForbidHttpResult,
        ChallengeHttpResult,
        InternalServerError
    };
}
