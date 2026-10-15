using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.Core;

// Only short summaries remain in memory. Payloads are encrypted independently on disk.
public sealed partial class HistoryStore(string directory, IContentProtector protector, HistoryLimits? limits = null,
    Func<byte[], byte[]>? createPreview = null) : IHistoryAccess
{
    private HistoryLimits _limits = Validate(limits ?? new());
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ClipSummary> _items = new();
    private readonly HashSet<string> _modernPayloads = new();
    private IReadOnlyList<ClipSummary>? _orderedSnapshot;
    public int UnreadableEntries { get; private set; }

    private sealed record StoredSummary(ClipSummary Summary, long ModifiedUtcTicks);
    private static HistoryLimits Validate(HistoryLimits value)
    {
        if (value.MaxEntries < 1 || value.MaxBytes < 1 || value.MaxEntryBytes < 1 || value.RetentionDays is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }

    private string FileFor(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid clip ID.");
        return Path.Combine(directory, id + (_modernPayloads.Contains(id) ? ".clip2" : ".clip"));
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            _items.Clear();
            _modernPayloads.Clear();
            _orderedSnapshot = null;
            UnreadableEntries = 0;
            CompletePendingClear();
            foreach (var file in Directory.EnumerateFiles(directory, "*.tmp")) File.Delete(file);
            foreach (var file in Directory.EnumerateFiles(directory, "*.clip2").Concat(Directory.EnumerateFiles(directory, "*.clip")))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (_items.ContainsKey(id))
                {
                    // A crash can leave the legacy half of a completed migration.
                    try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    continue;
                }
                try
                {
                    var size = new FileInfo(file).Length;
                    if (size > _limits.MaxEntryBytes) throw new InvalidDataException("Entry is too large.");
                    var info = new FileInfo(file);
                    var cached = await ReadSummaryAsync(file, info);
                    if (cached is not null)
                    {
                        if (Path.GetExtension(file) == ".clip2") _modernPayloads.Add(cached.Id);
                        _items[cached.Id] = cached; continue;
                    }
                    var clip = await ReadFileAsync(file);
                    try
                    {
                        if (!string.Equals(Path.GetFileNameWithoutExtension(file), clip.Id, StringComparison.Ordinal))
                            throw new InvalidDataException("Entry identity mismatch.");
                        if (Path.GetExtension(file) == ".clip2") _modernPayloads.Add(clip.Id);
                        _ = FileFor(clip.Id);
                        _items[clip.Id] = clip.Summary(size);
                        await CacheSummaryAsync(_items[clip.Id]);
                    }
                    finally { if (clip.ImagePng is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(clip.ImagePng); }
                }
                catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
                {
                    // Unreadable history must not silently consume the storage budget forever.
                    UnreadableEntries++;
                    File.Delete(file);
                    File.Delete(Path.ChangeExtension(file, ".meta"));
                    _modernPayloads.Remove(id); _items.Remove(id); _orderedSnapshot = null;
                }
            }
            foreach (var metadata in Directory.EnumerateFiles(directory, "*.meta"))
                if (!_items.ContainsKey(Path.GetFileNameWithoutExtension(metadata))) File.Delete(metadata);
            LoadPreviews();
            Trim(DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ClipSummary>> ListAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Trim(DateTimeOffset.UtcNow);
            return _orderedSnapshot ??= Array.AsReadOnly(_items.Values.OrderByDescending(x => x.Pinned).ThenByDescending(x => x.CopiedAt).ToArray());
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> AddAsync(Clip clip, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            return await SaveLockedAsync(clip, !clip.IsSnippet);
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> SaveLockedAsync(Clip clip, bool deduplicate)
    {
        Trim(DateTimeOffset.UtcNow);
        var fingerprint = clip.Fingerprint();
        var duplicate = deduplicate ? _items.Values.FirstOrDefault(x => x.Fingerprint == fingerprint) : null;
        if (duplicate is not null) clip = clip with { Id = duplicate.Id, Pinned = duplicate.Pinned };
        var bytes = PreparePayload(clip, fingerprint);
        if (bytes.Length > _limits.MaxEntryBytes) return false;
        // Plan eviction first: a rejected addition must not remove existing history.
        var candidates = _items.Values.Where(x => x.Id != clip.Id && !x.Pinned)
            .OrderBy(x => x.CopiedAt).ToList();
        var count = _items.Count - (_items.ContainsKey(clip.Id) ? 1 : 0) + 1;
        var total = _items.Values.Where(x => x.Id != clip.Id).Sum(x => x.StoredBytes) + bytes.Length;
        var evictions = new List<string>();
        foreach (var victim in candidates)
        {
            if (count <= _limits.MaxEntries && total <= _limits.MaxBytes) break;
            evictions.Add(victim.Id);
            count--;
            total -= victim.StoredBytes;
        }
        if (count > _limits.MaxEntries || total > _limits.MaxBytes) return false;
        await WriteAsync(clip.Id, bytes);
        foreach (var id in evictions) Remove(id);
        _items[clip.Id] = clip.Summary(bytes.Length, fingerprint);
        _orderedSnapshot = null;
        TrimPreviews();
        await CacheSummaryAsync(_items[clip.Id]);
        return true;
    }

    public async Task<bool> SaveSnippetAsync(string? id, string text, string title, string[] labels, CancellationToken cancellation = default)
    {
        TextReuse.ValidateSnippet(text, title, labels);
        await _gate.WaitAsync(cancellation);
        Clip? existing = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            Trim(DateTimeOffset.UtcNow);
            if (id is not null)
            {
                if (!_items.TryGetValue(id, out var summary) || !summary.IsSnippet || summary.Kind != ClipKind.Text)
                    throw new InvalidOperationException("This snippet expired or was deleted. Save a new copy instead.");
                existing = await ReadFileAsync(FileFor(id));
                if (!existing.IsSnippet || existing.Kind != ClipKind.Text) throw new InvalidDataException("Invalid snippet payload.");
            }
            var snippet = (existing ?? new Clip { Kind = ClipKind.Text, Pinned = true }) with
                { IsSnippet = true, Text = text, Title = title.Trim(), Labels = labels.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
            return await SaveLockedAsync(snippet, false);
        }
        finally
        {
            if (existing?.ImagePng is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(existing.ImagePng);
            _gate.Release();
        }
    }

    public async Task<Clip?> GetAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            Trim(DateTimeOffset.UtcNow);
            return _items.ContainsKey(id) ? await ReadFileAsync(FileFor(id)) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task SetPinnedAsync(string id, bool pinned)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_items.ContainsKey(id)) return;
            var clip = (await ReadFileAsync(FileFor(id))) with { Pinned = pinned };
            try
            {
                var fingerprint = clip.Fingerprint();
                var bytes = PreparePayload(clip, fingerprint);
                await WriteAsync(id, bytes);
                _items[id] = clip.Summary(bytes.Length, fingerprint); _orderedSnapshot = null;
                await CacheSummaryAsync(_items[id]);
                Trim(DateTimeOffset.UtcNow);
            }
            finally { if (clip.ImagePng is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(clip.ImagePng); }
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _gate.WaitAsync();
        try { if (_items.ContainsKey(id)) Remove(id); }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync();
        try
        {
            // A crash during deletion must not silently leave part of a requested purge.
            var pending = Path.Combine(directory, "clear.pending");
            await File.WriteAllBytesAsync(pending, []);
            CompletePendingClear();
        }
        finally { _gate.Release(); }
    }

    public async Task PruneAsync()
    {
        await _gate.WaitAsync();
        try { Trim(DateTimeOffset.UtcNow); }
        finally { _gate.Release(); }
    }

    public async Task<bool> UpdateLimitsAsync(HistoryLimits limits)
    {
        limits = Validate(limits);
        await _gate.WaitAsync();
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-limits.RetentionDays);
            var pins = _items.Values.Where(x => x.Pinned && x.CopiedAt >= cutoff).ToArray();
            if (pins.Length > limits.MaxEntries || pins.Sum(x => x.StoredBytes) > limits.MaxBytes) return false;
            _limits = limits;
            Trim(DateTimeOffset.UtcNow);
            return true;
        }
        finally { _gate.Release(); }
    }

    private void Trim(DateTimeOffset now)
    {
        CompletePendingClear();
        var cutoff = now.AddDays(-_limits.RetentionDays);
        List<string>? expired = null;
        foreach (var item in _items.Values)
            if (item.CopiedAt < cutoff) (expired ??= new()).Add(item.Id);
        if (expired is not null) foreach (var id in expired) Remove(id);
        var total = _items.Values.Sum(x => x.StoredBytes);
        if (_items.Count > _limits.MaxEntries || total > _limits.MaxBytes)
        {
            foreach (var item in _items.Values.Where(x => !x.Pinned).OrderBy(x => x.CopiedAt).ToArray())
            {
                if (_items.Count <= _limits.MaxEntries && total <= _limits.MaxBytes) break;
                total -= item.StoredBytes; Remove(item.Id);
            }
        }
        TrimPreviews();
    }

    private void CompletePendingClear()
    {
        var pending = Path.Combine(directory, "clear.pending");
        if (!File.Exists(pending)) return;
        foreach (var extension in new[] { "*.clip", "*.clip2", "*.meta", "*.thumb", "*.tmp" })
            foreach (var file in Directory.EnumerateFiles(directory, extension)) File.Delete(file);
        File.Delete(pending);
        _items.Clear(); _modernPayloads.Clear(); _previewSizes.Clear(); _orderedSnapshot = null;
    }

    private void Remove(string id)
    {
        File.Delete(FileFor(id));
        File.Delete(Path.Combine(directory, id + (_modernPayloads.Contains(id) ? ".clip" : ".clip2")));
        File.Delete(Path.ChangeExtension(FileFor(id), ".meta"));
        File.Delete(Path.ChangeExtension(FileFor(id), ".thumb"));
        _items.Remove(id);
        _modernPayloads.Remove(id);
        _orderedSnapshot = null;
        _previewSizes.Remove(id);
    }

    private async Task<Clip> ReadFileAsync(string file)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous);
        if (input.Length > _limits.MaxEntryBytes) throw new InvalidDataException("Entry is too large.");
        var image = await TryReadImageAsync(input);
        if (image is not null) return image;
        var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes);
        var plaintext = protector.Unprotect(bytes);
        try { return JsonSerializer.Deserialize<Clip>(plaintext) ?? throw new InvalidDataException("Empty entry."); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] Encrypt<T>(T value, JsonSerializerOptions? options = null)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, options);
        try { return protector.Protect(plaintext); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task<ClipSummary?> ReadSummaryAsync(string payloadFile, FileInfo info)
    {
        var metadata = Path.ChangeExtension(payloadFile, ".meta");
        if (!File.Exists(metadata) || new FileInfo(metadata).Length > 64 * 1024) return null;
        try
        {
            var plaintext = protector.Unprotect(await File.ReadAllBytesAsync(metadata));
            try
            {
                var cached = JsonSerializer.Deserialize<StoredSummary>(plaintext);
                if (cached?.Summary is not { } summary || summary.Id is null || summary.Preview is null || summary.Preview.Length > 320
                    || cached.ModifiedUtcTicks != info.LastWriteTimeUtc.Ticks || summary.StoredBytes != info.Length
                    || summary.Id != Path.GetFileNameWithoutExtension(payloadFile) || !Enum.IsDefined(summary.Kind)
                    || summary.Title?.Length > 64 || summary.Labels?.Length > 8 || (summary.Labels?.Any(label => label is null || label.Length > 24) ?? false)
                    || (summary.IsSnippet && summary.Kind != ClipKind.Text) || !ValidFingerprint(summary)) return null;
                _ = FileFor(cached.Summary.Id);
                return cached.Summary;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException or ArgumentException) { return null; }
    }

    private static bool ValidFingerprint(ClipSummary summary)
    {
        var prefix = (summary.IsSnippet ? "Snippet" : summary.Kind.ToString()) + ":";
        return summary.Fingerprint is not null && summary.Fingerprint.StartsWith(prefix, StringComparison.Ordinal)
            && summary.Fingerprint.Length == prefix.Length + 64 && summary.Fingerprint[prefix.Length..].All(Uri.IsHexDigit);
    }

    private async Task CacheSummaryAsync(ClipSummary summary)
    {
        var file = FileFor(summary.Id);
        var metadata = Path.ChangeExtension(file, ".meta");
        var temporary = metadata + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, Encrypt(new StoredSummary(summary, new FileInfo(file).LastWriteTimeUtc.Ticks)));
            File.Move(temporary, metadata, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { /* Payload remains authoritative; rebuild the optional summary on restart. */ }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Retry encrypted cache cleanup on startup. */ }
        }
    }

    private async Task WriteAsync(string id, PreparedPayload bytes)
    {
        var previous = FileFor(id);
        var file = Path.ChangeExtension(previous, bytes.Modern ? ".clip2" : ".clip");
        var temporary = file + ".tmp";
        try
        {
            if (bytes.Image is null) await File.WriteAllBytesAsync(temporary, bytes.Metadata);
            else
            {
                await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous))
                {
                    var header = new byte[ImageHeaderBytes]; ImageMagic.CopyTo(header);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), bytes.Metadata.Length);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), bytes.Image.Length);
                    await output.WriteAsync(header); await output.WriteAsync(bytes.Metadata); await output.WriteAsync(bytes.Image);
                    await output.FlushAsync();
                }
            }
            File.Move(temporary, file, true);
            if (previous != file)
            {
                try { File.Delete(previous); }
                catch { File.Delete(file); throw; } // Keep the original authoritative if migration cannot finish.
            }
            if (bytes.Modern) _modernPayloads.Add(id); else _modernPayloads.Remove(id);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
