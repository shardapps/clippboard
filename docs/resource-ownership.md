# Resource ownership in development builds

Reviewed October 16, 2026. This records implementation and automated evidence, not all-device certification.

| Resource | Owner and release rule |
| --- | --- |
| Tray, clipboard listener and hotkeys | Listener process; disposed on application exit. The listener uses a message-only native window. |
| Rope native surface, controls and renderer buffers | Disposable overlay process. Retirement removes hooks, rendering callbacks, static appearance events, timers and controls, then the child exits. |
| Prepared runtime | No window/control tree while idle; current memory headroom and measured private cost decide whether to keep it. |
| Session-switch subscription | Listener; unsubscribed on exit, with shutdown guards before dispatching lock work. |
| Pipe server | One reusable worker per session. Shutdown cancels, drains the worker/callbacks, then disposes its cancellation source. Closed sessions cannot send actions to a replacement session. |
| Restore action | Captures both session and lock leases before fetching the original. Closing/locking cancels pending work. An active WIC decode finishes before its caller can wipe the PNG; cancellation prevents the clipboard setter. File probes can abandon the caller while their single underlying OS worker remains bounded. |
| Clipboard setter | Runs on the listener STA. Its own reentrant OLE notifications are suppressed only during the synchronous setter; genuine copies during background decode can still be captured. |
| PNG/IPC/password buffers | Owned buffers are wiped at their documented transfer boundary, including error paths. Immutable managed strings and OS/native buffers cannot be promised to be securely erased. |
| Previews | Bounded derived files plus adaptive decoded LRU retention. Detail/original decode buffers are additional to the cache ceiling. |
| File/shell/OCR/destination workers | Bounded slots and caller cancellation/deadlines. Some Windows calls cannot be interrupted; retrying cannot spawn unbounded replacement workers. |
| Backup jobs | One background transfer/review; explicit commit and encrypted staging cleanup/recovery. External exported backups remain until the user deletes them. |

Local regression checks exercise duplicate pipe startup, shutdown drain, disposal rejection, delayed replies, panel detachment, canceled image queues and repeated surface retirement. Isolated CI additionally exercises real clipboard notifications, restored image pixels/dimensions, lock/cancellation and native hotkey/paste. No real personal clipboard is used for local tests.

No application optimization forces GC or trims a process working set. Process-private bytes, managed allocation and working-set samples are different measurements; shared driver/framework pages and active image buffers remain additional. Representative device, sustained workload, Store-installed and presentation checks remain in the [beta checklist](beta-checklist.md).