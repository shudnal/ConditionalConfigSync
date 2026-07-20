# Conditional Config Sync Project Context

This document is the durable engineering context for Conditional Config Sync (CCS). It is intentionally more detailed than the public README. Its purpose is to let a maintainer, reviewer, or AI assistant open the repository after a long gap and understand not only what the implementation does, but why it was designed this way, which invariants must be preserved, and which apparently simpler alternatives were deliberately rejected.

Read this file before making architectural, networking, compatibility, policy, lifecycle, or packaging changes. Read the current source code as the final authority when implementation details have evolved, and update this document whenever a project decision changes.

## Repository language and documentation policy

All repository content must be written in English. This includes source identifiers, comments, XML documentation, logs, exceptions, validation messages, configuration templates, scripts, filenames, directory names, release notes, commit drafts, and technical documentation. Intentional localization resources are the only exception.

Before publishing or returning a modified project, scan for accidental Cyrillic text outside intentional localization resources.

## Project identity

- Project name: Conditional Config Sync
- Common abbreviation: CCS
- BepInEx GUID and Harmony owner: `_shudnal.ConditionalConfigSync`
- Core assembly: `ConditionalConfigSync.dll`
- Bootstrap assembly: `ConditionalConfigSync.Plugin.dll`
- Current source/package line: 1.x
- Current wire protocol: numeric protocol version 1 with exact protocol matching
- Target runtime: Valheim with BepInEx 5, Harmony, Unity, and .NET Framework-compatible Mono
- Language version: C# 11 for the SDK-style CCS projects

The conventional syntax used throughout the project is intentional. Do not perform style-only rewrites to C# 12 primary constructors, collection expressions, or other newer syntax. Explicit constructors, explicit collection creation, and readable control flow are preferred because this library is a shared dependency that must remain easy to inspect and maintain in a Unity/Mono environment.

## Why the project exists

Valheim mods frequently need to synchronize BepInEx configuration and runtime state between a server and its clients. Historically, many mods embedded or ILRepacked a private copy of a synchronization helper. That model creates several long-term problems:

- every mod ships a separate implementation and therefore a separate copy of the same bugs;
- security or protocol fixes require every dependent mod to be rebuilt and republished;
- multiple embedded copies create duplicate static registries, Harmony patches, network handlers, file watchers, and diagnostics;
- server administrators cannot apply one consistent ownership policy across participating mods;
- old mods remain permanently tied to the synchronization source that happened to be embedded when they were built.

CCS is a standalone shared runtime intended to centralize those responsibilities. A small dependent mod should be able to remain unchanged for years while receiving synchronization fixes by updating CCS, provided that the dependent mod's own game-facing logic still works and CCS preserves its public and behavioral contracts.

## Primary goals

1. Provide one maintained shared synchronization runtime for participating Valheim mods.
2. Preserve a familiar migration path for mods previously using ServerSync-style APIs.
3. Make ownership explicit: always server-controlled, policy-controlled, or always client-controlled.
4. Let server administrators override only the settings that mod authors declared conditional.
5. Preserve separate client-local fallback values while authoritative server values are active.
6. Treat the server as the authority for permissions, ownership, policy, and redistributed state.
7. Make configuration UI metadata useful without treating a UI as a security boundary.
8. Support both state-like custom values and event-like ordered custom values.
9. Handle late registration, reconnection, cleanup, malformed packages, large payloads, and subscriber failures predictably.
10. Preserve binary, source, behavioral, and wire compatibility for old dependent mods whenever technically possible.

## Non-goals

CCS is not intended to:

- replace, patch, intercept, or disable Jotunn synchronization or ServerSync used by unrelated mods;
- provide gameplay content or a standalone player-facing configuration UI;
- make hidden configuration a security mechanism;
- allow embedded or ILRepacked private CCS copies;
- provide arbitrary remote code execution, file transfer, HTTP communication, telemetry, or self-updating behavior;
- retroactively rerun Valheim's completed peer admission handshake for a ConfigSync instance created after connection;
- make all client-local settings server-controlled by default;
- guarantee correctness for a mod whose own runtime logic cannot tolerate its selected ownership model.

## Distribution and assembly architecture

CCS is split into two assemblies for a deliberate reason.

### `ConditionalConfigSync.Plugin.dll`

This is the BepInEx bootstrap. It owns plugin discovery and starts the shared runtime. Dependent mods should not compile against this assembly.

The plugin assembly version follows the package version. Packaging reads this assembly to determine the Thunderstore version.

### `ConditionalConfigSync.dll`

This is the public API and runtime implementation used by dependent mods. It contains `ConditionalConfigSync`, the compatibility `ConfigSync` class, wrappers, custom values, policy logic, transport, lifecycle handling, diagnostics, and runtime patches.

Dependent mods compile against this assembly and declare a hard BepInEx dependency on `_shudnal.ConditionalConfigSync`.

The core assembly uses a stable ABI identity:

```text
AssemblyVersion = 1.0.0.0
AssemblyFileVersion = current package version
AssemblyInformationalVersion = current package version
```

