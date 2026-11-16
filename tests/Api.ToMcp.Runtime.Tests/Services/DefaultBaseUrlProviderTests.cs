using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

/// <summary>
/// The address that names a tool's request outside an MCP request. It is never called, so it only has to be a URL the
/// app serves as its own.
/// </summary>
public class DefaultBaseUrlProviderTests
{
    [Theory]
    [InlineData("http://localhost:5080/", "http://localhost:5080")]
    [InlineData("https://api.example.com/v1", "https://api.example.com/v1")]
    public void GetBaseUrl_UsesTheConfiguredAddressAsGiven_WhateverTheServerIsBoundTo(string configuredBaseUrl, string expected)
    {
        var provider = CreateProvider(["https://+:8443"], configuredBaseUrl);

        Assert.Equal(expected, provider.GetBaseUrl());
    }

    [Theory]
    [InlineData("http://unix:/run/app.sock", "http://localhost:5000", "http://localhost:5000")]
    [InlineData("http://pipe:/app", "http://+:8080", "http://localhost:8080")]
    public void GetBaseUrl_SkipsAUnixSocketAndANamedPipe(string nonTcpAddress, string tcpAddress, string expected)
    {
        // A URL would take "unix" or "pipe" for its host, and the socket's path for the start of its own.
        var provider = CreateProvider([nonTcpAddress, tcpAddress], configuredBaseUrl: null);

        Assert.Equal(expected, provider.GetBaseUrl());
    }

    [Theory]
    [InlineData("http://unix:/run/app.sock")]
    [InlineData(@"http://unix:C:\temp\app.sock")]
    [InlineData("http://pipe:/app")]
    public void GetBaseUrl_SaysToSetTheBaseUrl_WhenTheServerIsBoundOnlyToAUnixSocketOrANamedPipe(string address)
    {
        var provider = CreateProvider([address], configuredBaseUrl: null);

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetBaseUrl());

        Assert.Contains("unix socket or a named pipe", exception.Message);
        Assert.Contains("McpTools:BaseUrl", exception.Message);
    }

    [Fact]
    public void GetBaseUrl_SaysToSetTheBaseUrl_WhenTheServerIsBoundToNoAddress()
    {
        // TestServer, which WebApplicationFactory runs, is bound to none.
        var provider = CreateProvider([], configuredBaseUrl: null);

        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetBaseUrl());

        Assert.Contains("McpTools:BaseUrl", exception.Message);
    }

    [Fact]
    public void GetBaseUrl_PrefersAnHttpsBinding()
    {
        // UseHttpsRedirection would redirect a request named at the http:// one.
        var provider = CreateProvider(["http://+:8080", "https://+:8443"], configuredBaseUrl: null);

        Assert.Equal("https://localhost:8443", provider.GetBaseUrl());
    }

    [Theory]
    [InlineData("http://+:8080", "http://localhost:8080")]
    [InlineData("http://10.0.0.5:8080", "http://10.0.0.5:8080")]
    [InlineData("http://api.corp.example", "http://api.corp.example")]
    public void GetBaseUrl_TakesTheFirstHttpBinding_WithoutAnHttpsOne(string address, string expected)
    {
        var provider = CreateProvider([address, "http://127.0.0.1:8081"], configuredBaseUrl: null);

        Assert.Equal(expected, provider.GetBaseUrl());
    }

    [Fact]
    public void GetBaseUrl_ResolvesTheAddressOnce()
    {
        var addressesFeature = new ServerAddressesFeature();
        addressesFeature.Addresses.Add("http://+:8080");
        var provider = CreateProvider(addressesFeature, configuredBaseUrl: null);

        var first = provider.GetBaseUrl();
        addressesFeature.Addresses.Clear();
        addressesFeature.Addresses.Add("https://+:8443");

        Assert.Equal("http://localhost:8080", first);
        Assert.Equal("http://localhost:8080", provider.GetBaseUrl());
    }

    private static DefaultBaseUrlProvider CreateProvider(string[] boundAddresses, string? configuredBaseUrl)
    {
        var addressesFeature = new ServerAddressesFeature();
        foreach (var address in boundAddresses)
        {
            addressesFeature.Addresses.Add(address);
        }

        return CreateProvider(addressesFeature, configuredBaseUrl);
    }

    private static DefaultBaseUrlProvider CreateProvider(ServerAddressesFeature addressesFeature, string? configuredBaseUrl)
    {
        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(addressesFeature);
        var server = new Mock<IServer>();
        server.Setup(x => x.Features).Returns(features);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["McpTools:BaseUrl"] = configuredBaseUrl })
            .Build();

        return new DefaultBaseUrlProvider(server.Object, configuration);
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
