using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Clippboard.Core;

namespace Clippboard.App.Services;

internal sealed record CatalogUpdate(long BaseVersion, long Version, bool Full, ClipSummary[] Entries, string[] Removed, string[]? Order = null);

// One sender per disposable overlay session, using the store's bounded read-only
// summary snapshot. A missed/canceled reply is repaired by a full snapshot.
internal sealed class CatalogSender
{
    private IReadOnlyList<ClipSummary> _sent = [];
    private long _version;
    internal CatalogUpdate Create(IReadOnlyList<ClipSummary> entries, long expectedVersion)
    {
        if (entries.Count > AppSettings.MaximumEntries) throw new InvalidDataException("Catalog exceeds its limit.");
        var full = _version == 0 || expectedVersion != _version || _version == long.MaxValue;
        if (!full && ReferenceEquals(entries, _sent)) return new(_version, _version, false, [], []);
        var prior = _sent.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var current = entries.ToDictionary(row => row.Id, StringComparer.Ordinal);
        var changed = full ? entries.ToArray() : entries.Where(row => !prior.TryGetValue(row.Id, out var old) || !old.ContentEquals(row)).ToArray();
        string[] removed = full ? [] : prior.Keys.Where(id => !current.ContainsKey(id)).ToArray();
        // Carry exact ordering (including timestamp ties) without resending the
        // text/title/labels of all unchanged entries.
        var reordered = !full && !_sent.Select(row => row.Id).SequenceEqual(entries.Select(row => row.Id), StringComparer.Ordinal);
        if (!full && changed.Length == 0 && removed.Length == 0 && !reordered)
        { _sent = entries; return new(_version, _version, false, [], []); }
        var next = _version == long.MaxValue ? 1 : _version + 1;
        var update = new CatalogUpdate(expectedVersion, next, full, changed, removed, full ? null : entries.Select(row => row.Id).ToArray());
        _sent = entries; _version = next; return update;
    }
}

internal sealed class CatalogReplica
{
    private Dictionary<string, ClipSummary> _rows = new(StringComparer.Ordinal);
    private IReadOnlyList<ClipSummary> _snapshot = Array.Empty<ClipSummary>();
    internal long Version { get; private set; }
    internal void Reset() { Version = 0; _rows.Clear(); _snapshot = Array.Empty<ClipSummary>(); }
    internal IReadOnlyList<ClipSummary> Apply(CatalogUpdate update)
    {
        if (update.Version <= 0 || update.Entries is null || update.Removed is null
            || update.Entries.Length > AppSettings.MaximumEntries || update.Removed.Length > AppSettings.MaximumEntries
            || update.Order?.Length > AppSettings.MaximumEntries || (!update.Full && update.BaseVersion != Version))
            throw new InvalidDataException("Invalid catalog revision.");
        var empty = update.Entries.Length == 0 && update.Removed.Length == 0 && update.Order is null;
        if (!update.Full && update.Version == Version && empty) return _snapshot;
        if (!update.Full && (Version == long.MaxValue || update.Version != Version + 1)) throw new InvalidDataException("Invalid catalog sequence.");
        if (update.Full && (update.Removed.Length != 0 || update.Order is not null)) throw new InvalidDataException("Invalid catalog replacement.");
        // Validate into a temporary map. A malformed update never partially
        // changes the current catalog or its revision.
        var next = update.Full ? new Dictionary<string, ClipSummary>(StringComparer.Ordinal) : new(_rows, StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in update.Entries)
        {
            if (row is null || string.IsNullOrEmpty(row.Id) || !changed.Add(row.Id)) throw new InvalidDataException("Duplicate catalog entry.");
            next[row.Id] = row;
        }
        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in update.Removed)
        {
            if (string.IsNullOrEmpty(id) || !removed.Add(id) || changed.Contains(id) || !next.Remove(id)) throw new InvalidDataException("Invalid catalog removal.");
        }
        if (next.Count > AppSettings.MaximumEntries) throw new InvalidDataException("Catalog exceeds its limit.");
        ClipSummary[] ordered;
        if (update.Full) ordered = update.Entries.ToArray();
        else
        {
            if (update.Order is null || update.Order.Length != next.Count) throw new InvalidDataException("Missing catalog order.");
            var seen = new HashSet<string>(StringComparer.Ordinal); ordered = new ClipSummary[next.Count];
            for (var index = 0; index < ordered.Length; index++)
            {
                var id = update.Order[index];
                if (string.IsNullOrEmpty(id) || !seen.Add(id) || !next.TryGetValue(id, out var row)) throw new InvalidDataException("Invalid catalog order.");
                ordered[index] = row;
            }
        }
        _rows = next; _snapshot = Array.AsReadOnly(ordered); Version = update.Version; return _snapshot;
    }
}
