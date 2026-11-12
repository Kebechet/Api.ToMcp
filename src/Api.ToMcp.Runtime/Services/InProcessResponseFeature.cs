using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The response of an in-process request, started and completed as a server starts and completes one: the
/// <c>OnStarting</c> callbacks run, last registered first, right before the first byte of the body or when the app ends
/// without one, and the <c>OnCompleted</c> callbacks, which dispose the request's services among others, after it. Once
/// it has started, its headers are read-only, as Kestrel's are.
/// </summary>
internal sealed class InProcessResponseFeature : HttpResponseFeature
{
    private readonly List<(Func<object, Task> Callback, object State)> _onStarting = [];
    private readonly List<(Func<object, Task> Callback, object State)> _onCompleted = [];
    private readonly HeaderDictionary _headers = new();
    private bool _isStarting;
    private bool _hasStarted;

    public InProcessResponseFeature()
    {
        Headers = _headers;
    }

    public override bool HasStarted => _hasStarted;

    public override void OnStarting(Func<object, Task> callback, object state)
    {
        if (_hasStarted)
        {
            throw new InvalidOperationException("OnStarting cannot be set because the response has already started.");
        }

        _onStarting.Add((callback, state));
    }

    public override void OnCompleted(Func<object, Task> callback, object state)
    {
        _onCompleted.Add((callback, state));
    }

    /// <summary>
    /// Runs the <c>OnStarting</c> callbacks once, so a callback that writes the body does not start the response again.
    /// The response has started only when every callback succeeded: one that throws leaves it to be answered as failed.
    /// </summary>
    public async Task StartAsync()
    {
        if (_hasStarted || _isStarting)
        {
            return;
        }

        _isStarting = true;
        for (var i = _onStarting.Count - 1; i >= 0; i--)
        {
            await _onStarting[i].Callback(_onStarting[i].State);
        }

        MarkStarted();
    }

    /// <summary>
    /// Replaces what the app set with a bare response of the status, as a server answers an exception thrown before the
    /// response started.
    /// </summary>
    public void AnswerUnhandledException(int statusCode)
    {
        StatusCode = statusCode;
        ReasonPhrase = null;
        _headers.Clear();
        MarkStarted();
    }

    /// <summary>
    /// Runs the <c>OnCompleted</c> callbacks, last registered first, logging rather than throwing a callback's exception,
    /// as a server does once the response is sent.
    /// </summary>
    public async Task CompleteAsync(ILogger logger)
    {
        for (var i = _onCompleted.Count - 1; i >= 0; i--)
        {
            try
            {
                await _onCompleted[i].Callback(_onCompleted[i].State);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "An OnCompleted callback of the tool's in-process API request threw.");
            }
        }
    }

    private void MarkStarted()
    {
        _hasStarted = true;
        _headers.IsReadOnly = true;
    }
}

/// <summary>
/// The response body of an in-process request, kept in memory. Writing or flushing starts the response first, which runs
/// its <c>OnStarting</c> callbacks while the app can still change the headers. Synchronous writes are refused unless the
/// request allows them, as Kestrel refuses them.
/// </summary>
internal sealed class InProcessResponseBodyStream : Stream
{
    private readonly MemoryStream _buffer;
    private readonly InProcessResponseFeature _response;
    private readonly IHttpBodyControlFeature _bodyControl;

    public InProcessResponseBodyStream(MemoryStream buffer, InProcessResponseFeature response, IHttpBodyControlFeature bodyControl)
    {
        _buffer = buffer;
        _response = response;
        _bodyControl = bodyControl;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
        ThrowIfSynchronousIODisallowed();
        _response.StartAsync().GetAwaiter().GetResult();
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        return _response.StartAsync();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ThrowIfSynchronousIODisallowed();
        _response.StartAsync().GetAwaiter().GetResult();
        _buffer.Write(buffer, offset, count);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _response.StartAsync();
        await _buffer.WriteAsync(buffer, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    private void ThrowIfSynchronousIODisallowed()
    {
        if (!_bodyControl.AllowSynchronousIO)
        {
            throw new InvalidOperationException("Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.");
        }
    }
}
