using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class DefaultBaseUrlProviderTests
{
    [Fact]
    public void GetBaseUrl_UsesTheConfiguredHttpAddressAsGiven_WhenTheRequestCameInOverHttps()
    {
        var provider = CreateProvider(["http://+:8080"], configuredBaseUrl: "http://localhost:5080/", request: ("https", "api.example.com"));

        Assert.Equal("http://localhost:5080", provider.GetBaseUrl());
    }

    [Theory]
    [InlineData("api.example.com")]
    [InlineData("internal.example:8443")]
    [InlineData("169.254.169.254")]
    public void GetBaseUrl_KeepsTheHttpBinding_WhateverHostTheHttpsRequestNames(string requestHost)
    {
        // The Host header is the client's to set, so following it sent the tool's request, with the caller's
        // Authorization, to any server the app can reach: https://github.com/Kebechet/Api.ToMcp/issues/48
        var provider = CreateProvider(["http://+:8080"], configuredBaseUrl: null, request: ("https", requestHost));

        Assert.Equal("http://localhost:8080", provider.GetBaseUrl());
    }

    [Fact]
    public void GetForwardedScheme_IsHttps_ForAnHttpsRequest_AtAnHttpBinding()
    {
        // Behind a TLS-terminating proxy UseHttpsRedirection would redirect a call to the http binding to an https
        // port nothing listens on locally, unless the call says it stands for an https request.
        var provider = CreateProvider(["http://+:8080"], configuredBaseUrl: null, request: ("https", "api.example.com"));

        Assert.Equal("https", provider.GetForwardedScheme());
    }

    [Fact]
    public void GetForwardedScheme_IsNull_ForAnHttpRequest()
    {
        var provider = CreateProvider(["http://+:8080"], configuredBaseUrl: null, request: ("http", "localhost:8080"));

        Assert.Null(provider.GetForwardedScheme());
    }

    [Fact]
    public void GetForwardedScheme_IsNull_OutsideARequest()
    {
        var provider = CreateProvider(["http://+:8080"], configuredBaseUrl: null, request: null);

        Assert.Null(provider.GetForwardedScheme());
    }

    [Fact]
    public void GetForwardedScheme_IsNull_ForTheConfiguredHttpAddress()
    {
        var provider = CreateProvider(["http://+:8080"], configuredBaseUrl: "http://localhost:5080", request: ("https", "api.example.com"));

        Assert.Null(provider.GetForwardedScheme());
    }

    [Fact]
    public void GetForwardedScheme_IsNull_AtAnHttpsBinding()
    {
        var provider = CreateProvider(["http://+:8080", "https://+:8443"], configuredBaseUrl: null, request: ("https", "api.example.com"));

        Assert.Null(provider.GetForwardedScheme());
    }

    [Fact]
    public void GetForwardedScheme_IsNull_ForAProviderThatCannotSeeTheRequest()
    {
        var addressesFeature = new ServerAddressesFeature();
        addressesFeature.Addresses.Add("http://+:8080");
        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(addressesFeature);
        var server = new Mock<IServer>();
        server.Setup(x => x.Features).Returns(features);

        var provider = new DefaultBaseUrlProvider(server.Object, new ConfigurationBuilder().Build());

        Assert.Null(provider.GetForwardedScheme());
    }

    [Fact]
    public void GetBaseUrl_KeepsTheHttpBinding_ForARequestThatCameInOverHttp()
    {
        var provider = CreateProvider(["http://+:8080"], configuredBaseUrl: null, request: ("http", "localhost:8080"));

        Assert.Equal("http://localhost:8080", provider.GetBaseUrl());
    }

    [Fact]
    public void GetBaseUrl_KeepsTheHttpBinding_OutsideARequest()
    {
        var provider = CreateProvider(["http://+:8080"], configuredBaseUrl: null, request: null);

        Assert.Equal("http://localhost:8080", provider.GetBaseUrl());
    }

    [Fact]
    public void GetBaseUrl_PrefersAnHttpsBinding_OverTheRequestsAddress()
    {
        var provider = CreateProvider(["http://+:8080", "https://+:8443"], configuredBaseUrl: null, request: ("https", "api.example.com"));

        Assert.Equal("https://localhost:8443", provider.GetBaseUrl());
    }

    [Fact]
    public void GetForwardedScheme_IsDecidedForEachRequest()
    {
        var httpContextAccessor = new HttpContextAccessor();
        var provider = CreateProvider(["http://+:8080"], configuredBaseUrl: null, httpContextAccessor);

        httpContextAccessor.HttpContext = CreateHttpContext("https", "api.example.com");
        var throughProxy = (provider.GetBaseUrl(), provider.GetForwardedScheme());
        httpContextAccessor.HttpContext = CreateHttpContext("http", "localhost:8080");
        var direct = (provider.GetBaseUrl(), provider.GetForwardedScheme());

        Assert.Equal(("http://localhost:8080", "https"), throughProxy);
        Assert.Equal(("http://localhost:8080", (string?)null), direct);
    }

    private static DefaultBaseUrlProvider CreateProvider(string[] boundAddresses, string? configuredBaseUrl, (string Scheme, string Host)? request)
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = request is { } value ? CreateHttpContext(value.Scheme, value.Host) : null
        };

        return CreateProvider(boundAddresses, configuredBaseUrl, httpContextAccessor);
    }

    private static DefaultBaseUrlProvider CreateProvider(string[] boundAddresses, string? configuredBaseUrl, IHttpContextAccessor httpContextAccessor)
    {
        var addressesFeature = new ServerAddressesFeature();
        foreach (var address in boundAddresses)
        {
            addressesFeature.Addresses.Add(address);
        }

        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(addressesFeature);
        var server = new Mock<IServer>();
        server.Setup(x => x.Features).Returns(features);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["McpTools:BaseUrl"] = configuredBaseUrl })
            .Build();

        return new DefaultBaseUrlProvider(server.Object, configuration, httpContextAccessor);
    }

    private static HttpContext CreateHttpContext(string scheme, string host)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = scheme;
        httpContext.Request.Host = new HostString(host);

        return httpContext;
    }

    [Theory]
    [InlineData("http://+:80", "http://localhost:80")]
    [InlineData("http://*:80", "http://localhost:80")]
    [InlineData("http://0.0.0.0:80", "http://localhost:80")]
    [InlineData("http://[::]:80", "http://localhost:80")]
    [InlineData("http://[::1]:80", "http://localhost:80")]
    [InlineData("https://+:443", "https://localhost:443")]
    [InlineData("https://*:443", "https://localhost:443")]
    [InlineData("http://+:8080", "http://localhost:8080")]
    [InlineData("http://0.0.0.0:5000", "http://localhost:5000")]
    [InlineData("http://[::]:5000", "http://localhost:5000")]
    public void NormalizeBindingAddress_ReplacesWildcardHosts_WithLocalhost(string input, string expected)
    {
        var result = DefaultBaseUrlProvider.NormalizeBindingAddress(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://localhost:5001", "https://localhost:5001")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    [InlineData("https://myapp.azurewebsites.net", "https://myapp.azurewebsites.net")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080")]
    public void NormalizeBindingAddress_PreservesValidAddresses(string input, string expected)
    {
        var result = DefaultBaseUrlProvider.NormalizeBindingAddress(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("http://+:80/", "http://localhost:80")]
    [InlineData("https://localhost:5001/", "https://localhost:5001")]
    public void NormalizeBindingAddress_TrimsTrailingSlash(string input, string expected)
    {
        var result = DefaultBaseUrlProvider.NormalizeBindingAddress(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("http://+", "http://localhost")]
    [InlineData("http://*", "http://localhost")]
    [InlineData("http://0.0.0.0", "http://localhost")]
    public void NormalizeBindingAddress_HandlesNoPort(string input, string expected)
    {
        var result = DefaultBaseUrlProvider.NormalizeBindingAddress(input);
        Assert.Equal(expected, result);
    }
}
