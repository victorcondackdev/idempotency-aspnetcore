namespace Payments.Idempotency.AspNetCore.Internal;

/// <summary>
/// Write-through stream that forwards every write to the real response body and keeps a copy
/// for replay, up to a size limit. Streaming responses keep streaming: nothing is delayed, and
/// once the limit is exceeded the copy is dropped and <see cref="Overflowed"/> is set.
/// </summary>
internal sealed class ResponseCaptureStream : Stream
{
    private readonly Stream _inner;
    private readonly long _limit;
    private MemoryStream? _buffer = new();

    public ResponseCaptureStream(Stream inner, long limit)
    {
        _inner = inner;
        _limit = limit;
    }

    /// <summary>Whether the response exceeded the limit and was not captured.</summary>
    public bool Overflowed => _buffer is null;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>The captured bytes; empty when <see cref="Overflowed"/>.</summary>
    public byte[] ToArray() => _buffer?.ToArray() ?? [];

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Capture(buffer.AsSpan(offset, count));
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Capture(buffer);
        _inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Capture(buffer.AsSpan(offset, count));
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Capture(buffer.Span);
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        // The inner stream belongs to the server; only drop our copy.
        if (disposing)
        {
            _buffer?.Dispose();
            _buffer = null;
        }

        base.Dispose(disposing);
    }

    private void Capture(ReadOnlySpan<byte> data)
    {
        if (_buffer is null)
        {
            return;
        }

        if (_buffer.Length + data.Length > _limit)
        {
            _buffer.Dispose();
            _buffer = null;
            return;
        }

        _buffer.Write(data);
    }
}
