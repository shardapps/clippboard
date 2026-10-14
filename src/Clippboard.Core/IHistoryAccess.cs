using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace Clippboard.Core;

// The listener owns storage. The temporary overlay only requests bounded views.
public interface IHistoryAccess
{
    Task<IReadOnlyList<ClipSummary>> ListAsync();
    Task<Clip?> GetAsync(string id);
    // Consumers own and clear the returned encoded image after decoding.
    async Task<byte[]?> GetPreviewAsync(string id) => (await GetAsync(id))?.ImagePng;
    Task<IReadOnlyList<string>> SearchBatchAsync(string query, IReadOnlyList<string> ids, CancellationToken cancellation = default)
        => HistorySearch.MatchBatchAsync(this, query, ids, cancellation);
    Task SetPinnedAsync(string id, bool pinned);
    Task DeleteAsync(string id);
    Task ClearAsync();
}
