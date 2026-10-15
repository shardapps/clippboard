using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clippboard.Core;

public static class TextCombination
{
    public const int MaxNotes = 1000;
    public static async Task<string> BuildAsync(IHistoryAccess store, IReadOnlyList<string> ids, string separator, CancellationToken cancellation = default)
    {
        if (ids.Count is < 1 or > MaxNotes || separator.Length > 32) throw new ArgumentException("Select 1–1,000 text notes and a separator up to 32 characters.");
        var result = new StringBuilder(); var seen = new HashSet<string>();
        foreach (var id in ids)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!seen.Add(id)) throw new ArgumentException("A note can appear only once in the combination.");
            var clip = await store.GetAsync(id);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (clip is null) throw new IOException("A selected note expired or was deleted. Select your notes again.");
                if (clip.Kind != ClipKind.Text) throw new ArgumentException("Only text notes can be combined.");
                var text = clip.Text ?? ""; var separatorLength = seen.Count > 1 ? separator.Length : 0;
                if (text.Length > TextReuse.MaxTextLength - result.Length - separatorLength) throw new ArgumentException("The combined text exceeds the text limit. Select fewer notes.");
                if (seen.Count > 1) result.Append(separator);
                result.Append(text);
            }
            finally { if (clip?.ImagePng is not null) CryptographicOperations.ZeroMemory(clip.ImagePng); }
        }
        return result.ToString();
    }
}
