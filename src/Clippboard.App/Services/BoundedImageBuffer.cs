using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.App.Services;

// WIC PNG output can grow before the final length check. Bound both logical
// length and backing capacity, and wipe every owned scratch buffer on release.
internal sealed class BoundedImageBuffer : MemoryStream
{
    private readonly int _limit;
    private bool _cleared;
    internal bool LimitExceeded { get; private set; }
    internal BoundedImageBuffer(int limit)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
    }
    private void RequireLength(long value)
    {
        ObjectDisposedException.ThrowIf(!CanWrite, this);
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        if (value > _limit) { LimitExceeded = true; throw new IOException("Encoded image exceeds the capture limit."); }
    }
    private void RequireWrite(int count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        RequireLength(Position + (long)count);
    }
    public override int Capacity
    {
        get => base.Capacity;
        set
        {
            // MemoryStream's minimum/growth capacity may exceed a small ceiling
            // even for a valid write. Clamp reserve, not the requested content.
            var previous = GetBuffer();
            base.Capacity = Math.Min(value, _limit);
            if (!ReferenceEquals(previous, GetBuffer())) CryptographicOperations.ZeroMemory(previous);
        }
    }
    public override long Position { get => base.Position; set { RequireLength(value); base.Position = value; } }
    public override void SetLength(long value)
    {
        RequireLength(value); var previous = Length; base.SetLength(value);
        if (value < previous) CryptographicOperations.ZeroMemory(GetBuffer().AsSpan((int)value, (int)(previous - value)));
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var start = origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        long destination;
        try { destination = checked(start + offset); }
        catch (OverflowException) { LimitExceeded = true; throw new IOException("Encoded image seek exceeds its limit."); }
        RequireLength(destination); return base.Seek(offset, origin);
    }
    public override void Write(byte[] buffer, int offset, int count)
    { RequireWrite(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        RequireWrite(buffer.Length);
        var position = (int)Position; var end = position + buffer.Length;
        if (end > Length) base.SetLength(end);
        // MemoryStream's subclass span fallback rents a temporary array. Write
        // straight to our bounded owned storage instead of another PNG copy.
        buffer.CopyTo(GetBuffer().AsSpan(position, buffer.Length)); base.Position = end;
    }
    public override void WriteByte(byte value) { RequireWrite(1); base.WriteByte(value); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_cleared)
        {
            _cleared = true;
            if (TryGetBuffer(out var buffer) && buffer.Array is not null) CryptographicOperations.ZeroMemory(buffer.Array);
        }
        base.Dispose(disposing);
    }
    public override ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}
