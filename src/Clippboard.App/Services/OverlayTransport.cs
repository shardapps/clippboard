using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal sealed record OverlayRequest(string Command, string? Id = null, bool Flag = false, AppSettings? Settings = null,
    string? Query = null, IReadOnlyList<string>? Ids = null, string? Text = null, string? Title = null, string[]? Labels = null,
    string? Path = null, byte[]? Secret = null, FileActions.DestinationRevision? Destination = null, long VisibilityRevision = 0, long Surface = 0);
internal sealed record OverlayReply(string? Error = null, AppSettings? Settings = null, long Target = 0, long Version = 0, bool Close = false,
    bool StartupAvailable = true, IReadOnlyList<ClipSummary>? Entries = null, Clip? Clip = null, bool Demo = false, int ListenerProcess = 0, bool Standby = false,
    IReadOnlyList<string>? Matches = null, bool CaptureSuspended = false, bool Saved = false, BackupState? Transfer = null, string? Diagnostics = null, long VisibilityRevision = 0);

internal static class OverlayTransport
{
    // A Unicode text edit can expand to six JSON bytes per UTF-16 code unit.
    // Text commands additionally enforce the original 512-KiB character bound.
    internal const int RequestLimit = 4 * 1024 * 1024;
    internal const int ReplyLimit = 40 * 1024 * 1024;
    private const int ReplyVersion = 1;
    private sealed record WireReply(OverlayReply Reply, bool ImageAttached);
    private sealed class NoInlineImage : JsonConverter<byte[]>
    {
        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => throw new JsonException("Images require a binary attachment.");
        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
            => throw new JsonException("Images require a binary attachment.");
    }
    private static readonly JsonSerializerOptions ReplyJson = new() { Converters = { new NoInlineImage() } };
    internal static async Task WriteReplyAsync(Stream stream, OverlayReply reply, int limit, CancellationToken cancel)
    {
        var image = reply.Clip?.ImagePng;
        var imageLength = image?.Length ?? 0;
        if (imageLength > limit) throw new InvalidDataException("Overlay image exceeds its limit.");
        // Keep control metadata as JSON; image bytes are already encoded PNG.
        // Never create a Base64 string or second whole-image send buffer.
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new WireReply(image is null ? reply : reply with { Clip = reply.Clip! with { ImagePng = null } }, image is not null), ReplyJson);
        try
        {
            if (metadata.Length > limit - imageLength) throw new InvalidDataException("Overlay reply exceeds its limit.");
            var header = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(header, ReplyVersion);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), metadata.Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), imageLength);
            await stream.WriteAsync(header, cancel); await stream.WriteAsync(metadata, cancel);
            if (image is not null) await stream.WriteAsync(image, cancel);
            await stream.FlushAsync(cancel);
        }
        finally { CryptographicOperations.ZeroMemory(metadata); }
    }
    internal static async Task<OverlayReply> ReadReplyAsync(Stream stream, int limit, CancellationToken cancel)
    {
        var header = new byte[12]; await stream.ReadExactlyAsync(header, cancel);
        var version = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var imageLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        // Validate both lengths and their combined ceiling before allocation.
        if (version != ReplyVersion || length <= 0 || length > limit || imageLength < 0 || imageLength > limit - length)
            throw new InvalidDataException("Invalid overlay reply frame.");
        var metadata = new byte[length]; byte[]? image = null;
        try
        {
            await stream.ReadExactlyAsync(metadata, cancel);
            var wire = JsonSerializer.Deserialize<WireReply>(metadata, ReplyJson) ?? throw new InvalidDataException("Empty overlay reply.");
            if (wire.Reply?.Clip?.ImagePng is byte[] unexpected)
            {
                CryptographicOperations.ZeroMemory(unexpected);
                throw new InvalidDataException("Overlay image must use the binary attachment.");
            }
            if (wire.Reply is null || (!wire.ImageAttached && imageLength != 0)
                || (wire.ImageAttached && wire.Reply.Clip?.Kind != ClipKind.Image))
                throw new InvalidDataException("Invalid overlay image attachment.");
            if (!wire.ImageAttached) return wire.Reply;
            image = new byte[imageLength]; await stream.ReadExactlyAsync(image, cancel);
            var result = wire.Reply with { Clip = wire.Reply.Clip! with { ImagePng = image } };
            image = null; // Ownership transfers to the normal payload consumer.
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadata);
            if (image is not null) CryptographicOperations.ZeroMemory(image);
        }
    }
    internal static async Task WriteAsync<T>(Stream stream, T value, int limit, CancellationToken cancel)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            if (bytes.Length > limit) throw new InvalidDataException("Overlay message exceeds its limit.");
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            await stream.WriteAsync(header, cancel); await stream.WriteAsync(bytes, cancel); await stream.FlushAsync(cancel);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static async Task<T> ReadAsync<T>(Stream stream, int limit, CancellationToken cancel)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancel);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > limit) throw new InvalidDataException("Invalid overlay message size.");
        var bytes = new byte[length];
        try { await stream.ReadExactlyAsync(bytes, cancel); return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty overlay message."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal sealed class OverlayClient(string pipeName) : IHistoryAccess, IHistoryTransfer, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    internal async Task<OverlayReply> CallAsync(OverlayRequest request, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await _gate.WaitAsync(deadline.Token);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000, deadline.Token);
            await OverlayTransport.WriteAsync(pipe, request, OverlayTransport.RequestLimit, deadline.Token);
            var reply = await OverlayTransport.ReadReplyAsync(pipe, OverlayTransport.ReplyLimit, deadline.Token);
            if (reply.Error is not null) throw new IOException(reply.Error);
            return reply;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or JsonException or InvalidDataException) { throw new IOException("The clipboard listener is unavailable. Open the rope again.", ex); }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<ClipSummary>> ListAsync() => (await CallAsync(new("list"))).Entries ?? [];
    public async Task<Clip?> GetAsync(string id) => (await CallAsync(new("get", id))).Clip;
    public async Task<byte[]?> GetPreviewAsync(string id) => (await CallAsync(new("preview", id))).Clip?.ImagePng;
    public async Task<IReadOnlyList<string>> SearchBatchAsync(string query, IReadOnlyList<string> ids, CancellationToken cancellation = default)
        => (await CallAsync(new("search", Query: query, Ids: ids), cancellation)).Matches ?? [];
    public async Task SetPinnedAsync(string id, bool pinned) => await CallAsync(new("pin", id, pinned));
    public async Task DeleteAsync(string id) => await CallAsync(new("delete", id));
    public async Task ClearAsync() => await CallAsync(new("clear"));
    public async Task<bool> SaveSnippetAsync(string? id, string text, string title, string[] labels, CancellationToken cancellation = default)
        => (await CallAsync(new("save-snippet", Id: id, Text: text, Title: title, Labels: labels), cancellation)).Saved;
    public async Task<BackupState> BeginTransferAsync(string id, bool importing, string path, byte[] password, FileActions.DestinationRevision? expected, CancellationToken cancellation)
        => (await CallAsync(new(importing ? "transfer-import" : "transfer-export", Id: id, Path: path, Secret: password, Destination: expected), cancellation)).Transfer
            ?? throw new IOException("Transfer did not start.");
    public async Task<BackupState?> TransferStateAsync(string id, CancellationToken cancellation)
        => (await CallAsync(new("transfer-state", Id: id), cancellation)).Transfer;
    public async Task<BackupState> CommitTransferAsync(string id, CancellationToken cancellation)
        => (await CallAsync(new("transfer-commit", Id: id), cancellation)).Transfer ?? throw new IOException("Restore could not start.");
    public async Task CancelTransferAsync(string id) => _ = await CallAsync(new("transfer-cancel", Id: id));
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}

