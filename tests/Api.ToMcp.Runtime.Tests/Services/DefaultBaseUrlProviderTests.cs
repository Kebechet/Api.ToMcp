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

    [Theory]
    [InlineData("http://unix:/run/app.sock", "http://localhost:5000", "http://localhost:5000")]
    [InlineData("http://pipe:/app", "http://+:8080", "http://localhost:8080")]
    public void GetBaseUrl_SkipsAUnixSocketAndANamedPipe(string nonTcpAddress, string tcpAddress, string expected)
    {
        // An HTTP client takes "unix" or "pipe" for the host, so a call there fails.
        var provider = CreateProvider([nonTcpAddress, tcpAddress], configuredBaseUrl: null, request: ("https", "api.example.com"));

        Assert.Equal(expected, provider.GetBaseUrl());
    }

    [Theory]
    [InlineData("http://unix:/run/app.sock")]
    [InlineData(@"http://unix:C:\temp\app.sock")]
    [InlineData("http://pipe:/app")]
    public void GetBaseUrl_SaysToSetTheBaseUrl_WhenTheServerIsBoundOnlyToAUnixSocketOrANamedPipe(string address)
    {
        var provider = CreateProvider([address], configuredBaseUrl: null, request: ("https", "api.example.com"));

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetBaseUrl());

        Assert.Contains("unix socket or a named pipe", exception.Message);
        Assert.Contains("McpTools:BaseUrl", exception.Message);
    }

    [Theory]
    [InlineData("http://10.0.0.5:8080", "http://127.0.0.1:8081", "http://127.0.0.1:8081")]
    [InlineData("http://api.corp.example", "http://*:8080", "http://localhost:8080")]
    [InlineData("http://api.corp.example", "http://localhost:8080", "http://localhost:8080")]
    public void GetBaseUrl_PrefersAnHttpBindingOnLoopback_OverOneOffIt(string offLoopback, string onLoopback, string expected)
    {
        // The app's forwarded headers trust loopback by default, where a call to a loopback address comes from.
        var provider = CreateProvider([offLoopback, onLoopback], configuredBaseUrl: null, request: ("https", "api.example.com"));

        Assert.Equal(expected, provider.GetBaseUrl());
    }

    [Theory]
    [InlineData("http://10.0.0.5:8080")]
    [InlineData("http://api.corp.example")]
    public void GetBaseUrl_RefusesAnHttpsRequest_WhenTheOnlyBindingIsAnHttpOneOffLoopback(string address)
    {
        // The caller's token would go in clear text off the machine, and the app would take the call for HTTP.
        var provider = CreateProvider([address], configuredBaseUrl: null, request: ("https", "api.example.com"));

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetBaseUrl());

        Assert.Contains(address, exception.Message);
        Assert.Contains("McpTools:BaseUrl", exception.Message);
    }

    [Fact]
    public void GetBaseUrl_CallsAnHttpBindingOffLoopback_ForAnHttpRequest()
    {
        var provider = CreateProvider(["http://10.0.0.5:8080"], configuredBaseUrl: null, request: ("http", "10.0.0.5:8080"));

        Assert.Equal("http://10.0.0.5:8080", provider.GetBaseUrl());
    }

    [Fact]
    public void GetBaseUrl_CallsAnHttpBindingOffLoopback_OutsideARequest()
    {
        var provider = CreateProvider(["http://10.0.0.5:8080"], configuredBaseUrl: null, request: null);

        Assert.Equal("http://10.0.0.5:8080", provider.GetBaseUrl());
    }

    [Fact]
    public void GetBaseUrl_UsesTheConfiguredHttpAddressOffLoopbackAsGiven_ForAnHttpsRequest()
    {
        var provider = CreateProvider(["http://10.0.0.5:8080"], configuredBaseUrl: "http://api.internal:8080", request: ("https", "api.example.com"));

        Assert.Equal("http://api.internal:8080", provider.GetBaseUrl());
    }

    [Fact]
    public void GetBaseUrl_CallsAnHttpBindingOffLoopback_ForAProviderThatCannotSeeTheRequest()
    {
        var addressesFeature = new ServerAddressesFeature();
        addressesFeature.Addresses.Add("http://10.0.0.5:8080");
        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(addressesFeature);
        var server = new Mock<IServer>();
        server.Setup(x => x.Features).Returns(features);

        var provider = new DefaultBaseUrlProvider(server.Object, new ConfigurationBuilder().Build());

        Assert.Equal("http://10.0.0.5:8080", provider.GetBaseUrl());
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
    public void GetBaseUrl_DecidesTheRefusalForEachRequest()
    {
        var httpContextAccessor = new HttpContextAccessor();
        var provider = CreateProvider(["http://10.0.0.5:8080"], configuredBaseUrl: null, httpContextAccessor);

        httpContextAccessor.HttpContext = CreateHttpContext("http", "10.0.0.5:8080");
        var direct = provider.GetBaseUrl();
        httpContextAccessor.HttpContext = CreateHttpContext("https", "api.example.com");

        Assert.Equal("http://10.0.0.5:8080", direct);
        Assert.Throws<InvalidOperationException>(() => provider.GetBaseUrl());
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
