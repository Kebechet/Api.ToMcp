using Api.ToMcp.Runtime.Services;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class DefaultBaseUrlProviderTests
{
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