The stable `AssemblyVersion` is an explicit compatibility requirement for the 1.x line. It reduces the chance that an old dependent DLL compiled against an earlier CCS file version will fail assembly resolution when a compatible replacement is installed. The plugin assembly may continue to use the current package version as its assembly version because dependent mods do not reference it.

### Why embedding is rejected

An embedded copy would recreate the exact fragmentation that the shared runtime is intended to eliminate. CCS therefore detects another assembly containing the CCS public types and reports an error. Runtime Harmony patches are installed explicitly under the CCS Harmony owner rather than through broad `PatchAll` discovery, which also reduces the chance that a private copy can accidentally activate duplicate patches.

Do not weaken the embedding guard merely to make an incorrectly packaged dependent mod load. The correct repair is to remove the embedded assembly and declare the standalone hard dependency.

## Public API compatibility model

The public API is a long-lived contract. Compatibility includes more than public method names.

### Binary compatibility

An already compiled dependent mod records exact metadata references to assemblies, namespaces, types, fields, properties, constructors, methods, events, return types, parameter types, parameter order, and generic constraints. The following are binary breaking changes and must not be made in the compatible 1.x line:

- deleting or renaming a public type or member;
- moving a public type to another namespace or assembly;
- changing parameter count, type, order, or ref/out semantics;
- changing a return type;
- replacing a field with a property or a property with a field;
- changing an event's delegate type;
- changing generic constraints in a way that invalidates old callers;
- removing a constructor used by old mods;
- changing a base type or interface contract when old IL depends on it.

Optional parameters do not make a replacement method binary-compatible. If a method must gain a parameter, retain the old overload and add a new overload.

### Source compatibility

`ConfigSync` inherits from `ConditionalConfigSync` to provide a familiar source-level migration surface. It is not a binary replacement for an unrelated ServerSync assembly. A mod compiled against ServerSync must be rebuilt to use CCS, but a mod already compiled against an older compatible CCS core should not need rebuilding.

### Behavioral compatibility

The following semantics are part of the public contract even when signatures remain unchanged:

- normal `CustomSyncedValue<T>` represents latest state and suppresses equal assignments according to its comparer;
- `SequencedCustomSyncedValue<T>` preserves every assignment, including repeated equal payloads;
- local fallback values survive a connected server session and are restored after disconnect;
- server values remain active only while effective ownership is server-controlled;
- policy and lifecycle events observe the final applied active state;
- subscriber exceptions are isolated and do not prevent remaining handlers from running;
- `AssignLocalValue` methods preserve local fallback semantics on a client replica;
- initial synchronization completes only after the full package has been applied;
- late registration requests or distributes the missing authoritative state rather than silently remaining unsynchronized.

Changing any of these requires the same care as changing a public signature.

### Additive evolution

Prefer the following order when extending the API:

1. add a new overload;
2. add a new type or optional capability;
3. retain the old member and implement it through the new path;
4. mark an old member obsolete only when a clear replacement exists;
5. avoid removing old members while real dependent mods still use them.

If a future architecture is fundamentally incompatible and cannot retain old contracts, it should use a separately named assembly and plugin GUID rather than silently replacing the shared 1.x runtime.

## Config registration and ownership modes

Every registered BepInEx config wrapper has a `ConfigSyncMode`.

### `AlwaysServerControlled`

The server always owns the active value. Sync policy cannot release it to clients. Use this for shared mechanics, world state, network-visible calculations, structural inventory rules, or any setting where peer disagreement is invalid.

### `Conditional`

The mod supplies a normalized default ownership and the server may override it through `SyncPolicy.cfg`. This is the default compatibility mode for settings that may reasonably be either shared or per-client depending on server policy.

A conditional setting that is effectively server-controlled must receive the same authorization and runtime protection as an `AlwaysServerControlled` setting. The only difference is whether policy may change ownership. Never use `AlwaysServerControlled` as a substitute for correct permission enforcement.

### `AlwaysClientControlled`

Each client owns its local value. Server policy cannot force it to server ownership. Use it for local presentation, controls, UI, and other behavior that does not participate in shared mechanics.

### Registration must establish ownership before observable synchronization

Bind-and-register overloads should be preferred because they establish mode and default ownership in one operation. Compatibility overloads remain supported, but the implementation must avoid an observable window in which a newly registered client-owned config is temporarily treated as server-owned.

### Locking config

A mod may register one locking entry. The locking entry is always upgraded to all of the following, even if it had already been registered through another overload:

- `SyncMode = AlwaysServerControlled`;
- `SynchronizedConfig = true`;
- effective server ownership;
- protected against non-admin changes even when other unlocked client updates are enabled.

A client must never be able to change the lock first and thereby grant itself permission to change other values.

## Source of truth and lifecycle roles

`IsSourceOfTruth` is true on the server and in local/offline contexts. It becomes false on a connected client replica.

A connected client is fail-closed from the moment the network session becomes active. Before the first complete server package is applied, server-controlled entries are not writable. This prevents a configuration UI opened in the main menu from retaining pre-connection write permissions during loading.