internal sealed class OverlayServer(Func<OverlayRequest, Task<OverlayReply>> handler) : IDisposable
{
    internal string Name { get; } = "ShardApps.Clippboard." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _run, _shutdown;
    private bool _disposed;
    internal Task Shutdown { get { lock (_gate) return _shutdown ?? Task.CompletedTask; } }
    internal Task RunAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _run ??= RunCoreAsync(_lifetime.Token);
        }
    }
    private Task RunCoreAsync(CancellationToken lifetime) => Task.Run(async () =>
    {
        while (!lifetime.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(lifetime);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var request = await OverlayTransport.ReadAsync<OverlayRequest>(pipe, OverlayTransport.RequestLimit, deadline.Token);
                OverlayReply reply;
                try { reply = await handler(request); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or CryptographicException
                    or NotSupportedException or FormatException or System.Runtime.InteropServices.COMException)
                { reply = new(Error: "Could not complete this action. Check the original item and try again."); }
                finally { if (request.Secret is byte[] secret) CryptographicOperations.ZeroMemory(secret); }
                try { await OverlayTransport.WriteReplyAsync(pipe, reply, OverlayTransport.ReplyLimit, deadline.Token); }
                finally { if (reply.Clip?.ImagePng is byte[] png) CryptographicOperations.ZeroMemory(png); }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException or UnauthorizedAccessException) { /* A disconnected or malformed client cannot stop capture. */ }
        }
    });
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _shutdown = StopAsync(_run);
        }
    }
    private async Task StopAsync(Task? running)
    {
        try
        {
            try { await _lifetime.CancelAsync().ConfigureAwait(false); }
            catch (AggregateException) { /* Cancellation stays requested even if a callback fails. */ }
            if (running is not null)
                try { await running.ConfigureAwait(false); }
                catch (Exception) { /* RunAsync still exposes its fault; observe it while draining ownership. */ }
        }
        finally { _lifetime.Dispose(); }
    }
}
