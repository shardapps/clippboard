using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.Core;

public static class HistorySearch
{
    public const int BatchSize = 8;
    public static bool Contains(string? text, string query) => text is not null
        && CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase) >= 0;

    // No plaintext index or resident full-text corpus. Yield between small pages
    // so preview/restore/state requests can use the listener while searching.
    public static async Task<IReadOnlyList<string>> MatchBatchAsync(IHistoryAccess store, string query,
        IReadOnlyList<string> ids, CancellationToken cancellation = default)
    {
        if (query.Length > 100 || ids.Count > BatchSize) throw new ArgumentException("Search exceeds its bounds.");
        var found = new List<string>();
        foreach (var id in ids)
        {
            cancellation.ThrowIfCancellationRequested();
            var clip = await store.GetAsync(id);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (clip is null) continue;
                var matches = clip.Kind switch
                {
                    ClipKind.Text => Contains(clip.Text, query),
                    ClipKind.Files => Array.Exists(clip.FilePaths ?? [], path => Contains(path, query)),
                    _ => false
                };
                if (matches) found.Add(id);
            }
            finally { if (clip?.ImagePng is not null) CryptographicOperations.ZeroMemory(clip.ImagePng); }
        }
        return found;
    }
}