`InitialSyncDone` becomes true only after a complete server package is successfully applied. Read-only metadata is recalculated again after this transition because applying the package while `InitialSyncDone` is still false intentionally keeps the client closed during the operation.

On shutdown or connection reset:

- authoritative server values are cleared;
- client-local fallback values are restored;
- default ownership is restored until another server policy is received;
- hidden state is cleared;
- pending broadcasts, fragment assemblies, late-registration state, and correction rate limits are cleared;
- lifecycle events run after the local state has been restored.

## Client value model

A synchronized config may have several conceptually different values.

### Active value

`ConfigEntryBase.BoxedValue` is the value currently observed by the mod. On a server-controlled client this is the authoritative server value.

### Local fallback

`LocalBaseValue` is the client's own value retained while a server value is active. It is restored after disconnect or after policy returns the setting to client ownership. Config-file serialization is patched so saving while connected does not overwrite this fallback with the active server value.

### Server value

`ServerValue` is the last authoritative server value received for a config. It can be reapplied when ownership changes back to server-controlled without requiring the server to resend the value immediately.

### Last accepted value

`LastAcceptedValue` is an internal defensive snapshot of the most recent value accepted by local permissions or applied from the authoritative server. It is used to undo writes made through stale or incompatible configuration UIs when no better authoritative snapshot is available.

These concepts must not be collapsed into one field. The separate model is what allows a client to use a server value temporarily without losing its local configuration.

## Permission and authorization model

Configuration UI state is user experience, not authority. CCS enforces permissions in three layers.

### Layer 1: dynamic UI metadata

CCS updates `ConfigurationManagerAttributes.ReadOnly` and `Browsable` whenever source-of-truth, initial synchronization, policy, lock, admin exemption, or hidden state changes.

A compatible configuration manager should refresh these mutable tag values while its window remains open. The maintained ConfigurationManager project does this every frame, closes an edit window when a setting becomes non-browsable, disables edit controls when it becomes read-only, and rechecks dynamic attributes immediately before a write.

This prevents the known stale-window scenario where a manager was opened in the main menu and retained an old writable copy after joining a locked server.

### Layer 2: runtime config guard

CCS patches `ConfigEntryBase.OnSettingChanged` at high priority. If a protected server-controlled value was changed through a stale UI, another manager, a direct `ConfigEntry.Value` assignment, or another local path, CCS restores the authoritative value before the normal invalid change notification is delivered. The original unauthorized notification is suppressed, while any restoration notification exposes only the restored value.

The normal CCS `SettingChanged` handler also performs a rollback as defense in depth. This protects the active client state even if another patch changes invocation behavior.

The guard does not replace the local fallback API. Mod code that wants to update a client's saved local value while a server value is active must use the documented `AssignLocalValue` path.

### Layer 3: server-side package authorization

The server independently validates every client update. Client-side state, UI metadata, and claims are never trusted as authorization.

For each client update the server verifies:

- the sender is a ready connected peer;
- the package uses the supported partial update shape;
- entry and payload limits are respected;
- entries are known and not duplicated;
- values parse or deserialize using the server's registered type;
- no prohibited server metadata is present;
- every config is effectively server-controlled according to the server's own mode and policy;
- the protected locking config is changed only by an administrator;
- a non-admin is rejected while the real server lock is enabled;
- a non-admin is rejected while unlocked client updates are disabled by the mod.

The real server lock is evaluated separately from the local admin exemption. `IsLocked` remains a useful local/effective view, but server authorization uses the underlying server lock state plus the actual sender's current admin status.

### Administrator handling

The client receives an admin exemption for UI and local behavior, but the server checks the connected peer against the current Valheim admin list for every update. Therefore a stale client-side exemption cannot authorize a removed administrator. A newly added administrator may remain locally read-only until the next admin-state update, which is safe; the server remains authoritative.

### Unlocked client updates

`AllowClientConfigUpdatesWhenUnlocked` defaults to `false`.

This default is deliberate. A server-controlled value remains server-originated unless the mod explicitly opts into collaborative unlocked-client updates. Administrators are not blocked by this option. A mod that genuinely wants ordinary clients to publish shared values while the lock is disabled must set the option explicitly and accept the gameplay implications.

## Client update packet rules

New CCS clients send only config values and custom values in partial update packages. They do not send authoritative policy, hidden, lock-exemption, or server-version metadata.

For compatibility with earlier CCS 1.x clients, a client `ConfigState` entry may be accepted only as a non-authoritative legacy claim paired with the changed config value. The server validates that the claim matches the server's effective ownership and hidden policy, never applies it, and never forwards it. A mismatched, unknown, duplicate, or state-only claim rejects the update.

`LockExempt`, `ServerVersion`, unknown entry kinds, unknown values, malformed payloads, duplicate values, and trailing data are rejected in client updates.

The entire package is parsed and validated before the first value is applied. A package containing one invalid entry is rejected rather than partially applying the valid entries.

## Server canonicalization and redistribution

The server must never forward an untrusted client package directly.

After an authorized update:

