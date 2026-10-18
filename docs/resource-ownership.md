# Resource ownership in development builds

Reviewed October 18, 2026. This records implementation and automated evidence, not all-device certification.

| Resource | Owner and release rule |
| --- | --- |
| Tray, clipboard listener and hotkeys | Listener process; disposed on application exit. The listener uses a message-only native window. |
| Rope native surface, controls and renderer buffers | Disposable overlay process. Retirement removes hooks, rendering callbacks, static appearance events, timers and controls, then the child exits. |
| Prepared runtime | Runtime only, or empty overlay controls when affordable; no native surface, history reads or image previews. One bounded initial runtime probe learns the child's own cost. Later starts/retention and optional control preparation follow measured costs and current physical/commit headroom. The FastOpen preference disables both tiers. |
| Session-switch subscription | Listener; unsubscribed on exit, with shutdown guards before dispatching lock work. |
| Pipe server | One reusable worker per session. Shutdown cancels, drains the worker/callbacks, then disposes its cancellation source. Closed sessions cannot send actions to a replacement session. |
| Pipe client | One admitted background exchange per client. Waiting/canceled calls do not queue additional worker jobs; async pipe waits release the worker. JSON parsing never resumes on the UI synchronization context. |
| Active catalog | Sender retains one bounded summary snapshot per overlay session. The overlay retains a read-only replica; later replies carry changed summaries, removed IDs and exact ID ordering. Revision mismatch resends a full snapshot, and malformed updates cannot partially modify the replica. No original payloads enter this catalog. |
| Restore action | Captures both session and lock leases before fetching the original. Closing/locking cancels pending work. An active WIC decode finishes before its caller can wipe the PNG; cancellation prevents the clipboard setter. File probes can abandon the caller while their single underlying OS worker remains bounded. |
| Clipboard setter | Runs on the listener STA. Its own reentrant OLE notifications are suppressed only during the synchronous setter; genuine copies during background decode can still be captured. |
| PNG/IPC/password buffers | Owned buffers are wiped at their documented transfer boundary, including error paths. Immutable managed strings and OS/native buffers cannot be promised to be securely erased. |
| Previews and expanded images | Bounded derived files plus adaptive decoded LRU retention. Thumbnail and original-detail reads/decodes share the overlay's memory-aware worker slots. A canceled in-flight WIC operation keeps its slot until it finishes; queued cancellations never read an original. Expanded images fit the maximum overlay size in physical pixels, remain outside the thumbnail LRU, and release their encoded originals after decoding. Source raster/scratch and displayed detail buffers are additional to the cache ceiling. |
| File/shell/OCR/destination workers | Bounded slots and caller cancellation/deadlines. Some Windows calls cannot be interrupted; retrying cannot spawn unbounded replacement workers. |
| Backup jobs | One background transfer/review; explicit commit and encrypted staging cleanup/recovery. External exported backups remain until the user deletes them. |

Local regression checks exercise duplicate pipe startup, shutdown drain, disposal rejection, delayed replies, panel detachment, canceled image queues and repeated surface retirement. Isolated CI additionally exercises real clipboard notifications, restored image pixels/dimensions, lock/cancellation and native hotkey/paste. No real personal clipboard is used for local tests.

No application optimization forces GC or trims a process working set. Process-private bytes, managed allocation and working-set samples are different measurements; shared driver/framework pages and active image buffers remain additional. Representative device, sustained workload, Store-installed and presentation checks remain in the [beta checklist](beta-checklist.md).

Beta.5 adds session-owned native note windows and DIB pixel buffers. One shared raster scratch buffer feeds the bounded viewport surfaces; no worker thread is created per note. The owning WPF dispatcher handles UI/input, existing bounded workers handle clipboard/storage/IPC/decoding, and Windows composites cached movement. Hover/content changes invalidate affected sources; outer transforms move cached pixels. Descriptor handlers, global motion notifications and rendering subscriptions detach; native source/rotation buffers are wiped before deletion. Pressure/error fallback restores the existing WPF controls and does not retry in an allocation loop. Regression checks verify native HWND destruction directly, rather than waiting for garbage collection.
