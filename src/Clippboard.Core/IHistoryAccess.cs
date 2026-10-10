using System.Collections.Generic;
using System.Threading.Tasks;

namespace Clippboard.Core;

// The listener owns storage. The temporary overlay only requests bounded views.
public interface IHistoryAccess
{
    Task<IReadOnlyList<ClipSummary>> ListAsync();
    Task<Clip?> GetAsync(string id);
    Task SetPinnedAsync(string id, bool pinned);
    Task DeleteAsync(string id);
    Task ClearAsync();
}