1. the server applies the candidate value through its registered BepInEx entry or custom value;
2. acceptable-value clamping and mod-side normalization are allowed to produce the final active value;
3. the server reads the resulting canonical value from its own state;
4. the server builds a new package containing only canonical values and server-computed config state;
5. the new package is sent to all clients, including the initiating client.

This guarantees that all clients converge on what the server actually accepted rather than what a client originally requested. It also prevents a client from smuggling policy or lock metadata to other clients through server forwarding.

When an update is rejected, the server rate-limits and sends an authoritative correction. Known values can receive a partial correction; malformed or metadata-conflicting updates receive a complete resynchronization when the peer is still available. Rejection and acceptance logs include the sender and authorization reason so future reports can be diagnosed from server logs.

## Lock semantics

There are two related but distinct concepts.

### Server lock enabled

This is the actual locking config value, optionally replaced by the programmatic `IsLocked` override. It does not include a particular client's admin exemption. Server authorization uses this state.

### Effective local lock

This combines the server lock with the local process's admin exemption. It is useful for UI and events. An administrator may therefore observe the effective lock as false while the server lock is still enabled for ordinary clients.

Do not use the effective local lock as the sole server-side authorization check for an arbitrary sender.

## Sync policy

Server policy files live under:

```text
BepInEx/config/shudnal.ConditionalConfigSync
```

### `ConditionalConfigSync.SyncPolicy.cfg`

Ownership rules may target one exact setting or a complete section. Exact rules override section rules. Rules apply only to `Conditional` settings. Fixed modes remain fixed.

The implementation uses the longest registered GUID prefix to resolve records because GUIDs and section/key names may contain punctuation. It distinguishes unknown mod GUIDs, unknown sections, and unknown config names in diagnostics.

Every non-empty, non-comment record is logged in file order. Detailed records use the `[SyncPolicy]` tag. Records that change a mod-defined default are warnings; no-effect records are informational; unresolved records are warnings. Source details are added only at extended debug levels.

The summary format is:

```text
[SyncPolicy] Reloaded: forceServer=..., forceClient=..., hidden=..., source=...
```

### `ConditionalConfigSync.HiddenConfigs.cfg`

Hidden rules affect compatible configuration-manager presentation only. Every resolved record is intentionally logged as a warning because it removes a setting from the normal UI. Hidden policy is not access control. A hidden server-controlled config is still protected by ownership and lock validation; a hidden client-controlled config can still be edited through files or other tools.

### Stable file loading

Policy reads retry until file metadata is stable. This protects against common editor save patterns such as truncate-write-rename and avoids replacing a valid policy with a partially written snapshot. File watcher callbacks queue work to the Unity main thread before mutating runtime state.

### Runtime policy toggle API

Compatible configuration UIs may request an exact-setting ownership toggle only when:

- the setting is `Conditional`;
- the server advertises the capability;
- the requesting client is an administrator;
- the request resolves to the expected registered setting.

The server persists a minimal exact rule and remains the source of truth. The UI request is not a direct local ownership mutation.

## Custom synchronized values

### State values

`CustomSyncedValue<T>` represents current state. Equal values are suppressed according to the configured comparer. Pending state updates may be coalesced to the latest value.

Use a content comparer for arrays, lists, dictionaries, or domain objects when reference equality does not represent meaningful equality.

### Sequenced values

`SequencedCustomSyncedValue<T>` represents event-like data. Every assignment, including equal repeated payloads, is preserved in order. Pending sequenced updates are snapshotted into separate packages and are not silently coalesced.

Use sequenced values for commands, pulses, combat events, and other cases where `A, A` means two events. Do not use them merely to force initial state processing; use the explicit notify assignment methods on a normal custom value instead.

### Local fallback semantics

On a client replica, `AssignLocalValue`, `AssignLocalValueIfChanged`, and `AssignLocalValueAndNotify` update local fallback according to their documented behavior rather than overwriting the active server value.

Direct active-value assignments on a client are subject to the same lock/admin/unlocked-publication rules as config updates. Unauthorized direct changes are restored from the last accepted value.

## Package format and protocol

The wire protocol has its own numeric version independent of package/file version. Client and server require an exact protocol match.

Increase `ProtocolVersion` only when the wire contract becomes incompatible. Do not increase it for internal refactoring, logging changes, new documentation, or compatible validation that still accepts the established format.

V2 synchronization packages use length-prefixed entries. Entry kinds currently include:

- regular config value;
- custom synchronized value;
- server version metadata;
- lock exemption and capability metadata;
- config ownership/hidden state.

Regular config values are serialized through BepInEx TOML conversion using the locally registered type. Custom values use typed Valheim package serialization, including `ISerializableParameter`, enums, collections, dictionaries, and supported value types.

Unknown or malformed server entries are logged and isolated where forward compatibility permits. Client updates use stricter all-or-nothing validation because they cross an authorization boundary.

## Payload safety and transport

The network layer includes explicit limits to prevent accidental or hostile memory growth:

- maximum package payload: 20 MiB before or after compression;
- maximum entry count: 8192;
- maximum fragment count: 128;
- maximum fragment size: 300000 bytes;
- maximum incomplete fragment assemblies per sender: 4;
- per-sender fragment cache limit: 20 MiB;
- global fragment cache limit: 64 MiB;
- maximum pending sequenced events: 100;
- send-queue timeout: 30 seconds.

