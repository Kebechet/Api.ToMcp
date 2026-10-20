using System.Collections.Generic;
using System.Collections.Immutable;
using Api.ToMcp.Generator.Emitting;
using Api.ToMcp.Generator.Models;
using Xunit;

namespace Api.ToMcp.Generator.Tests.Emitting;

public class ToolClassEmitterTests
{
    private const string Formatter = "global::Api.ToMcp.Runtime.Services.McpRequestValueFormatter";

    #region RouteContainsParameter Tests

    [Theory]
    [InlineData("/api/products/{id}", "id", true)]
    [InlineData("/api/products/{id}", "name", false)]
    [InlineData("/api/products/{id:guid}", "id", true)]
    [InlineData("/api/products/{id:int}", "id", true)]
    [InlineData("/api/products/{id:long}", "id", true)]
    [InlineData("/api/products/{id:alpha}", "id", true)]
    [InlineData("/api/products/{id:datetime}", "id", true)]
    [InlineData("/api/products/{id?}", "id", true)]
    [InlineData("/api/products/{id:guid?}", "id", true)]
    [InlineData("/api/{category}/products/{id:guid}", "category", true)]
    [InlineData("/api/{category}/products/{id:guid}", "id", true)]
    [InlineData("/api/products", "id", false)]
    public void RouteContainsParameter_DetectsParametersCorrectly(string route, string paramName, bool expected)
    {
        var result = ToolClassEmitter.RouteContainsParameter(route, paramName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void RouteContainsParameter_IgnoresCase_AsMvcRouteValuesDo()
    {
        Assert.True(ToolClassEmitter.RouteContainsParameter("/api/{Id}", "Id"));
        Assert.True(ToolClassEmitter.RouteContainsParameter("/api/{Id}", "id"));
        Assert.True(ToolClassEmitter.RouteContainsParameter("/api/{ID:int}", "id"));
    }

    #endregion

    #region ConvertRouteTemplate Tests

    [Fact]
    public void ConvertRouteTemplate_ConvertsSimpleParameter()
    {
        var routeParams = new List<ParameterInfoModel>
        {
            new() { Name = "id", Type = "int" }
        };

        var result = ToolClassEmitter.ConvertRouteTemplate("/api/products/{id}", routeParams);
        Assert.Equal("/api/products/{routeid}", result);
    }

    [Fact]
    public void ConvertRouteTemplate_ConvertsGuidConstraint()
    {
        var routeParams = new List<ParameterInfoModel>
        {
            new() { Name = "id", Type = "System.Guid" }
        };

        var result = ToolClassEmitter.ConvertRouteTemplate("/api/products/{id:guid}", routeParams);
        Assert.Equal("/api/products/{routeid}", result);
    }

    [Fact]
    public void ConvertRouteTemplate_ConvertsIntConstraint()
    {
        var routeParams = new List<ParameterInfoModel>
        {
            new() { Name = "id", Type = "int" }
        };

        var result = ToolClassEmitter.ConvertRouteTemplate("/api/products/{id:int}", routeParams);
        Assert.Equal("/api/products/{routeid}", result);
    }

    [Fact]
    public void ConvertRouteTemplate_ConvertsOptionalParameter()
    {
        var routeParams = new List<ParameterInfoModel>
        {
            new() { Name = "id", Type = "int?" }
        };

        var result = ToolClassEmitter.ConvertRouteTemplate("/api/products/{id?}", routeParams);
        Assert.Equal("/api/products/{routeid}", result);
    }

    [Fact]
    public void ConvertRouteTemplate_ConvertsMultipleParameters()
    {
        var routeParams = new List<ParameterInfoModel>
        {
            new() { Name = "category", Type = "string" },
            new() { Name = "id", Type = "System.Guid" }
        };

        var result = ToolClassEmitter.ConvertRouteTemplate("/api/{category}/products/{id:guid}", routeParams);
        Assert.Equal("/api/{routecategory}/products/{routeid}", result);
    }

    [Fact]
    public void ConvertRouteTemplate_HandlesCustomConstraint()
    {
        var routeParams = new List<ParameterInfoModel>
        {
            new() { Name = "slug", Type = "string" }
        };

        var result = ToolClassEmitter.ConvertRouteTemplate("/api/posts/{slug:regex(^[a-z-]+$)}", routeParams);
        Assert.Equal("/api/posts/{routeslug}", result);
    }

    [Fact]
    public void ConvertRouteTemplate_LeavesUnmatchedParamsUntouched()
    {
        var routeParams = new List<ParameterInfoModel>
        {
            new() { Name = "id", Type = "int" }
        };

        var result = ToolClassEmitter.ConvertRouteTemplate("/api/{controller}/{id}", routeParams);
        Assert.Equal("/api/{controller}/{routeid}", result);
    }

    #endregion

    #region Emit Integration Tests

    [Fact]
    public void Emit_GeneratesCorrectCodeForGuidRouteParameter()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/{id:guid}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "id",
                Type = "System.Guid",
                Source = ParameterSourceModel.Auto,
                IsNullable = false
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"var routeid = System.Uri.EscapeDataString({Formatter}.Format(id));", result);
        Assert.Contains("var route = $\"/api/products/{routeid}\";", result);
        Assert.DoesNotContain("{id:guid}", result);
        Assert.DoesNotContain(".ToString()", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForIntRouteParameter()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "OrdersController",
            HttpMethod = "GET",
            RouteTemplate = "/api/orders/{id:int}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "id",
                Type = "int",
                Source = ParameterSourceModel.Auto,
                IsNullable = false
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"var routeid = System.Uri.EscapeDataString({Formatter}.Format(id));", result);
        Assert.Contains("var route = $\"/api/orders/{routeid}\";", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForQueryParameter()
    {
        var action = new ActionInfoModel
        {
            Name = "GetAll",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "category",
                Type = "string",
                Source = ParameterSourceModel.Auto,
                IsSimpleType = true,
                IsNullable = true,
                HasDefaultValue = true,
                DefaultValueExpression = "null"
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"{Formatter}.AddQueryValue(queryParts, \"category\", category);", result);
    }

    [Fact]
    public void Emit_PassesFromQueryObjectToTheFormatter_NotItsToString()
    {
        var action = new ActionInfoModel
        {
            Name = "Search",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/search",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "filter",
                Type = "ProductFilter",
                Source = ParameterSourceModel.Query,
                IsNullable = false
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"{Formatter}.AddQueryValue(queryParts, \"filter\", filter);", result);
        Assert.DoesNotContain(".ToString()", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForNullableRouteParameter()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "id",
                Type = "string",
                Source = ParameterSourceModel.Auto,
                IsNullable = true
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"var routeid = System.Uri.EscapeDataString({Formatter}.Format(id));", result);
    }

    [Fact]
    public void Emit_DoesNotAddRouteParamAsQueryParam()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/{id:guid}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "id",
                Type = "System.Guid",
                Source = ParameterSourceModel.Auto,
                IsNullable = false
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.DoesNotContain("AddQueryValue(queryParts, \"id\"", result);
        Assert.DoesNotContain("var queryParts", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectToolName()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty
        };

        var config = new GeneratorConfigModel
        {
            Naming = new NamingConfigModel
            {
                ToolNameFormat = "{Controller}_{Action}",
                RemoveControllerSuffix = true
            }
        };

        var result = ToolClassEmitter.Emit(action, config);
        Assert.Contains("[McpServerTool(Name = \"Products_GetById\", ", result);
    }

    [Fact]
    public void Emit_KeepsControllerSuffix_WhenRemoveControllerSuffixIsFalse()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty
        };

