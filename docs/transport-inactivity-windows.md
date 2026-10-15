# Transport inactivity windows

This note supplements the transport and compatibility sections in `PROJECT_CONTEXT.md`.
The change starts from `master` commit `3b0adea29f265f65a5a0bb09af7767ac0c6ffc27`.

## Release 1.0.11

Config sync is more reliable on slow connections. Transfers can continue while they make progress, rather than failing only because they take longer.
Update CCS to 1.0.11 or newer on both the server and affected clients for the full improvement.
Older compatible CCS versions can still connect, but retain their previous waiting rules.
No new settings are required. Other mods do not need to be rebuilt, and the optional API still requires CCS 1.0.10 or newer.

## Scope

Keep the existing packet format, RPC names, admission checks, queue ordering, and size and memory limits.
Change only how CCS decides that a send wait or an incomplete fragmented package has stopped making progress.
There are no new configuration files, negotiated timeout values, RPCs, or application-level acknowledgements.

Package version is 1.0.11, CCS protocol remains 1, core `AssemblyVersion` remains 1.0.0.0, and the soft-dependency bridge contract remains 1.
Existing consumer mods do not need to be rebuilt for this transport change.

## Waiting to send

The existing 30-second limit applies to one wait for the connection's outgoing queue, not to the entire connection or full synchronization.
Previously, it expired 30 seconds after entering that wait even if the queue was shrinking.

The sender now remembers the previous queue sample and the time of the last observed decrease.
It continues to use the existing resume threshold of 20000.

- If the current queue is at or below the threshold, send as before.
- If it is smaller than the previous sample, restart the inactivity window before checking elapsed time.
- If it is unchanged or larger, keep the previous progress timestamp.
- If it remains above the threshold for more than 30 seconds without an observed decrease, send the existing connection error and disconnect the peer.

A slow queue may take longer than 30 seconds to drain. There is no additional absolute deadline while observed progress continues.
Each new packet or fragment wait gets its own timestamp, as before. The timestamps are iterator-local and cannot carry over to another peer or session.
Peer identity, socket connection, send-slot ownership, and session-generation checks remain unchanged.

The queue belongs to the whole connection. Its decrease is a transport progress signal, not proof that a specific consumer has applied its settings.
Other outgoing traffic can mask progress between samples. A queued send, a larger queue, a live socket, or an unrelated incoming ping does not by itself reset the CCS timer.
No transport-specific ACK inspection or statistics-reset calls are added.

On a timeout, the warning reports the inactivity limit, queue sample, resume threshold, and total duration of this wait.
Total wait duration is diagnostic only; it is not a second disconnect deadline.

## Receiving fragments

An incomplete package now expires 60 seconds after its last accepted new fragment, rather than 60 seconds after its first fragment.
Each consumer keeps one deadline per sender/package identifier in `cacheExpirations`.
A dictionary replaces the old list so renewing a deadline cannot accumulate duplicate expiry records.

Before checking cache budgets, receipt of a fragment sweeps unrelated expired assemblies while temporarily preserving the current candidate.
The existing count, index, size, consistency, duplicate, and memory-budget checks still run.
Only after a new fragment passes those checks and is stored does CCS renew its deadline.
An invalid or duplicate fragment never renews the window; the existing discard/rejection behavior is retained.

This ordering matters after a long local frame. A valid continuation may already be waiting for processing.
If its assembly still exists, CCS can accept that continuation instead of deleting the earlier fragments immediately before reading it.
A late continuation can therefore preserve an assembly that has not yet been swept.
This is deliberate; the deadline remains a receive-driven cache cleanup rule, not a strict on-the-clock rejection of a fragment.
If another incoming message already removed the assembly, this change cannot recover those discarded fragments.

Unfragmented messages and malformed/oversized input still trigger receive-driven expiry cleanup.
A generation check prevents fallback cleanup from touching a replacement session after a callback resets transport state.
No periodic or background expiry sweep is added.
Completion, rejection, disconnect, and session reset continue to use `RemoveFragmentAssembly`, which also removes the deadline.

Both send and receive windows use `Stopwatch.GetTimestamp()` and `Stopwatch.Frequency` for elapsed-time accounting instead of calendar timestamps or frame counts.
The receive path no longer uses `DateTimeOffset.Now` to expire fragments.

## Compatibility

No bytes on the wire change. Existing sizes and counts are unchanged, including 250000-byte outgoing fragments, the 300000-byte receive limit, 128 fragments, and the 20 MiB payload limit.
These are local waiting rules, so no additional handshake or version gate is needed.

| Sender | Receiver | Behavior |
| --- | --- | --- |
| Previous implementation | Previous implementation | Existing absolute send-wait and fragment-cache limits |
| Inactivity implementation | Previous implementation | Sender allows a shrinking queue more time; receiver still has its old 60-second cache lifetime |
| Previous implementation | Inactivity implementation | Receiver renews fragment deadlines; sender still has its old 30-second send wait |
| Inactivity implementation | Inactivity implementation | Both local progress-based windows apply |

The same matrix applies to server-to-client and permitted client-to-server transfers.
For a slow fragmented transfer, update CCS on both the server and the affected client.
Other compatible clients do not have to update at the same time.
Updating only the server can still help a long wait before the first fragment or an unfragmented packet.
It cannot change fragment expiry inside an older client DLL.

A previous receiver may discard earlier fragments of a transfer that lasts over 60 seconds and remain without a complete initial sync.
That receiver limitation is not repaired by a more patient sender. Do not describe a server-only update as a universal slow-client fix.
Vanilla RPC, Steam, PlayFab, and other mods' timeouts remain independent and are not modified.

## Maintainer validation checklist

These are runtime scenarios to check in the maintainer's Valheim environment, not test results from this change.

1. Connect with matching consumers using unfragmented, compressed, and fragmented packages. Initial sync, buffered vanilla traffic, and subsequent config updates must retain their existing order.
2. Keep a queue above 20000 for over 30 seconds with regular decreasing samples. The send must continue waiting; dropping to 20000 or below must resume it.
3. Keep a queue flat, or only growing, above the threshold for over 30 seconds. The existing connection-error/disconnect path must run.
4. Pause the sender's local updates, then resume with a smaller queue or a queue below the threshold. Read the fresh sample before deciding on a timeout. With no observed decrease, the normal inactivity deadline still applies.
5. Receive fragments at elapsed times 0, 25, 50, and 75 seconds. The incomplete assembly must survive and finish, although its total duration exceeds 60 seconds.
6. Receive a valid late continuation while its assembly is still cached. Preserve that assembly through current-message validation. Receive unrelated traffic after an assembly's idle deadline and verify that it is removed rather than renewed.
7. Repeat duplicate, changed-count, oversized, and cache-budget rejection cases. None may renew a deadline or weaken an existing limit. Expired unrelated assemblies must release their budget before a new valid fragment is admitted.
8. Disconnect during a send wait and during a fragmented receive. Reconnect, reset the session, and check that neither progress timestamps nor fragment deadlines carry into the new connection.
9. Repeat initial and subsequent sends with multiple consumers and multiple peers, including one slow peer. The existing send reservations, sequencing, and buffered socket cleanup must remain intact.
10. Check the mixed-version combinations above. Verify ordinary interoperability separately from the full slow-transfer improvement, which needs both updated directions.