Large packages are compressed and then fragmented when required. Fragment caches are keyed by sender and package identifier, expire, reject duplicates and inconsistent fragment counts, and are cleaned on session shutdown.

Do not remove limits merely to accommodate one unusually large mod value. First determine whether the data should be represented more compactly or split into an explicit application-level structure. If a limit is changed, update documentation and test malformed/oversized behavior.

### Initial peer handshake buffering

During the server-side `RPC_PeerInfo` admission path, CCS temporarily wraps the peer RPC socket so the CCS full synchronization package can be sent before selected vanilla initialization traffic is released. The wrapper must preserve the original send order of every buffered package.

The buffered vanilla methods are:

- `PeerInfo`;
- `PlayerList`;
- `AdminList`;
- `RoutedRPC`;
- `ZDOData`.

`PlayerList` and `AdminList` are not optional conveniences. The client registers their handlers while processing `PeerInfo`, so allowing either package to bypass the buffered `PeerInfo` package can make the initial RPC arrive before its handler exists. That loses the initial player list and can leave `LocalPlayerIsAdminOrHost()` incorrect until another vanilla refresh happens.

When inspecting an outgoing package, restore its original read position before buffering or forwarding it. Buffered packages must be cloned because Valheim may reuse the original `ZPackage`. Release the queued packages in their captured order and replay a queued `VersionMatch` call at the exact relative position where it was requested.

This ordering fix is transport-internal and does not change the CCS wire protocol.

## Deferred broadcasts and reentrancy

Setting handlers may change other synchronized values while a package is being applied. Dropping those changes would lose real mod state, while broadcasting recursively from inside package processing can corrupt ordering or produce reentrant network behavior.

CCS therefore queues outgoing changes while processing or sending:

- config updates are deduplicated by config entry;
- normal custom values coalesce to latest state;
- sequenced custom values retain each event package;
- pending values flush only when processing and sending are idle.

Do not replace this with a single generic queue without preserving the different state/event semantics.

## Late registration and resynchronization

A config or custom value registered after the normal startup window must not remain silently unsynchronized.

- Server-side late registrations are batched per frame and distributed.
- Client-side late registrations request a complete package after initial sync.
- A `ConfigSync` instance created after connection registers its RPC handlers and requests its first complete package.
- Mods that rebuild a dynamic registration set may call `RequestFullSync()` explicitly.

A late resync updates data and policy state. It does not retroactively repeat the completed Valheim peer admission and version check. Mods requiring connection rejection must create their `ConfigSync` and set `ModRequired` before connection.

## `ModRequired` semantics

`ModRequired` controls whether the owning mod must exist compatibly on the remote side. It is independent from the local hard dependency on CCS itself.

- On a client with `ModRequired = true`, the server must have a compatible copy of the owning mod.
- On a server with `ModRequired = true`, connecting clients must have a compatible copy of the owning mod.
- With `ModRequired = false`, the remote side may lack the owning mod.

Set it before connection. Late changes cannot retroactively redo an already completed handshake.

A mod such as Seasons that requires matching runtime definitions must use `ModRequired = true`. A small optional client/server-compatible mod may deliberately use false.

## Version checking

`VersionCheck` integrates with Valheim's peer admission path and uses exact CCS protocol compatibility. Keep its required `using` block in the established order unless functional code requires a change:

```csharp
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using UnityEngine;
```

Do not conflate package version, dependent mod version, assembly identity, and wire protocol. They solve different problems:

- package/file version identifies the release;
- core `AssemblyVersion` provides stable ABI identity;
- dependent mod version controls that mod's compatibility policy;
- `ProtocolVersion` controls network format compatibility.

## Logging and diagnostics

Normal logs should answer which mod instance, side, area, sender, setting, and reason were involved without requiring verbose debugging.

Extended debug logging can be enabled globally or through the CCS debug config and filtered by mod name or GUID. It has Basic, Verbose, and Trace levels.

Important diagnostic principles:

- rejected client updates are warnings and include current admin, server-lock, and unlocked-update state;
- accepted client updates are logged with the authorization reason;
- package rejection raises `SyncRejected` in addition to logging;
- one subscriber failure is logged but does not stop remaining subscribers;
- policy records preserve file order in logs;
- no sensitive external data is collected or transmitted.

Avoid restoring high-volume unconditional logs in frame-sensitive paths. Security-relevant server accept/reject decisions are an exception because they are infrequent and necessary for auditability.

## ConfigurationManager integration

CCS communicates UI state through a tag object named `ConfigurationManagerAttributes` with nullable `ReadOnly` and `Browsable` members. Name-based reflection preserves compatibility with configuration managers that do not reference CCS directly.

The maintained ConfigurationManager additionally reads public synchronization metadata to display ownership indicators and policy tooltips.

