using System.Runtime.Versioning;
using Api.ToMcp.Runtime.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Api.ToMcp.Runtime.Tests.Services;

public class ServerRequestLimitsTests
{
    private const string Http11 = "HTTP/1.1";

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, StatusCodes.Status414UriTooLong)]
    public void GetRefusal_PassesARequestLineAtKestrelsLimit_AndRefusesOneByteMore(int over, int? expectedStatus)
    {
        // "GET " + target + " HTTP/1.1\r\n": the CRLF counts, as Kestrel looks for the line's end within the limit.
        var limits = ServerRequestLimits.ForKestrel(new KestrelServerLimits());
        var request = CreateRequest("/" + new string('a', 8192 - 15 - 1 + over));

        var refusal = limits.GetRefusal(request);

        Assert.Equal(expectedStatus, refusal?.StatusCode);
        Assert.Equal(expectedStatus is null ? null : "Request line too long.", refusal?.Reason);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, StatusCodes.Status414UriTooLong)]
    public void GetRefusal_CountsTheRequestLine_WithTheTargetAsSent_AndTheMethodAndProtocolOfTheRequest(int over, int? expectedStatus)
    {
        // "DELETE /a%40b?q=%2F HTTP/1.1\r\n" is 30 bytes, the percent-encoded target included as it is.
        var limits = ServerRequestLimits.ForKestrel(new KestrelServerLimits { MaxRequestLineSize = 30 - over });
        var request = CreateRequest("/a%40b?q=%2F", HttpMethods.Delete);

        var refusal = limits.GetRefusal(request);

        Assert.Equal(expectedStatus, refusal?.StatusCode);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, StatusCodes.Status431RequestHeaderFieldsTooLarge)]
    public void GetRefusal_PassesHeadersAtKestrelsTotalSize_AndRefusesOneByteMore(int over, int? expectedStatus)
    {
        // Each header is one line of "name: value\r\n"; the empty line that ends them does not count.
        var limits = ServerRequestLimits.ForKestrel(new KestrelServerLimits());
        var request = CreateRequest("/");
        request.Headers.Host = "api.example.com";
        request.Headers["X-Pad"] = new string('p', 32768 - "Host: api.example.com\r\n".Length - "X-Pad: \r\n".Length + over);

        var refusal = limits.GetRefusal(request);

        Assert.Equal(expectedStatus, refusal?.StatusCode);
        Assert.Equal(expectedStatus is null ? null : "Request headers too long.", refusal?.Reason);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, StatusCodes.Status431RequestHeaderFieldsTooLarge)]
    public void GetRefusal_CountsAHeaderOfSeveralValues_AsOneLineOfThemSeparatedByCommas(int over, int? expectedStatus)
    {
        // "X-Tags: a, bc\r\n" is 15 bytes, as HttpClient writes the header.
        var limits = ServerRequestLimits.ForKestrel(new KestrelServerLimits { MaxRequestHeadersTotalSize = 15 - over });
        var request = CreateRequest("/");
        request.Headers["X-Tags"] = new[] { "a", "bc" };

        var refusal = limits.GetRefusal(request);

        Assert.Equal(expectedStatus, refusal?.StatusCode);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, StatusCodes.Status431RequestHeaderFieldsTooLarge)]
    public void GetRefusal_CountsAHeaderValue_InTheBytesOfItsUtf8(int over, int? expectedStatus)
    {
        // "X-Name: é\r\n" is 12 bytes: the é takes 2.
        var limits = ServerRequestLimits.ForKestrel(new KestrelServerLimits { MaxRequestHeadersTotalSize = 12 - over });
        var request = CreateRequest("/");
        request.Headers["X-Name"] = "é";

        var refusal = limits.GetRefusal(request);

        Assert.Equal(expectedStatus, refusal?.StatusCode);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, StatusCodes.Status431RequestHeaderFieldsTooLarge)]
    public void GetRefusal_PassesKestrelsNumberOfHeaders_AndRefusesOneMore(int over, int? expectedStatus)
    {
        var limits = ServerRequestLimits.ForKestrel(new KestrelServerLimits());
        var request = CreateRequest("/");
        for (var i = 0; i < 100 + over; i++)
        {
            request.Headers[$"X-Pad-{i}"] = "1";
        }

        var refusal = limits.GetRefusal(request);

        Assert.Equal(expectedStatus, refusal?.StatusCode);
        Assert.Equal(expectedStatus is null ? null : "Request contains too many headers.", refusal?.Reason);
    }

    [Fact]
    public void GetRefusal_RefusesHeadersForTheirSize_WhenALineOverTheSizeComesBeforeOneOverTheNumber()
    {
        // Kestrel counts a header only once its whole line is read, so a line past the size never counts.
        var limits = ServerRequestLimits.ForKestrel(new KestrelServerLimits { MaxRequestHeadersTotalSize = 20, MaxRequestHeaderCount = 1 });
        var request = CreateRequest("/");
        request.Headers["X-A"] = "1";
        request.Headers["X-B"] = new string('b', 20);

        var refusal = limits.GetRefusal(request);

        Assert.Equal("Request headers too long.", refusal?.Reason);
    }

    [Fact]
    public void GetRefusal_ReadsKestrelsLimitsForEachRequest()
    {
        var kestrelLimits = new KestrelServerLimits();
        var limits = ServerRequestLimits.ForKestrel(kestrelLimits);
        var request = CreateRequest("/" + new string('a', 100));

        kestrelLimits.MaxRequestLineSize = 100;
        kestrelLimits.MaxRequestBodySize = 7;

        Assert.Equal(StatusCodes.Status414UriTooLong, limits.GetRefusal(request)?.StatusCode);
        Assert.Equal(7, limits.MaxRequestBodySize);
    }

    [Fact]
    public void None_RefusesNoRequest_AndHoldsNoBody()
    {
        var request = CreateRequest("/" + new string('a', 100_000));
        for (var i = 0; i < 1000; i++)
        {
            request.Headers[$"X-Pad-{i}"] = new string('p', 100);
        }

        Assert.Null(ServerRequestLimits.None.GetRefusal(request));
        Assert.Null(ServerRequestLimits.None.MaxRequestBodySize);
    }

    [Fact]
    public void Of_GivesNoLimits_WithoutAServer()
    {
        var limits = ServerRequestLimits.Of(new ServiceCollection().BuildServiceProvider());

        Assert.Same(ServerRequestLimits.None, limits);
    }

    [Fact]
    public void Of_GivesNoLimits_ForAServerItDoesNotKnow_ThoughKestrelsOptionsAreRegistered()
    {
        // The default host configures Kestrel's options even when a test replaces the server, as with TestServer.
        var services = new ServiceCollection()
            .AddSingleton(Mock.Of<IServer>())
            .Configure<KestrelServerOptions>(options => options.Limits.MaxRequestLineSize = 10)
            .BuildServiceProvider();

        var limits = ServerRequestLimits.Of(services);

        Assert.Same(ServerRequestLimits.None, limits);
    }

    [Fact]
    public async Task Of_ReadsKestrelsLimits_FromTheOptionsTheHostGaveIt()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestLineSize = 50;
            options.Limits.MaxRequestBodySize = 1234;
        });
        await using var app = builder.Build();

        var limits = ServerRequestLimits.Of(app.Services);

        Assert.Equal(1234, limits.MaxRequestBodySize);
        Assert.Equal(StatusCodes.Status414UriTooLong, limits.GetRefusal(CreateRequest("/" + new string('a', 50)))?.StatusCode);
        Assert.Null(limits.GetRefusal(CreateRequest("/a")));
    }

    [Fact]
    public void Of_ReadsTheLimits_OfAKestrelServerMadeByHand_FromItsOwnOptions()
    {
        var options = new KestrelServerOptions();
        options.Limits.MaxRequestBodySize = 99;
        var services = new ServiceCollection()
            .AddSingleton<IServer>(new KestrelServer(Microsoft.Extensions.Options.Options.Create(options), Mock.Of<IConnectionListenerFactory>(), NullLoggerFactory.Instance))
            .BuildServiceProvider();

        var limits = ServerRequestLimits.Of(services);

        Assert.Equal(99, limits.MaxRequestBodySize);
    }

    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public async Task Of_ReadsTheBodyLimitOfHttpSys_AndNoLimitOfTheRequestLineOrHeaders()
    {
        // HTTP.sys holds the request line and the headers to limits from the registry, which the app cannot read.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseHttpSys(options => options.MaxRequestBodySize = 4321);
        await using var app = builder.Build();

        var limits = ServerRequestLimits.Of(app.Services);

        Assert.Equal(4321, limits.MaxRequestBodySize);
        Assert.Null(limits.GetRefusal(CreateRequest("/" + new string('a', 100_000))));
    }

    private static HttpRequestFeature CreateRequest(string rawTarget, string? method = null)
    {
        return new HttpRequestFeature
        {
            Method = method ?? HttpMethods.Get,
            RawTarget = rawTarget,
            Protocol = Http11
        };
    }

    private sealed class WindowsOnlyFactAttribute : FactAttribute
    {
        public WindowsOnlyFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
            {
                Skip = "HTTP.sys runs only on Windows.";
            }
        }
    }
}
