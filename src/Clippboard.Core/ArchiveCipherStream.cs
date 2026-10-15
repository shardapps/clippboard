using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.Core;

// A versioned sequence of independently authenticated, ordered chunks. The
// authenticated empty terminal chunk is mandatory: EOF is never success.
internal sealed class ArchiveCipherStream : Stream
{
    internal const int ChunkSize = 64 * 1024;
    internal const long MaxPlainBytes = 512L * 1024 * 1024;
    private const int HeaderSize = 36, TagSize = 16, Iterations = 600_000;
    private readonly Stream _stream;
    private readonly bool _writing;
    private readonly AesGcm _cipher;
    private readonly byte[] _plain = new byte[ChunkSize], _encrypted = new byte[ChunkSize], _tag = new byte[TagSize];
    private readonly byte[] _nonce = new byte[12], _aad = new byte[HeaderSize + 8], _size = new byte[4];
    private int _count, _position;
    private long _total;
    private uint _index;
    private bool _finished, _disposed;
    private bool _partialSeen;
    private ArchiveCipherStream(Stream stream, byte[] header, byte[] key, bool writing)
    {
        _stream = stream; _writing = writing; _cipher = new AesGcm(key, TagSize);
        header.CopyTo(_aad, 0); header.AsSpan(28, 8).CopyTo(_nonce);
    }
    internal static async Task<ArchiveCipherStream> OpenAsync(Stream stream, ReadOnlyMemory<byte> password, bool writing, CancellationToken cancellation)
    {
        if (password.Length is < 12 or > 1024) throw new ArgumentException("Use a password of at least 12 UTF-8 bytes and at most 1024 bytes.");
        cancellation.ThrowIfCancellationRequested();
        var header = new byte[HeaderSize];
        if (writing)
        {
            "CLIPBAK1"u8.CopyTo(header); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), Iterations);
            RandomNumberGenerator.Fill(header.AsSpan(12));
        }
        else
        {
            await stream.ReadExactlyAsync(header, cancellation);
            if (!header.AsSpan(0, 8).SequenceEqual("CLIPBAK1"u8) || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != Iterations)
                throw new InvalidDataException("Unsupported backup format or password parameters.");
        }
        var key = Rfc2898DeriveBytes.Pbkdf2(password.Span, header.AsSpan(12, 16), Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (writing) await stream.WriteAsync(header, cancellation);
            return new(stream, header, key, writing);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private void SetAssociatedData(int size)
    {
        BinaryPrimitives.WriteUInt32BigEndian(_nonce.AsSpan(8), _index);
        BinaryPrimitives.WriteUInt32LittleEndian(_aad.AsSpan(HeaderSize), _index);
        BinaryPrimitives.WriteInt32LittleEndian(_aad.AsSpan(HeaderSize + 4), size);
        BinaryPrimitives.WriteInt32LittleEndian(_size, size);
    }
    private async Task WriteChunkAsync(int count, CancellationToken cancellation)
    {
        SetAssociatedData(count);
        _cipher.Encrypt(_nonce, _plain.AsSpan(0, count), _encrypted.AsSpan(0, count), _tag, _aad);
        await _stream.WriteAsync(_size, cancellation);
        if (count > 0) await _stream.WriteAsync(_encrypted.AsMemory(0, count), cancellation);
        await _stream.WriteAsync(_tag, cancellation);
        CryptographicOperations.ZeroMemory(_plain); _count = 0; _index++;
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_writing || _finished) throw new InvalidOperationException("Backup stream is not writable.");
        if (buffer.Length > MaxPlainBytes - _total) throw new InvalidDataException("Backup exceeds the 512-MiB plaintext limit.");
        _total += buffer.Length;
        while (!buffer.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var copy = Math.Min(ChunkSize - _count, buffer.Length); buffer[..copy].CopyTo(_plain.AsMemory(_count));
            _count += copy; buffer = buffer[copy..];
            if (_count == ChunkSize) await WriteChunkAsync(_count, cancellationToken);
        }
    }
    internal async Task CompleteAsync(CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_writing || _finished) throw new InvalidOperationException("Backup stream already completed.");
        if (_count > 0) await WriteChunkAsync(_count, cancellation);
        await WriteChunkAsync(0, cancellation); await _stream.FlushAsync(cancellation); _finished = true;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writing) throw new NotSupportedException();
        if (buffer.IsEmpty || _finished) return 0;
        if (_position == _count)
        {
            CryptographicOperations.ZeroMemory(_plain);
            await _stream.ReadExactlyAsync(_size, cancellationToken);
            var count = BinaryPrimitives.ReadInt32LittleEndian(_size);
            if (count is < 0 or > ChunkSize || count > MaxPlainBytes - _total || (_partialSeen && count != 0)) throw new InvalidDataException("Invalid backup chunk bounds.");
            if (count is > 0 and < ChunkSize) _partialSeen = true;
            if (count > 0) await _stream.ReadExactlyAsync(_encrypted.AsMemory(0, count), cancellationToken);
            await _stream.ReadExactlyAsync(_tag, cancellationToken);
            SetAssociatedData(count); _cipher.Decrypt(_nonce, _encrypted.AsSpan(0, count), _tag, _plain.AsSpan(0, count), _aad);
            _index++; _total += count; _count = count; _position = 0;
            if (count == 0)
            {
                var trailing = new byte[1];
                if (await _stream.ReadAsync(trailing, cancellationToken) != 0) throw new InvalidDataException("Unexpected data after backup terminator.");
                _finished = true; return 0;
            }
        }
        var length = Math.Min(buffer.Length, _count - _position); _plain.AsMemory(_position, length).CopyTo(buffer); _position += length; return length;
    }
    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true; _cipher.Dispose(); CryptographicOperations.ZeroMemory(_plain);
            CryptographicOperations.ZeroMemory(_encrypted); CryptographicOperations.ZeroMemory(_tag);
        }
        base.Dispose(disposing); // Caller owns the underlying file/stream.
    }
    public override bool CanRead => !_writing && !_disposed;
    public override bool CanWrite => _writing && !_finished && !_disposed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => _stream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous backup reads.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous backup writes.");
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