The known stale-state bug existed because ConfigurationManager copied tags once when building a `ConfigSettingEntry`. If the window stayed open from the main menu through server loading, the policy indicator refreshed through its own live metadata path but the cached `ReadOnly` value did not. The stale row could write a value and older CCS code could send it to the server.

The repair has two independent parts:

1. ConfigurationManager refreshes mutable `ReadOnly` and `Browsable` tags while open and rechecks immediately before every write.
2. CCS rejects or restores unauthorized writes locally and validates them again on the server.

Both are required. The ConfigurationManager fix provides correct UX; CCS protects users of other managers, direct config assignments, stale plugins, and crafted network packages.

## Rejected or deliberately avoided designs

### Rely only on ConfigurationManager `ReadOnly`

Rejected because tags may be cached, other managers may interpret them differently, files and direct code can bypass the UI, and clients are untrusted from the server's perspective.

### Forward the original client package

Rejected because it forwards untrusted metadata, can distribute a pre-clamp value, can leave the initiating client divergent, and makes the server a relay rather than an authority.

### Use `HasLocalBaseValue` as write permission

Rejected because value backup state is not authorization. A server-controlled config can lack a local backup before first application or after unusual lifecycle paths and must still fail closed.

### Treat `AlwaysServerControlled` as the security fix

Rejected because an effectively server-controlled `Conditional` config needs identical protection. Fixed mode controls policy mutability, not write authorization.

### Keep unlocked client publication enabled by default

Rejected for the shared runtime because it makes server-controlled values client-publishable unless every mod author remembers to opt out. The secure default is server-originated values with explicit opt-in.

### Trust client-supplied `ConfigState`

Rejected. Legacy claims may be validated for wire compatibility, but the server computes and redistributes the authoritative state.

### Change assembly version on every patch

Rejected because dependent mods reference the core assembly identity. The file and informational versions communicate release identity without destabilizing compatible binary references.

### Embed one copy per dependent mod

Rejected because it prevents centralized fixes and creates duplicate runtime infrastructure.

### Remove old public API in a major internal refactor

Avoided whenever possible. A shared dependency must prioritize ecosystem continuity. Additive adapters are preferred even when an internal implementation is replaced.

## Known limitations and intentional boundaries

- CCS cannot prevent a mod's own code from performing side effects before CCS is registered or before that mod uses the supported synchronization wrapper.
- A third-party Harmony prefix with higher/equivalent priority can interfere with any runtime patch. Server authorization remains the final boundary.
- A rollback may still cause a restoration notification when an invalid write already reached BepInEx's setting-change path. The invalid value itself is suppressed from normal notification by the CCS high-priority guard.
- Hidden configuration remains discoverable and editable through direct files or custom tools when ownership permits.
- Late-created ConfigSync instances cannot retroactively reject a peer that already completed admission.
- Exact protocol matching does not guarantee that every dependent mod has identical optional content unless that mod correctly sets `ModRequired` and its own version requirements.
- Very large custom values can still be expensive within the accepted hard limits; limits protect memory, not application-level efficiency.
- The library cannot guarantee that a mod is semantically safe when an administrator forces a conditional setting to client ownership. Mod authors must choose fixed modes for settings that cannot diverge.

## Regression-prone areas

Exercise extra care in the following code paths:

- source-of-truth transitions during `ZNet.Awake` and `ZNet.Shutdown`;
- initial sync ordering relative to `InitialSyncDone` and read-only recalculation;
- initial peer handshake buffering, especially `PeerInfo` before `PlayerList` and `AdminList`;
- local fallback serialization while an active server value is present;
- policy changes that switch active ownership without a new value package;
- locking config registration after an earlier generic registration;
- admin-list changes and stale client exemptions;
- partial client updates versus full server packages;
- compressed and fragmented package flag handling;
- validation before application and canonical redistribution after application;
- event reentrancy while restoring a rejected local config write;
- custom value construction and initial assignment before ownership subscriptions settle;
- pending sequenced-value queue overflow;
- cleanup of fragment caches, RPC registrations, watchers, and static state between sessions;
- compatibility code that uses type/member names through reflection.

## Required regression test matrix

At minimum, run or reproduce the following before a release that touches synchronization or permissions.

### Config ownership and UI

1. `Conditional`, server-controlled by default, no policy override, locked server, non-admin client: main and edit UI are read-only.
2. Open ConfigurationManager in the main menu, keep it open while joining the server: indicator, tooltip, `ReadOnly`, and `Browsable` all update without reopening.
3. Attempt to edit the stale row through the main drawer and through the separate Edit window: the active value remains authoritative and no client update is accepted.
4. Repeat the forbidden write through direct `ConfigEntry.Value` or `BoxedValue`: CCS restores or blocks it.
5. `ForceClientControlled`: the setting becomes editable, remains local, and the mod reacts to the local value.
6. `ForceServerControlled`: the conditional setting receives the same protection as a fixed server-controlled setting.
7. `AlwaysServerControlled`: policy cannot release it.
8. `AlwaysClientControlled`: policy cannot capture it and no server update is sent.
9. Hidden policy changes while the manager is open: the entry disappears and any open edit window closes.

### Authorization

