# Repository-wide review - 2026-09-08

## Baseline and scope

The independent review started from `master` commit `d0dcaa36fd6bc19e424cf2b092a7b926102f0fef`. All corrections are on `fix/full-repository-review-20260908`. The source corrections through `a86078cf4bf8273920833e3e1fb394642a065300` precede this report.

The review assumes clients run the intended, compatible mod version. Hostile clients, modified clients, deliberately malformed packets, adversarial deserialization, and speculative security hardening are outside this task. Existing validation and permission checks were retained. Findings below concern ordinary operation, supported data, consumer callbacks, and lifecycle transitions.

The existing version remains 1.0.5, the core assembly version remains 1.0.0.0, and protocol version remains 1. No CHANGELOG copy, package manifest, project/build definition, or packaging script was changed. No new public feature or public API member was introduced. Corrections necessarily change previously incorrect behavior; they are not intended to redefine successful synchronization contracts.

## Coverage and evidence

The independent source pass covered every C# source file in the core and bootstrap projects, including unchanged code:

- The main registration API, ConfigSync compatibility facade, typed config and custom-value wrappers, public modes, policy-control states, and event contracts.
- Runtime initialization and Harmony registration, GameReflection, RuntimeGuard, VersionCheck, session admission, buffering, complete/partial transport, fragmentation, compression, custom serialization, snapshot invalidation, policy control, file watchers, diagnostics, and shutdown.
- Both project files, Directory.Build.props, the solution, assembly/plugin metadata, Thunderstore targets, manifest update/checksum scripts, package manifest, and repository ignore/attribute rules.
- README and packaging contracts, relevant architecture/lifecycle material in PROJECT_CONTEXT, notices, and package-copy layout. PROJECT_CONTEXT is historical background, not a claim that every historical note is current. Unchanged duplicate package documents were checked by repository blob identity rather than repeatedly reread. Image assets were inventoried, not runtime-tested.

Primary implementation references were read rather than inferred:

- `shudnal/assemblies_combined`, commit `cf2cda3a4c5c05e62cb8052a61753e5dcaecc28e`: `assembly_valheim/ZRpc.cs` and `assembly_valheim/ZPackage.cs`. This was the first source used for game implementation details.
- `BepInEx/BepInEx`, branch `v5-lts`, commit `5c18fafea1b41aed5a66d2302cfc9ccecc364f2e`: `BepInEx/Configuration/ConfigEntryBase.cs`, `ConfigFile.cs`, and `ConfigDescription.cs`.

Verification consisted of source inspection, cross-file call-path review, repository tree/blob comparisons, and committed-diff inspection. No mod build, compilation, automated test, game launch, or runtime/network experiment was performed. Runtime correctness must not be inferred from the existence of this report.

## Confirmed defects and corrections

### 1. Synchronization operations could overtake each other

**Normal trigger:** a large valid value requires fragmentation; a shorter later update, full resync, or sequenced event is dispatched before the earlier coroutine finishes.

**Consequence:** a later state or event could arrive first. A completed older snapshot could overwrite newer data. Merely queuing coroutine starts did not preserve update order.

**Correction:** `Parts/SendQueue.cs`, `Transport.cs`, and `Stabilization.cs` implement per-instance FIFO reservations covering complete snapshots, partial updates, metadata, corrections, and sequenced values. Reservations are made before consumer serialization and before the initial-sync coroutine can yield. Sequenced pending entries also reserve their order before serialization. Latest-state values remain coalesced; the existing sequenced queue bound still rejects the newest event explicitly.

### 2. Old work could outlive the connection that created it

**Normal trigger:** disconnect or shutdown while a fragmented send, queue wait, snapshot stabilization, or queued registration is suspended, followed by another connection in the same process.

**Consequence:** old continuations could inspect or change new-session state, and old counter cleanup could interfere with current sends.

**Correction:** transport reservations carry a generation and the exact ZNet session. Continuations verify the session, peer, RPC, and socket after waits. Reset invalidates reservations before consumer callbacks. Sender disposal is idempotent and cannot remove a new-session reservation. Buffered vanilla startup data is restored through the captured wrapper rather than whichever socket happens to be current later.

