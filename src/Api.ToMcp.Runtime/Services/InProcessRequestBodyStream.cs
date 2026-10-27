using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Api.ToMcp.Runtime.Services;

/// <summary>
/// The body of an in-process request, held to the limit the app sets, as with <c>[RequestSizeLimit]</c>, as Kestrel
/// holds a body to it: reading a body over the limit throws the <see cref="BadHttpRequestException"/> with 413 that
/// Kestrel throws, and the limit is fixed once reading began. Without a limit set, the body is read whole: it is already
/// in memory, and no larger than the MCP request the server took it from.
/// </summary>
internal sealed class InProcessRequestBodyStream : Stream, IHttpMaxRequestBodySizeFeature
{
    private readonly MemoryStream _body;
    private long? _maxRequestBodySize;
    private bool _isReadStarted;

    public InProcessRequestBodyStream(byte[] body)
    {
        _body = new MemoryStream(body, writable: false);
    }

    public bool IsReadOnly => _isReadStarted;

    public long? MaxRequestBodySize
    {
        get => _maxRequestBodySize;
        set
        {
            if (_isReadStarted)
            {
                throw new InvalidOperationException("The maximum request body size cannot be modified after the app has already started reading the request body.");
            }

            _maxRequestBodySize = value;
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        StartReading();
        return _body.Read(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        StartReading();
        return _body.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        StartReading();
        return _body.ReadAsync(buffer, cancellationToken);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    private void StartReading()
    {
        _isReadStarted = true;
        if (_body.Length > _maxRequestBodySize)
        {
            throw new BadHttpRequestException(
                $"Request body too large. The max request body size is {_maxRequestBodySize} bytes.",
                StatusCodes.Status413PayloadTooLarge);
        }
    }
}
