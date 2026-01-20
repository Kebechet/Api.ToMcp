using System.Collections.Immutable;
using Api.ToMcp.Generator.Emitting;
using Api.ToMcp.Generator.Models;
using Xunit;

namespace Api.ToMcp.Generator.Tests.Emitting;

public class ToolClassEmitterTests
{
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
    public void RouteContainsParameter_IsCaseSensitive()
    {
        Assert.True(ToolClassEmitter.RouteContainsParameter("/api/{Id}", "Id"));
        Assert.False(ToolClassEmitter.RouteContainsParameter("/api/{Id}", "id"));
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

    #region IsValueType Tests

    [Theory]
    [InlineData("int", true)]
    [InlineData("Int32", true)]
    [InlineData("System.Int32", true)]
    [InlineData("long", true)]
    [InlineData("System.Int64", true)]
    [InlineData("bool", true)]
    [InlineData("System.Boolean", true)]
    [InlineData("double", true)]
    [InlineData("decimal", true)]
    [InlineData("Guid", true)]
    [InlineData("System.Guid", true)]
    [InlineData("DateTime", true)]
    [InlineData("System.DateTime", true)]
    [InlineData("string", false)]
    [InlineData("String", false)]
    [InlineData("System.String", false)]
    [InlineData("object", false)]
    [InlineData("MyClass", false)]
    public void IsValueType_IdentifiesValueTypesCorrectly(string typeName, bool expected)
    {
        var result = ToolClassEmitter.IsValueType(typeName);
        Assert.Equal(expected, result);
    }

    #endregion

    #region IsComplexType Tests

    [Theory]
    [InlineData("string", false)]
    [InlineData("int", false)]
    [InlineData("System.Guid", false)]
    [InlineData("int?", false)]
    [InlineData("CreateProductRequest", true)]
    [InlineData("List<string>", true)]
    [InlineData("MyNamespace.MyModel", true)]
    public void IsComplexType_IdentifiesComplexTypesCorrectly(string typeName, bool expected)
    {
        var result = ToolClassEmitter.IsComplexType(typeName);
        Assert.Equal(expected, result);
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

        Assert.Contains("var routeid = System.Uri.EscapeDataString(id.ToString());", result);
        Assert.Contains("var route = $\"/api/products/{routeid}\";", result);
        Assert.DoesNotContain("{id:guid}", result);
        Assert.DoesNotContain("id?.ToString()", result);
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

        Assert.Contains("var routeid = System.Uri.EscapeDataString(id.ToString());", result);
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
                IsNullable = true,
                HasDefaultValue = true,
                DefaultValue = null
            })
        };

        var config = new GeneratorConfigModel();
        var result = ToolClassEmitter.Emit(action, config);

        Assert.Contains("queryParts.Add($\"category={System.Uri.EscapeDataString(category.ToString()!)}\");", result);
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

        Assert.Contains("id?.ToString() ?? string.Empty", result);
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

        Assert.DoesNotContain("queryParts.Add($\"id=", result);
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
        Assert.Contains("[McpServerTool(Name = \"Products_GetById\")]", result);
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
        Assert.Contains("[McpServerTool(Name = \"ProductsController_GetById\")]", result);
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
        Assert.Contains("[McpServerTool(Name = \"custom_get_product\")]", result);
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

        // Should not have trailing comma after invoker parameter when there are no other params
        Assert.DoesNotContain("invoker,", result);
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
        Assert.Contains("var response = await invoker.PostAsync(route, bodyJson);", result);
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
        Assert.Contains("var response = await invoker.PutAsync(route, bodyJson);", result);
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

        Assert.Contains("var response = await invoker.PutAsync(route, null);", result);
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
        Assert.Contains("var response = await invoker.PatchAsync(route, bodyJson);", result);
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

        Assert.Contains("var response = await invoker.PatchAsync(route, null);", result);
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

        Assert.Contains("var response = await invoker.DeleteAsync(route);", result);
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

        Assert.Contains("var routeorderId = System.Uri.EscapeDataString(orderId.ToString());", result);
        Assert.Contains("var routeitemId = System.Uri.EscapeDataString(itemId.ToString());", result);
        Assert.Contains("var response = await invoker.DeleteAsync(route);", result);
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

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Read);", result);
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

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Write);", result);
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

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Write);", result);
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

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Write);", result);
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

        Assert.Contains("await invoker.BeforeInvokeAsync(McpScope.Delete);", result);
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
}
