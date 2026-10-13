using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal sealed record OverlayRequest(string Command, string? Id = null, bool Flag = false, AppSettings? Settings = null);
internal sealed record OverlayReply(string? Error = null, AppSettings? Settings = null, long Target = 0, long Version = 0, bool Close = false,
    bool StartupAvailable = true, IReadOnlyList<ClipSummary>? Entries = null, Clip? Clip = null, bool Demo = false, int ListenerProcess = 0, bool Standby = false);

internal static class OverlayTransport
{
    internal const int RequestLimit = 16 * 1024;
    internal const int ReplyLimit = 40 * 1024 * 1024;
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

internal sealed class OverlayClient(string pipeName) : IHistoryAccess, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    internal async Task<OverlayReply> CallAsync(OverlayRequest request)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await _gate.WaitAsync(deadline.Token);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(3000, deadline.Token);
            await OverlayTransport.WriteAsync(pipe, request, OverlayTransport.RequestLimit, deadline.Token);
            var reply = await OverlayTransport.ReadAsync<OverlayReply>(pipe, OverlayTransport.ReplyLimit, deadline.Token);
            if (reply.Error is not null) throw new IOException(reply.Error);
            return reply;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or JsonException or InvalidDataException) { throw new IOException("The clipboard listener is unavailable. Open the rope again.", ex); }
        finally { _gate.Release(); }
    }
    public async Task<IReadOnlyList<ClipSummary>> ListAsync() => (await CallAsync(new("list"))).Entries ?? [];
    public async Task<Clip?> GetAsync(string id) => (await CallAsync(new("get", id))).Clip;
    public async Task SetPinnedAsync(string id, bool pinned) => await CallAsync(new("pin", id, pinned));
    public async Task DeleteAsync(string id) => await CallAsync(new("delete", id));
    public async Task ClearAsync() => await CallAsync(new("clear"));
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }
}

internal sealed class OverlayServer(Func<OverlayRequest, Task<OverlayReply>> handler) : IDisposable
{
    internal string Name { get; } = "ShardApps.Clippboard." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    internal Task RunAsync() => Task.Run(async () =>
    {
        while (!_lifetime.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var request = await OverlayTransport.ReadAsync<OverlayRequest>(pipe, OverlayTransport.RequestLimit, deadline.Token);
                OverlayReply reply;
                try { reply = await handler(request); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or CryptographicException)
                { reply = new(Error: "Could not complete this action. Check the original item and try again."); }
                try { await OverlayTransport.WriteAsync(pipe, reply, OverlayTransport.ReplyLimit, deadline.Token); }
                finally { if (reply.Clip?.ImagePng is byte[] png) CryptographicOperations.ZeroMemory(png); }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or JsonException or UnauthorizedAccessException) { /* A disconnected or malformed client cannot stop capture. */ }
        }
    });
    public void Dispose() { _lifetime.Cancel(); }
}