### 3. Cross-instance callback updates could remain queued indefinitely

**Normal trigger:** applying a package for one ConfigSync causes a consumer callback to change a value owned by another ConfigSync.

**Consequence:** the process-wide processing flag deferred the second instance, but the first instance only flushed its own queues afterward.

**Correction:** processing completion restores the prior flag and flushes pending work across a snapshot of registered instances. ZNet.Update also services deferred work. Package preparation is separately gated to prevent serializer callbacks from sending out of order.

### 4. Full resynchronization unnecessarily replayed local fallback values

**Normal trigger:** a complete resync contains an entry already controlled by the server.

**Consequence:** resetting all entries first temporarily applied the local fallback, raised consumer callbacks, and then applied the new server value. Runtime systems could rebuild twice or observe an irrelevant local state.

**Correction:** `ResetConfigsFromServer` accepts the incoming parsed snapshot. Entries present in that snapshot retain active values and local fallbacks until replacement values are applied. Entries absent from the snapshot are still restored normally. Application stops when a consumer callback ends the session.

### 5. Ownership and UI metadata could be stale during lifecycle events

**Normal trigger:** shutdown, optional-server fallback, a lock transition, or source-of-truth change with registered custom values and Configuration Manager metadata.

**Consequence:** callbacks could observe old custom-value ownership or old ReadOnly/Browsable attributes; local settings could remain visually locked after disconnect.

**Correction:** ownership flags and metadata are refreshed before their public notifications. Reset events are emitted after local restoration and network-session cleanup. Iterations that can invoke consumers use snapshots where registration may change during callbacks.

### 6. Late registration could miss the initial snapshot permanently

**Normal trigger:** an entry is registered during package deserialization/application, or an optional server provider appears after its original admission window.

**Consequence:** the package lookup maps cannot contain the newly created entry, while initial-sync gating previously discarded the registration request. A first partial package from a late provider was not sufficient to initialize the client completely.

**Correction:** late registrations are retained before initial completion; only registrations actually present in a parsed package are cleared. Initial completion schedules the remaining request. A partial first contact from a late provider schedules a full-sync repair after peer readiness. An optional-provider fallback does not overwrite an already received full snapshot or a pending repair.

### 7. Administrator updates depended on an arbitrary optional consumer

**Normal trigger:** the server administrator list changes, but the first registered sync instance is not installed on a particular client.

**Consequence:** sending the process-wide exemption only through that first consumer's RPC could leave the client with stale administrator state.

**Correction:** administrator metadata is sent through each registered synchronization instance. Received process-wide exemption changes refresh all local instances.

### 8. Shared ConfigDescription objects coupled unrelated entries

**Normal trigger:** consumers reuse ConfigDescription.Empty or another description instance for multiple settings.

**Consequence:** attaching wrapper and Configuration Manager tags to the shared description could make unrelated entries share synchronization metadata or resolve the wrong wrapper.

**Correction:** each registered entry receives its own description with preserved description text, acceptable values, and non-CCS tags. Wrapper lookup verifies the underlying ConfigEntry reference.

### 9. One consumer subscriber could suppress synchronization or later callbacks

**Normal trigger:** an earlier typed ConfigEntry.SettingChanged subscriber throws, or one CustomSyncedValue.ValueChanged subscriber throws.

**Consequence:** the typed multicast delegate stopped before reaching CCS, or later custom-value subscribers were skipped.

**Correction:** CCS observes the individually isolated ConfigFile event and positions its observer after newly bound entries' typed forwarders. Custom-value subscribers are invoked individually with failure logging. Existing typed normalization handlers still run before CCS publishes the observed value.

### 10. A local config assignment before initial sync could be discarded

**Normal trigger:** consumer initialization uses SyncedConfigEntry.AssignLocalValue while the client is already a replica but has not received its first server value.

**Consequence:** no fallback had been captured yet, so the method attempted an active protected assignment rather than storing the intended local value.

**Correction:** fallback selection accounts for current ownership and effective server control, not only the presence of an earlier fallback.

### 11. Primitive and collection serializers were asymmetric

**Normal trigger:** valid custom values contain byte data, primitive types not handled by ZRpc.Serialize, arrays, or reference-type generic collections such as HashSet<T>.

