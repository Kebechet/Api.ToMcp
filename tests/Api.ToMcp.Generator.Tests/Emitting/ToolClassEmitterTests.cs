using System.Collections.Generic;
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
    [InlineData("short", true)]
    [InlineData("System.Int16", true)]
    [InlineData("byte", true)]
    [InlineData("System.Byte", true)]
    [InlineData("sbyte", true)]
    [InlineData("SByte", true)]
    [InlineData("System.SByte", true)]
    [InlineData("ushort", true)]
    [InlineData("UInt16", true)]
    [InlineData("System.UInt16", true)]
    [InlineData("uint", true)]
    [InlineData("UInt32", true)]
    [InlineData("System.UInt32", true)]
    [InlineData("ulong", true)]
    [InlineData("UInt64", true)]
    [InlineData("System.UInt64", true)]
    [InlineData("nint", true)]
    [InlineData("IntPtr", true)]
    [InlineData("System.IntPtr", true)]
    [InlineData("nuint", true)]
    [InlineData("UIntPtr", true)]
    [InlineData("System.UIntPtr", true)]
    [InlineData("bool", true)]
    [InlineData("System.Boolean", true)]
    [InlineData("double", true)]
    [InlineData("decimal", true)]
    [InlineData("float", true)]
    [InlineData("Single", true)]
    [InlineData("System.Single", true)]
    [InlineData("Half", true)]
    [InlineData("System.Half", true)]
    [InlineData("Int128", true)]
    [InlineData("System.Int128", true)]
    [InlineData("UInt128", true)]
    [InlineData("System.UInt128", true)]
    [InlineData("char", true)]
    [InlineData("System.Char", true)]
    [InlineData("Guid", true)]
    [InlineData("System.Guid", true)]
    [InlineData("DateTime", true)]
    [InlineData("System.DateTime", true)]
    [InlineData("DateTimeOffset", true)]
    [InlineData("System.DateTimeOffset", true)]
    [InlineData("TimeSpan", true)]
    [InlineData("System.TimeSpan", true)]
    [InlineData("DateOnly", true)]
    [InlineData("System.DateOnly", true)]
    [InlineData("TimeOnly", true)]
    [InlineData("System.TimeOnly", true)]
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
    [InlineData("DateOnly", false)]
    [InlineData("System.DateOnly", false)]
    [InlineData("TimeOnly", false)]
    [InlineData("System.TimeOnly", false)]
    [InlineData("sbyte", false)]
    [InlineData("System.SByte", false)]
    [InlineData("ushort", false)]
    [InlineData("System.UInt16", false)]
    [InlineData("uint", false)]
    [InlineData("System.UInt32", false)]
    [InlineData("ulong", false)]
    [InlineData("System.UInt64", false)]
    [InlineData("nint", false)]
    [InlineData("System.IntPtr", false)]
    [InlineData("nuint", false)]
    [InlineData("System.UIntPtr", false)]
    [InlineData("Half", false)]
    [InlineData("System.Half", false)]
    [InlineData("Int128", false)]
    [InlineData("System.Int128", false)]
    [InlineData("UInt128", false)]
    [InlineData("System.UInt128", false)]
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

        Assert.Contains("var routedate = System.Uri.EscapeDataString(date.ToString());", result);
        Assert.DoesNotContain("date?.ToString()", result);
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

        Assert.Contains("var routetime = System.Uri.EscapeDataString(time.ToString());", result);
        Assert.DoesNotContain("time?.ToString()", result);
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

        Assert.Contains("date?.ToString() ?? string.Empty", result);
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

        Assert.Contains("var routerequest_MeasurementTypeId = System.Uri.EscapeDataString(request.MeasurementTypeId.ToString());", result);
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

        Assert.Contains("request.ExerciseId.ToString()", result);
        Assert.Contains("request.SetId.ToString()", result);
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

        Assert.Contains("var routeorderId = System.Uri.EscapeDataString(orderId.ToString());", result);
        Assert.Contains("request.ItemId.ToString()", result);
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

        Assert.Contains("filter.CategoryId?.ToString() ?? string.Empty", result);
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

        Assert.Contains("request.itemId.ToString()", result);
        Assert.DoesNotContain("{ItemId}", result);
    }

    #endregion
}