10. Locked server, non-admin client, normal UI package: server rejects and another client does not change.
11. Locked server, non-admin client, crafted value-only package: server rejects.
12. Locked server, admin client: server accepts and broadcasts the canonical value.
13. Unlocked server, `AllowClientConfigUpdatesWhenUnlocked = false`: non-admin UI and crafted packages are rejected.
14. Unlocked server, option true: non-admin update is accepted and canonicalized.
15. Non-admin attempts to change the locking config while unlocked: always rejected.
16. Client sends `LockExempt`, `ServerVersion`, unknown kinds, unknown configs, duplicates, or trailing data: update is rejected.
17. Legacy client sends a matching paired `ConfigState`: claim is ignored as authority and update follows normal permissions.
18. Legacy client sends mismatched or state-only `ConfigState`: update is rejected and corrected.
19. Mixed package with one valid and one invalid entry: nothing is applied.
20. Sender removed from admin list while its client exemption is stale: server rejects based on current peer identity.

### Canonicalization

21. Client requests a value outside `AcceptableValueRange`: all clients, including the initiator, converge on the server's clamped value.
22. A server-side `SettingChanged` handler normalizes the value: redistributed value is the final server value.
23. Rejected known value receives a partial authoritative correction.
24. Malformed or metadata-conflicting update receives a rate-limited full correction.

### Lifecycle

25. Change a local fallback, connect to a server value, disconnect: local fallback is restored and remains persisted.
26. Switch policy server -> client -> server at runtime: active values and read-only state follow the effective owner.
27. Late register a config and custom value on server and client: one batched resync supplies correct state.
28. Reconnect to a different server: no fragment, policy, admin, pending-update, or value state leaks from the previous session.

### Custom values

29. Normal custom value equal assignments coalesce/suppress as documented.
30. Sequenced equal assignments are delivered as separate events in order.
31. Unauthorized direct custom-value change on a client is restored.
32. Deferred custom changes made during package processing flush with correct state/event semantics.

### Transport and robustness

33. Compression and fragmentation boundaries round-trip correctly.
34. Duplicate, missing, inconsistent, expired, and oversized fragments are rejected and cleaned.
35. Payload and entry limits reject explicitly without unbounded memory growth.
36. One failing subscriber or one failed entry does not crash synchronization processing for unrelated entries.
37. During initial peer admission, `PeerInfo`, `PlayerList`, and `AdminList` are released in original send order after the CCS full sync completes.
38. A newly connected client receives the initial vanilla player list and reports `LocalPlayerIsAdminOrHost()` correctly without waiting for a later list refresh.
39. Queued `VersionMatch` ordering remains correct when vanilla initialization packages are buffered.

### Compatibility

40. Load an unchanged test consumer DLL compiled against the first public 1.x CCS API with the new core DLL; do not rebuild the consumer.
41. Compare the new public API against the retained 1.x baseline and investigate every removal or signature change.
42. Test a compatible older CCS client/server format whenever validation or package entry handling changes.

## Compatibility verification requirement

Maintain two forms of compatibility testing:

1. A public API baseline checked with an API compatibility tool before release.
2. A small old consumer mod compiled against the original supported 1.x core and intentionally never rebuilt for routine tests.

The old consumer test is important because a source rebuild can hide a binary break such as an optional-parameter replacement, field-to-property conversion, or changed assembly identity.

A dependent mod should declare the minimum CCS package version that provides the newest API it actually uses, not automatically the latest version installed on the developer machine.

## Release checklist

1. Confirm no requested version change was accidentally made.
2. When releasing, update package/file version only where intended; keep the core `AssemblyVersion` at `1.0.0.0` for compatible 1.x releases.
3. Confirm `ProtocolVersion` changed only if the wire contract is intentionally incompatible.
4. Run public API compatibility checks.
5. Run the unchanged old consumer.
6. Run the permission, stale-UI, canonicalization, policy, lifecycle, handshake-ordering, and transport test matrix relevant to the change.
7. Build both assemblies against the intended Valheim/BepInEx references with no new warnings.
8. Verify the plugin package contains both runtime DLLs and the core XML documentation.
9. Verify dependent mod packages do not embed either CCS assembly.
10. Generate and verify SHA-256 hashes for both runtime DLLs.
11. Confirm README, CHANGELOG, PROJECT_CONTEXT, packaging documentation, and staged release copies are synchronized.
12. Scan repository text for accidental Cyrillic outside intentional localization resources.
13. Remove stale PDB/MDB files and stale binaries from package staging before creating release archives.
14. Test installation on a clean client and dedicated server profile.

## Source layout and responsibilities

### Repository root

- `README.md`: public user, administrator, and mod-author documentation.
- `CHANGELOG.md`: release-visible changes.
- `PROJECT_CONTEXT.md`: durable engineering rationale and invariants.
- `PACKAGING.md`: build and publication mechanics.
- `THIRD_PARTY_NOTICES.md`: retained notices and acknowledgements.
- `Directory.Build.props`: common target framework, C# version, nullable, deterministic-build, documentation, and path settings.

### Core project root

