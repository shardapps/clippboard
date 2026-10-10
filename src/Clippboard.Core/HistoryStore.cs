using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.Core;

// Only short summaries remain in memory. Payloads are encrypted independently on disk.
public sealed class HistoryStore(string directory, IContentProtector protector, HistoryLimits? limits = null)
{
    private readonly HistoryLimits _limits = limits ?? new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ClipSummary> _items = new();
    public int UnreadableEntries { get; private set; }

    private string FileFor(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid clip ID.");
        return Path.Combine(directory, id + ".clip");
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.tmp")) File.Delete(file);
            foreach (var file in Directory.EnumerateFiles(directory, "*.clip"))
            {
                try
                {
                    var size = new FileInfo(file).Length;
                    if (size > _limits.MaxEntryBytes) throw new InvalidDataException("Entry is too large.");
                    var clip = await ReadFileAsync(file);
                    if (!string.Equals(Path.GetFileName(file), clip.Id + ".clip", StringComparison.Ordinal))
                        throw new InvalidDataException("Entry identity mismatch.");
                    _ = FileFor(clip.Id);
                    _items[clip.Id] = clip.Summary(size);
                }
                catch (Exception ex) when (ex is IOException or JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
                {
                    // Unreadable history must not silently consume the storage budget forever.
                    UnreadableEntries++;
                    File.Delete(file);
                }
            }
            Trim(DateTimeOffset.UtcNow);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ClipSummary>> ListAsync()
    {
        await _gate.WaitAsync();
        try { return _items.Values.OrderByDescending(x => x.Pinned).ThenByDescending(x => x.CopiedAt).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> AddAsync(Clip clip)
    {
        await _gate.WaitAsync();
        try
        {
            Trim(DateTimeOffset.UtcNow);
            var fingerprint = clip.Fingerprint();
            var duplicate = _items.Values.FirstOrDefault(x => x.Fingerprint == fingerprint);
            if (duplicate is not null) clip = clip with { Id = duplicate.Id, Pinned = duplicate.Pinned };
            var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(clip));
            if (bytes.Length > _limits.MaxEntryBytes) return false;
            // Plan eviction first: a rejected addition must not remove existing history.
            var candidates = _items.Values.Where(x => x.Id != clip.Id && !x.Pinned)
                .OrderBy(x => x.CopiedAt).ToList();
            var count = _items.Count - (duplicate is null ? 0 : 1) + 1;
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
            _items[clip.Id] = clip.Summary(bytes.Length);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<Clip?> GetAsync(string id)
    {
        await _gate.WaitAsync();
        try { return _items.ContainsKey(id) ? await ReadFileAsync(FileFor(id)) : null; }
        finally { _gate.Release(); }
    }

    public async Task SetPinnedAsync(string id, bool pinned)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_items.ContainsKey(id)) return;
            var clip = (await ReadFileAsync(FileFor(id))) with { Pinned = pinned };
            var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(clip));
            await WriteAsync(id, bytes);
            _items[id] = clip.Summary(bytes.Length);
            Trim(DateTimeOffset.UtcNow);
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
        try { foreach (var id in _items.Keys.ToArray()) Remove(id); }
        finally { _gate.Release(); }
    }

    public async Task PruneAsync()
    {
        await _gate.WaitAsync();
        try { Trim(DateTimeOffset.UtcNow); }
        finally { _gate.Release(); }
    }

    private void Trim(DateTimeOffset now)
    {
        foreach (var item in _items.Values.Where(x => !x.Pinned && x.CopiedAt < now.AddDays(-_limits.RetentionDays)).ToArray())
            Remove(item.Id);
        foreach (var item in _items.Values.Where(x => !x.Pinned).OrderBy(x => x.CopiedAt).ToArray())
        {
            if (_items.Count <= _limits.MaxEntries && _items.Values.Sum(x => x.StoredBytes) <= _limits.MaxBytes) break;
            Remove(item.Id);
        }
    }

    private void Remove(string id)
    {
        File.Delete(FileFor(id));
        _items.Remove(id);
    }

    private async Task<Clip> ReadFileAsync(string file)
    {
        if (new FileInfo(file).Length > _limits.MaxEntryBytes) throw new InvalidDataException("Entry is too large.");
        var bytes = await File.ReadAllBytesAsync(file);
        return JsonSerializer.Deserialize<Clip>(protector.Unprotect(bytes)) ?? throw new InvalidDataException("Empty entry.");
    }

    private async Task WriteAsync(string id, byte[] bytes)
    {
        var file = FileFor(id);
        var temporary = file + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
