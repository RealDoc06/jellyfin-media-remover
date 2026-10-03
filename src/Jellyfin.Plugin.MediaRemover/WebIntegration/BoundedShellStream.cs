namespace Jellyfin.Plugin.MediaRemover.WebIntegration;

/// <summary>Buffers the small web shell; oversized responses pass through without modification.</summary>
internal sealed class BoundedShellStream(Stream destination, int maximumBytes) : Stream
{
    private readonly MemoryStream _buffer = new();
    public bool IsPassthrough { get; private set; }
    public byte[] GetBytes() => _buffer.ToArray();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!IsPassthrough && _buffer.Length + buffer.Length <= maximumBytes)
        {
            _buffer.Write(buffer);
            return;
        }
        if (!IsPassthrough)
        {
            IsPassthrough = true;
            _buffer.Position = 0;
            _buffer.CopyTo(destination);
            _buffer.SetLength(0);
        }
        destination.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsPassthrough && _buffer.Length + buffer.Length <= maximumBytes)
        {
            await _buffer.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!IsPassthrough)
        {
            IsPassthrough = true;
            _buffer.Position = 0;
            await _buffer.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            _buffer.SetLength(0);
        }
        await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    // ASP.NET may start or flush static-file responses early. Delay that until the transform is decided.
    public override void Flush() { if (IsPassthrough) destination.Flush(); }
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        IsPassthrough ? destination.FlushAsync(cancellationToken) : Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _buffer.Dispose();
        base.Dispose(disposing);
    }
}