        var config = new GeneratorConfigModel
        {
            Naming = new NamingConfigModel
            {
                ToolNameFormat = "{Controller}_{Action}",
                RemoveControllerSuffix = false
            }
        };

        var result = ToolClassEmitter.Emit(action, config);
        Assert.Contains("[McpServerTool(Name = \"ProductsController_GetById\", ", result);
    }

    /// <summary>
    /// The two patterns differ for every pair of hints, so emitting one hint's value under another's name fails one of
    /// them; each runs on both emit paths, with and without an output schema.
    /// </summary>
    [Theory]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public void Emit_WritesEachAnnotationUnderItsOwnName_OnBothEmitPaths(bool hasOutputSchema, bool isReadOnly, bool isDestructive, bool isIdempotent, bool isOpenWorld)
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            OutputSchemaType = hasOutputSchema ? "global::TestApi.Product" : null,
            Annotations = new ToolAnnotationsModel
            {
                IsReadOnly = isReadOnly,
                IsDestructive = isDestructive,
                IsIdempotent = isIdempotent,
                IsOpenWorld = isOpenWorld
            }
        };

        var result = ToolClassEmitter.Emit(action, new GeneratorConfigModel());

        var expectedAnnotations = $"ReadOnly = {FormatBool(isReadOnly)}, Destructive = {FormatBool(isDestructive)}, Idempotent = {FormatBool(isIdempotent)}, OpenWorld = {FormatBool(isOpenWorld)})]";
        var expectedAttribute = hasOutputSchema
            ? $"[McpServerTool(Name = \"Products_GetById\", UseStructuredContent = true, OutputSchemaType = typeof(global::TestApi.Product), {expectedAnnotations}"
            : $"[McpServerTool(Name = \"Products_GetById\", {expectedAnnotations}";
        Assert.Contains(expectedAttribute, result);
    }

    [Fact]
    public void Emit_ActionWhoseAnnotationsWereNeverDerived_StatesTheSpecDefaults()
    {
        // A path that skips deriving the hints must not advertise a plausible "safe" tuple: the spec's defaults are the
        // cautious ones, so a DELETE that slipped through still asks for confirmation.
        var action = new ActionInfoModel
        {
            Name = "Delete",
            ControllerName = "ProductsController",
            HttpMethod = "DELETE",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty
        };

        var result = ToolClassEmitter.Emit(action, new GeneratorConfigModel());

        Assert.Contains("[McpServerTool(Name = \"Products_Delete\", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]", result);
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    [Fact]
    public void Emit_UsesCustomToolName()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            CustomToolName = "custom_get_product"
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);
        Assert.Contains("[McpServerTool(Name = \"custom_get_product\", ", result);
    }

    [Fact]
    public void Emit_GeneratesValidCodeForParameterlessMethod()
    {
        var action = new ActionInfoModel
        {
            Name = "GetSecret",
            ControllerName = "ValuesController",
            HttpMethod = "GET",
            RouteTemplate = "/api/values/secret",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        // The injected cancellation token is always the last parameter (so invoker is now
        // followed by a comma), and there must be no dangling comma before the close paren.
        Assert.Contains("IMcpHttpInvoker invoker,", result);
        Assert.Contains("System.Threading.CancellationToken cancellationToken = default", result);
        Assert.DoesNotContain("cancellationToken = default,", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForPostWithBody()
    {
        var action = new ActionInfoModel
        {
            Name = "Create",
            ControllerName = "ProductsController",
            HttpMethod = "POST",
            RouteTemplate = "/api/products",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "request",
                Type = "CreateProductRequest",
                Source = ParameterSourceModel.Body,
                IsNullable = false
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("var bodyJson = JsonSerializer.Serialize(request);", result);
        Assert.Contains("return await invoker.PostAsync(route, bodyJson, cancellationToken);", result);
    }

    #endregion

    #region PUT Method Tests

    [Fact]
    public void Emit_GeneratesCorrectCodeForPutWithBody()
    {
        var action = new ActionInfoModel
        {
            Name = "Update",
            ControllerName = "ProductsController",
            HttpMethod = "PUT",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray.Create(
                new ParameterInfoModel
                {
                    Name = "id",
                    Type = "System.Guid",
                    Source = ParameterSourceModel.Route,
                    IsNullable = false
                },
                new ParameterInfoModel
                {
                    Name = "request",
                    Type = "UpdateProductRequest",
                    Source = ParameterSourceModel.Body,
                    IsNullable = false
                }),
            RequiredScope = McpScopeModel.Write
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("var bodyJson = JsonSerializer.Serialize(request);", result);
        Assert.Contains("return await invoker.PutAsync(route, bodyJson, cancellationToken);", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForPutWithoutBody()
    {
        var action = new ActionInfoModel
        {
            Name = "Activate",
            ControllerName = "ProductsController",
            HttpMethod = "PUT",
            RouteTemplate = "/api/products/{id}/activate",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "id",
                Type = "System.Guid",
                Source = ParameterSourceModel.Route,
                IsNullable = false
            }),
            RequiredScope = McpScopeModel.Write
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("return await invoker.PutAsync(route, null, cancellationToken);", result);
    }

    #endregion

    #region PATCH Method Tests

    [Fact]
    public void Emit_GeneratesCorrectCodeForPatchWithBody()
    {
        var action = new ActionInfoModel
        {
            Name = "PartialUpdate",
            ControllerName = "ProductsController",
            HttpMethod = "PATCH",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray.Create(
                new ParameterInfoModel
                {
                    Name = "id",
                    Type = "System.Guid",
                    Source = ParameterSourceModel.Route,
                    IsNullable = false
                },
                new ParameterInfoModel
                {
                    Name = "patchDoc",
                    Type = "JsonPatchDocument",
                    Source = ParameterSourceModel.Body,
                    IsNullable = false
                }),
            RequiredScope = McpScopeModel.Write
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("var bodyJson = JsonSerializer.Serialize(patchDoc);", result);
        Assert.Contains("return await invoker.PatchAsync(route, bodyJson, cancellationToken);", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForPatchWithoutBody()
    {
        var action = new ActionInfoModel
        {
            Name = "Touch",
            ControllerName = "ProductsController",
            HttpMethod = "PATCH",
            RouteTemplate = "/api/products/{id}/touch",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "id",
                Type = "int",
                Source = ParameterSourceModel.Route,
                IsNullable = false
            }),
            RequiredScope = McpScopeModel.Write
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("return await invoker.PatchAsync(route, null, cancellationToken);", result);
    }

    #endregion

    #region DELETE Method Tests

    [Fact]
    public void Emit_GeneratesCorrectCodeForDelete()
    {
        var action = new ActionInfoModel
        {
            Name = "Delete",
            ControllerName = "ProductsController",
            HttpMethod = "DELETE",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "id",
                Type = "System.Guid",
                Source = ParameterSourceModel.Route,
                IsNullable = false
            }),
            RequiredScope = McpScopeModel.Delete
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("return await invoker.DeleteAsync(route, cancellationToken);", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForDeleteWithMultipleRouteParams()
    {
        var action = new ActionInfoModel
        {
            Name = "DeleteItem",
            ControllerName = "OrdersController",
            HttpMethod = "DELETE",
            RouteTemplate = "/api/orders/{orderId}/items/{itemId}",
            Parameters = ImmutableArray.Create(
                new ParameterInfoModel
                {
                    Name = "orderId",
                    Type = "int",
                    Source = ParameterSourceModel.Route,
                    IsNullable = false
                },
                new ParameterInfoModel
                {
                    Name = "itemId",
                    Type = "int",
                    Source = ParameterSourceModel.Route,
                    IsNullable = false
                }),
            RequiredScope = McpScopeModel.Delete
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"var routeorderId = System.Uri.EscapeDataString({Formatter}.Format(orderId));", result);
        Assert.Contains($"var routeitemId = System.Uri.EscapeDataString({Formatter}.Format(itemId));", result);
        Assert.Contains("return await invoker.DeleteAsync(route, cancellationToken);", result);
    }

    #endregion

    #region Scope-Related Tests

    [Fact]
    public void Emit_GeneratesBeforeInvokeAsyncForGetMethod()
    {
        var action = new ActionInfoModel
        {
            Name = "GetAll",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Read
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Read, cancellationToken);", result);
    }

    [Fact]
    public void Emit_DeclaresInjectedCancellationTokenParameter_AndForwardsIt()
    {
        var action = new ActionInfoModel
        {
            Name = "GetAll",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Read
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        // The MCP server injects the token; it must be declared with a default and forwarded.
        Assert.Contains("System.Threading.CancellationToken cancellationToken = default", result);
        Assert.Contains("return await invoker.GetAsync(route, cancellationToken);", result);
    }

    [Fact]
    public void Emit_GeneratesBeforeInvokeAsyncForPostMethod()
    {
        var action = new ActionInfoModel
        {
            Name = "Create",
            ControllerName = "ProductsController",
            HttpMethod = "POST",
            RouteTemplate = "/api/products",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Write
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Write, cancellationToken);", result);
    }

    [Fact]
    public void Emit_GeneratesBeforeInvokeAsyncForPutMethod()
    {
        var action = new ActionInfoModel
        {
            Name = "Update",
            ControllerName = "ProductsController",
            HttpMethod = "PUT",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Write
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Write, cancellationToken);", result);
    }

    [Fact]
    public void Emit_GeneratesBeforeInvokeAsyncForPatchMethod()
    {
        var action = new ActionInfoModel
        {
            Name = "PartialUpdate",
            ControllerName = "ProductsController",
            HttpMethod = "PATCH",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Write
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Write, cancellationToken);", result);
    }

    [Fact]
    public void Emit_GeneratesBeforeInvokeAsyncForDeleteMethod()
    {
        var action = new ActionInfoModel
        {
            Name = "Delete",
            ControllerName = "ProductsController",
            HttpMethod = "DELETE",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Delete
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Delete, cancellationToken);", result);
    }

    [Fact]
    public void Emit_IncludesScopeNamespaceUsing()
    {
        var action = new ActionInfoModel
        {
            Name = "GetAll",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Read
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("using Api.ToMcp.Abstractions.Scopes;", result);
    }

    [Fact]
    public void Emit_BeforeInvokeAsyncComesBeforeHttpCall()
    {
        var action = new ActionInfoModel
        {
            Name = "GetAll",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Read
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        var beforeInvokeIndex = result.IndexOf("BeforeInvokeAsync");
        var getAsyncIndex = result.IndexOf("GetAsync");

        Assert.True(beforeInvokeIndex < getAsyncIndex, "BeforeInvokeAsync should come before GetAsync");
    }

    #endregion

    #region Emit Override Class Name Tests

    [Fact]
    public void Emit_WithOverrideClassName_UsesOverriddenName()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Read
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config, "ProductsController_GetByIdTool_2");

        Assert.Contains("public static class ProductsController_GetByIdTool_2", result);
    }

    [Fact]
    public void Emit_WithoutOverrideClassName_UsesDefaultToolClassName()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ProductsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/products/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Read
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("public static class ProductsController_GetByIdTool", result);
    }

    [Fact]
    public void Emit_WithNullOverrideClassName_UsesDefaultToolClassName()
    {
        var action = new ActionInfoModel
        {
            Name = "Delete",
            ControllerName = "OrdersController",
            HttpMethod = "DELETE",
            RouteTemplate = "/api/orders/{id}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Delete
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config, null);

        Assert.Contains("public static class OrdersController_DeleteTool", result);
    }

    #endregion

    #region EmitRegistration Duplicate Handling Tests

    [Fact]
    public void EmitRegistration_WithNoDuplicates_GeneratesOriginalNames()
    {
        var actions = ImmutableArray.Create(
            CreateActionWithToolClassName("GetAll", "ProductsController"),
            CreateActionWithToolClassName("GetById", "ProductsController"),
            CreateActionWithToolClassName("Create", "OrdersController")
        );

        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "ProductsController_GetAllTool", 1 },
            { "ProductsController_GetByIdTool", 1 },
            { "OrdersController_CreateTool", 1 }
        };

        var result = ToolClassEmitter.EmitRegistration(actions, seenNames);

        Assert.Contains("typeof(ProductsController_GetAllTool),", result);
        Assert.Contains("typeof(ProductsController_GetByIdTool),", result);
        Assert.Contains("typeof(OrdersController_CreateTool),", result);
        Assert.DoesNotContain("_2", result);
        Assert.DoesNotContain("_3", result);
    }

    [Fact]
    public void EmitRegistration_WithDuplicateBaseNames_GeneratesUniqueTypeReferences()
    {
        var actions = ImmutableArray.Create(
            CreateActionWithToolClassName("GetById", "ProductsController"),
            CreateActionWithToolClassName("GetById", "ProductsController"),
            CreateActionWithToolClassName("GetById", "ProductsController")
        );

        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "ProductsController_GetByIdTool", 3 }
        };

        var result = ToolClassEmitter.EmitRegistration(actions, seenNames);

        Assert.Contains("typeof(ProductsController_GetByIdTool),", result);
        Assert.Contains("typeof(ProductsController_GetByIdTool_2),", result);
        Assert.Contains("typeof(ProductsController_GetByIdTool_3),", result);
    }

    [Fact]
    public void EmitRegistration_WithMixedDuplicates_GeneratesCorrectNames()
    {
        var actions = ImmutableArray.Create(
            CreateActionWithToolClassName("GetAll", "ProductsController"),
            CreateActionWithToolClassName("GetById", "ProductsController"),
            CreateActionWithToolClassName("GetById", "ProductsController")
        );

        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "ProductsController_GetAllTool", 1 },
            { "ProductsController_GetByIdTool", 2 }
        };

        var result = ToolClassEmitter.EmitRegistration(actions, seenNames);

        Assert.Contains("typeof(ProductsController_GetAllTool),", result);
        Assert.Contains("typeof(ProductsController_GetByIdTool),", result);
        Assert.Contains("typeof(ProductsController_GetByIdTool_2),", result);
        Assert.DoesNotContain("ProductsController_GetAllTool_2", result);
    }

    [Fact]
    public void EmitRegistration_GeneratesMcpToolsInfoClass()
    {
        var actions = ImmutableArray.Create(
            CreateActionWithToolClassName("GetAll", "ProductsController")
        );

        var seenNames = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "ProductsController_GetAllTool", 1 }
        };

        var result = ToolClassEmitter.EmitRegistration(actions, seenNames);

        Assert.Contains("public static class McpToolsInfo", result);
        Assert.Contains("public static readonly System.Type[] ToolTypes", result);
    }

    private static ActionInfoModel CreateActionWithToolClassName(string actionName, string controllerName)
    {
        return new ActionInfoModel
        {
            Name = actionName,
            ControllerName = controllerName,
            HttpMethod = "GET",
            RouteTemplate = $"/api/{controllerName.ToLower().Replace("controller", "")}/{actionName.ToLower()}",
            Parameters = ImmutableArray<ParameterInfoModel>.Empty,
            RequiredScope = McpScopeModel.Read
        };
    }

    #endregion

    #region DateOnly/TimeOnly Value Type Tests

    [Fact]
    public void Emit_GeneratesCorrectCodeForDateOnlyRouteParameter()
    {
        var action = new ActionInfoModel
        {
            Name = "GetByDate",
            ControllerName = "ReportsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/reports/{date}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "date",
                Type = "System.DateOnly",
                Source = ParameterSourceModel.Route,
                IsNullable = false
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"var routedate = System.Uri.EscapeDataString({Formatter}.Format(date));", result);
        Assert.DoesNotContain(".ToString()", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForTimeOnlyRouteParameter()
    {
        var action = new ActionInfoModel
        {
            Name = "GetByTime",
            ControllerName = "ScheduleController",
            HttpMethod = "GET",
            RouteTemplate = "/api/schedule/{time}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "time",
                Type = "System.TimeOnly",
                Source = ParameterSourceModel.Route,
                IsNullable = false
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"var routetime = System.Uri.EscapeDataString({Formatter}.Format(time));", result);
        Assert.DoesNotContain(".ToString()", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForNullableDateOnlyRouteParameter()
    {
        var action = new ActionInfoModel
        {
            Name = "GetByDate",
            ControllerName = "ReportsController",
            HttpMethod = "GET",
            RouteTemplate = "/api/reports/{date?}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "date",
                Type = "System.DateOnly?",
                Source = ParameterSourceModel.Route,
                IsNullable = true
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"var routedate = System.Uri.EscapeDataString({Formatter}.Format(date));", result);
    }

    #endregion

    #region Complex Type Route Placeholder Tests

    [Fact]
    public void Emit_GeneratesCorrectCodeForRouteFromComplexTypeProperty()
    {
        var action = new ActionInfoModel
        {
            Name = "SaveValues",
            ControllerName = "MeasurementController",
            HttpMethod = "POST",
            RouteTemplate = "/api/measurement/{measurementTypeId}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "request",
                Type = "MeasurementValuesRequest",
                Source = ParameterSourceModel.Body,
                IsNullable = false,
                Properties = ImmutableArray.Create(
                    new PropertyInfoModel { Name = "MeasurementTypeId", Type = "System.Guid", IsNullable = false },
                    new PropertyInfoModel { Name = "Value", Type = "decimal", IsNullable = false }
                )
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"var routerequest_MeasurementTypeId = System.Uri.EscapeDataString({Formatter}.Format(request.MeasurementTypeId));", result);
        Assert.Contains("{routerequest_MeasurementTypeId}", result);
        Assert.DoesNotContain("{measurementTypeId}", result);
    }

    [Fact]
    public void Emit_GeneratesCorrectCodeForMultipleRouteParamsFromComplexType()
    {
        var action = new ActionInfoModel
        {
            Name = "GetExercise",
            ControllerName = "ExerciseController",
            HttpMethod = "GET",
            RouteTemplate = "/api/exercises/{exerciseId}/sets/{setId}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "request",
                Type = "ExerciseSetRequest",
                Source = ParameterSourceModel.Route,
                IsNullable = false,
                Properties = ImmutableArray.Create(
                    new PropertyInfoModel { Name = "ExerciseId", Type = "int", IsNullable = false },
                    new PropertyInfoModel { Name = "SetId", Type = "int", IsNullable = false }
                )
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"{Formatter}.Format(request.ExerciseId)", result);
        Assert.Contains($"{Formatter}.Format(request.SetId)", result);
    }

    [Fact]
    public void Emit_MixesDirectParamsAndComplexTypeProperties()
    {
        var action = new ActionInfoModel
        {
            Name = "UpdateItem",
            ControllerName = "OrderController",
            HttpMethod = "PUT",
            RouteTemplate = "/api/orders/{orderId}/items/{itemId}",
            Parameters = ImmutableArray.Create(
                new ParameterInfoModel
                {
                    Name = "orderId",
                    Type = "System.Guid",
                    Source = ParameterSourceModel.Route,
                    IsNullable = false
                },
                new ParameterInfoModel
                {
                    Name = "request",
                    Type = "UpdateItemRequest",
                    Source = ParameterSourceModel.Body,
                    IsNullable = false,
                    Properties = ImmutableArray.Create(
                        new PropertyInfoModel { Name = "ItemId", Type = "int", IsNullable = false },
                        new PropertyInfoModel { Name = "Quantity", Type = "int", IsNullable = false }
                    )
                })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"var routeorderId = System.Uri.EscapeDataString({Formatter}.Format(orderId));", result);
        Assert.Contains($"{Formatter}.Format(request.ItemId)", result);
    }

    [Fact]
    public void Emit_HandlesNullablePropertyFromComplexType()
    {
        var action = new ActionInfoModel
        {
            Name = "GetData",
            ControllerName = "DataController",
            HttpMethod = "GET",
            RouteTemplate = "/api/data/{categoryId}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "filter",
                Type = "DataFilter",
                Source = ParameterSourceModel.Route,
                IsNullable = false,
                Properties = ImmutableArray.Create(
                    new PropertyInfoModel { Name = "CategoryId", Type = "int?", IsNullable = true }
                )
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"{Formatter}.Format(filter.CategoryId)", result);
    }

    [Fact]
    public void Emit_CaseInsensitiveMatchForRoutePlaceholder()
    {
        var action = new ActionInfoModel
        {
            Name = "GetById",
            ControllerName = "ItemController",
            HttpMethod = "GET",
            RouteTemplate = "/api/items/{ItemId}",
            Parameters = ImmutableArray.Create(new ParameterInfoModel
            {
                Name = "request",
                Type = "ItemRequest",
                Source = ParameterSourceModel.Route,
                IsNullable = false,
                Properties = ImmutableArray.Create(
                    new PropertyInfoModel { Name = "itemId", Type = "int", IsNullable = false }
                )
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains($"{Formatter}.Format(request.itemId)", result);
        Assert.DoesNotContain("{ItemId}", result);
    }

    #endregion
}