**Consequence:** ZRpc.Serialize silently skipped several primitives; arrays had a writer without a matching reader; generic-only collections could have a reader without a matching writer.

**Correction:** `Parts/CustomValueSerialization.cs` uses native ZPackage primitive writers and matching stream readers, restores one-dimensional array reads, and writes supported generic collection elements symmetrically. Byte arrays use the native count-plus-bytes representation. Existing successful dictionary, reflected-struct, List<string>, nullable-root/field, and ISerializableParameter encodings remain unchanged. Structs implementing only ICollection<T> retain their reflected-field layout.

No per-element null marker or multidimensional-array shape metadata was added to protocol 1. Those unsupported layouts now produce explicit diagnostics rather than silently emitting unusable data. Sequenced values were not removed from full snapshots.

### 12. File watcher work could lose or reorder normal edits

**Normal trigger:** another policy save occurs while both policy files are being read, overlapping debug reloads finish in reverse order, or an old watcher callback arrives after cleanup.

**Consequence:** the final saved policy could be missed, an older debug state could replace a newer one, or old-session work could affect current state.

**Correction:** every policy notification advances the dirty generation, including notifications during a read; the worker rereads when dirty. Synchronous reads allocate their generation before I/O. Watcher identity prevents old-session callbacks from publishing or clearing new worker state. Debug reads and publication are serialized with cleanup. Synchronous policy application remains immediate even when a watcher reports the operation's own write.

### 13. Deferred retries and failure diagnostics had ordinary lifecycle defects

**Normal trigger:** work reschedules itself while draining the main-thread queue; required reflection bindings fail during startup; or a congested connection reaches the existing send timeout.

**Consequence:** a retry could consume the same update repeatedly, the empty reflection-validation method did not guarantee beforefieldinit binding initialization, and the error RPC passed an enum that ZRpc.Serialize does not support.

**Correction:** a drain processes only the work present at its start, with queue-generation cancellation. Reflection initialization is forced before patches are installed. The error RPC sends its native integer argument. Runtime dependency diagnostics use the actual plugin GUID.

## PR #1 disposition

PR #1 was inspected only after the independent source pass. Its branch was not merged, used as a baseline, or cherry-picked. The source commit claimed in its description (`b5f7f3f2df526e0e11dd625d563dc35c9d1c8923`) was not available through the repository commit API when checked.

Its comments were treated as hypotheses requiring current-source verification. The shared-description observation was useful additional input; several lifecycle, queue, callback, and watcher observations independently matched defects in the current source. Snapshot build-stability work already present in master was not duplicated. Proposals that would add nullable collection wire markers or remove sequenced values from full snapshots were not adopted because they would redefine the protocol or existing behavior.

## Maintainer runtime validation checklist - not executed

1. Connect with several consumers, send a fragmented value followed by a short value and repeated equal sequenced events, and request a full snapshot during the transfer. Verify event order and final values.
2. Disconnect during a send and reconnect or host a local world. Verify that old sends, fragment caches, read-only metadata, fallback values, and reset callbacks do not affect the replacement session.
3. Register settings/custom values during initial callbacks and after connection, including an optional provider first observed through a partial update. Verify eventual complete synchronization without repeated initialization events.
4. Change the server administrator list while the first server consumer is absent on a client. Verify that installed consumers refresh their access and metadata.
5. Reuse a ConfigDescription for several settings, throw from one consumer subscriber, and change another sync instance from a callback. Verify independent metadata, continued notifications, and outgoing synchronization.
6. Request a full resync with unchanged and changed state. Verify that retained entries do not temporarily replay local fallbacks and that disconnect still restores the correct local values.
7. Round-trip representative primitives, byte arrays, integer arrays, enum-underlying types, lists, dictionaries, sets, reflected structs, and ISerializableParameter values. Verify existing successful formats with the intended matching mod deployment.
8. Save policy/debug files rapidly and through editor atomic replacement, then stop and start a network session. Verify the final file contents become effective and old watcher work does not reapply.

This checklist is a handoff for the maintainer's game environment, not a record of passing tests. A separate Codex whole-tree audit is requested in the pull request; its request and actual result must be distinguished.
