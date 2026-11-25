namespace AgentBridge.Host.Delivery.Staging;

// Bound plaintext as it arrives, not only after a blob source has finished writing to disk.
// The caller owns output; disposing this wrapper leaves it open for final length/hash checks.
sealed class BlobWriteStream(Stream output, long size) : Stream
{
    long written;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => written;
    public override long Position { get => written; set => throw new NotSupportedException(); }
    public override void Flush() => output.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => output.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    void Check(int count)
    {
        if (count > size - written) { throw new InvalidDataException("the blob runs past the attachment's size"); }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(buffer.Length);
        output.Write(buffer);
        written += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(buffer.Length);
        await output.WriteAsync(buffer, cancellationToken);
        written += buffer.Length;
    }
}