- `ConditionalConfigSync.cs`: central instance state, public registration API, locking registration, shared metadata.
- `ConfigSync.cs`: compatibility class.
- `ConfigSyncMode.cs`: ownership mode contract.
- `SyncedConfigEntry.cs`: config wrapper, local/server/accepted value state, typed API.
- `CustomSyncedValue.cs`: state and sequenced runtime value APIs.
- `ConfigurationManagerAttributes.cs`: UI interoperability tags.
- `GameReflection.cs`: validated runtime binding layer for non-public/publicized Valheim differences.
- `RuntimeGuard.cs`: standalone assembly enforcement and Harmony identity.
- `VersionCheck.cs`: peer admission and mod/protocol compatibility.
- `SynchronizationEvents.cs`: public lifecycle/policy/rejection event arguments.
- `PluginInfo.cs`: shared package/plugin/protocol metadata.

### `Parts`

- `Runtime.cs`: one-time initialization, Harmony patch installation, main-thread queue, embedded-copy diagnostics.
- `ConfigState.cs`: write permissions, UI metadata, fallback serialization, rejected-write restoration.
- `Packages.cs`: entry serialization/deserialization, config state application, custom value application, package construction.
- `Transport.cs`: RPC handlers, client authorization, canonical broadcast, compression, fragmentation, queues, shutdown reset.
- `Policy.cs`: policy files, record resolution, ownership/hidden computation, runtime policy changes, logs and commands.
- `Stabilization.cs`: RPC registration, late registration, resync, protocol validation, session cleanup.
- `Diagnostics.cs`: debug config, events, rejection reporting, status and console diagnostics.

### Plugin project

- plugin entry point and lifecycle;
- package targets and publication scripts;
- Thunderstore and GitHub release staging.

Keep responsibilities separated. Do not grow one giant synchronization class file again merely because partial classes make cross-file access easy.

## Historical decisions that must not be accidentally reversed

- CCS is a standalone hard dependency, not an embedded helper.
- `ConfigSync` compatibility is source-oriented, not a claim of binary ServerSync replacement.
- The numeric wire protocol is independent from package version and uses exact matching.
- Config ownership and hidden presentation are separate policies.
- The locking config is always server-controlled and non-admin protected.
- Effective server-controlled conditional configs receive the same security as fixed server-controlled configs.
- Local fallback and active server value are separate.
- UI `ReadOnly` is not a security boundary.
- Clients do not authorize themselves through config state or lock-exemption metadata.
- The server canonicalizes accepted client values and broadcasts its own package.
- Unlocked non-admin publication is opt-in, not default.
- Sequenced custom values preserve events rather than coalescing them.
- Deferred changes are queued, not silently dropped.
- Subscriber failures are isolated.
- Fragment and payload limits are explicit and bounded.
- Initial vanilla handshake packages that depend on `PeerInfo` handler registration must remain buffered and ordered behind `PeerInfo`.
- Source code remains C# 11 with conventional explicit syntax.
- Core `AssemblyVersion` remains stable for compatible 1.x releases.
- All repository content is English.

## How to approach a future bug report

1. Identify the exact dependent mod version, CCS version, ConfigurationManager version, server/client role, lock state, admin state, ownership mode, effective policy, and whether initial sync completed.
2. Determine whether the reported change was only visual, changed one client's active value, reached the server, or reached another client.
3. Collect both client and server logs from connection through the attempted change. A cross-client change necessarily involves an accepted or incorrectly forwarded server path.
4. Reproduce with a minimal conditional config before changing the dependent mod. Do not hide a CCS defect by converting every setting to `AlwaysServerControlled`.
5. Test a stale UI opened before connection because dynamic metadata and runtime authorization are separate code paths.
6. Verify the server's own computed ownership and current peer admin result. Never infer authorization from the client's indicator.
7. Inspect whether the server built a canonical package or forwarded client bytes.
8. Confirm local fallback persistence and disconnect restoration after any fix.
9. Add the exact scenario to the regression matrix and update this document if the fix establishes a new invariant.

## Current security-hardening rationale

The stale ConfigurationManager report demonstrated why defense in depth is necessary. In the normal post-load scenario, `ReadOnly` was correct and the Edit window's Apply path restored the active value. In the main-menu-to-server scenario, the live synchronization indicator updated but the copied `ReadOnly` field did not. The stale manager then changed a conditional server-controlled value, and the update could reach the server and another client.

The repair is intentionally broader than the observed UI defect:

- ConfigurationManager refreshes dynamic attributes;
- CCS fails closed before initial synchronization;
- CCS guards BepInEx setting notifications and restores unauthorized local writes;
- new clients send no authoritative config metadata;
- the server validates the sender and every entry against its own effective policy;
- lock and admin checks are server-side and per update;
- unlocked publication requires explicit mod opt-in;
- legacy state claims are validated but never trusted;
- the server no longer forwards client packages;
- accepted values are canonicalized and returned to every client;
- rejected values receive authoritative correction;
- logs preserve enough information to diagnose the next report.

Future refactoring must preserve all of those layers. Removing one because another layer appears sufficient would recreate the same class of defect through a different UI or code path.
