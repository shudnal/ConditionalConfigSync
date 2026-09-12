# Conditional Config Sync Project Context

This document is the durable engineering context for Conditional Config Sync (CCS). It is intentionally more detailed than the public README. Its purpose is to let a maintainer, reviewer, or AI assistant open the repository after a long gap and understand not only what the implementation does, but why it was designed this way, which invariants must be preserved, and which apparently simpler alternatives were deliberately rejected.

Read this file before making architectural, networking, compatibility, policy, lifecycle, or packaging changes. Read the current source code as the final authority when implementation details have evolved, and update this document whenever a project decision changes.

## Current handoff snapshot — 2026-09-12

This section is the shortest path for starting a new chat or resuming work after context loss. It records the exact accepted baseline, the current unreleased release state, the incident that motivated the latest work, and the non-negotiable implementation decisions. The remainder of this document contains the deeper architecture and historical rationale.

### Accepted source lineage

The authoritative pre-1.0.3 reference supplied by the owner is `ConditionalConfigSync(7).zip`. Any future comparison against the 1.0.2-era code must use that archive, not an older reconstructed working tree.

A previously generated 1.0.3 archive was rejected because it had accidentally been based on an older source state and regressed the structured policy-control API. In particular, it lost or reverted:

- `ConfigSyncPolicyControlState.cs`;
- `PolicyControl.GetPolicyControlStateFor(...)`;
- `SyncedConfigEntry.SynchronizationPolicyControlState`;
- the related README, CHANGELOG, and project-context documentation.

Those regressions were not intentional. Do not use `ConditionalConfigSync_1.0.3_Release_20260721_0123.zip` as a baseline.

The corrected 1.0.3 project was produced by applying only the initial-handshake transport fix and release metadata to `ConditionalConfigSync(7).zip`. The policy-control files in the corrected project were verified byte-for-byte against the reference. The 1.0.3 functional change was:

- buffer vanilla `PlayerList` and `AdminList` together with `PeerInfo`, `RoutedRPC`, and `ZDOData`;
- release them only after `PeerInfo`, preserving the initial player list and `LocalPlayerIsAdminOrHost()`;
- restore the inspected `ZPackage` cursor before buffering or forwarding;
- retain protocol version 1 and core assembly identity `1.0.0.0`.

The current 1.0.4 work starts from the corrected 1.0.3 state plus the version-diagnostics work in `ConditionalConfigSync_1.0.4_VersionDiagnostics_20260722_1217.zip`. Version 1.0.4 is still unreleased in that historical handoff. The latest source tree must retain every API and transport change from the accepted reference and 1.0.3.

For the 1.0.5 work started on 2026-08-20, the authoritative baseline is the owner-supplied `ConditionalConfigSync.zip` from this chat, SHA-256 `7f06152b4e8a412bb0ddeb91c4160629d9733b126f00ceb08bffa38a7b92e3a5`. That archive already contains the accepted 1.0.4 connection diagnostics, optional-server ownership fix, disconnect-reason UI integration, Visual Studio project-tree fix, packaging metadata, and the full durable project context. Do not reconstruct 1.0.5 from one of the older generated archives when this baseline is available.

For the 1.0.6 conditional mod-requirement work started on 2026-09-12, the authoritative baseline is the owner-supplied `ConditionalConfigSync(5).zip`, SHA-256 `b8d8ab6c6cc65d68a79e15351cf54e0786c2ad245d6622b280075ac28407982f`. It already contains the accepted 1.0.5 snapshot-cache implementation and the later connection-rejection diagnostics. The 1.0.6 work is an additive public API and server admission-policy feature; do not reconstruct it from older handoffs.

### Current release identity

- Package version: `1.0.6`
- CCS wire protocol: `1`
- Disconnect-report subformat: `1`
- Core `AssemblyVersion`: `1.0.0.0`
- Core and plugin file/informational version: `1.0.6`
- BepInEx GUID and Harmony owner: `_shudnal.ConditionalConfigSync`
- Jotunn Harmony owner used only for patch ordering: `com.jotunn.jotunn`
- ServerSync Harmony owner used only for patch ordering: `org.bepinex.helpers.ServerSync`

The additional package-version string in the ordinary version handshake is an optional trailing protocol-1 field. It does not justify a CCS protocol bump. The disconnect report is a separate best-effort RPC with its own internal format version and likewise does not change the main protocol.

### 1.0.6 conditional mod requirements

Version 1.0.6 adds an explicit admission-policy mode for `ModRequired` without changing the existing `ModRequired` member, core assembly identity, or protocol-1 version packet. The feature is intentionally additive so consumers compiled against 1.0.5 continue to load and retain their exact fixed requirement behavior without recompilation.

The public contract is:

```csharp
public bool ModRequired { get; set; } = false;
public ModRequirementMode ModRequirementMode { get; set; } = ModRequirementMode.Fixed;
```

`ModRequirementMode.Fixed` is the backward-compatible default. Existing consumers therefore remain fixed even when the server has a `ModRequirements.cfg` rule for their GUID. `ModRequirementMode.Conditional` is an explicit author opt-in allowing a server administrator to override the author's `ModRequired` default for incoming clients only.

The policy is intentionally asymmetric:

- the client-side requirement remains the author's local `ModRequired` value; a client with `ModRequired = true` still refuses to join a server that does not provide the consumer;
- the server may apply `+ ModGuid` (`ForceRequired`) or `- ModGuid` (`ForceOptional`) from `ConditionalConfigSync.ModRequirements.cfg` only when that consumer uses `ModRequirementMode.Conditional`;
- a server override never becomes a synchronized config value and is never trusted from a client;
- this is consumer compatibility policy, not a general allow/deny list for unrelated client mods.

Conditional consumers always advertise their existing version handshake from the client, even when the author default is optional. This is necessary because a server may force an author-default optional consumer to required and must distinguish "installed and compatible" from "missing" before `PeerInfo` admission. Fixed optional consumers retain the original one-sided handshake behavior. No new field is added to the packet, so the main CCS protocol remains `1`.

Valheim 1.0.12 source at `assemblies_combined` commit `62d166f0baddaf3ab09ee51ce268859040dc2000` was checked for the unmodded-peer path. `ZRpc.HandlePackage` invokes a direct RPC only when its method hash is registered, and `ZRoutedRpc.HandleRoutedRPC` likewise invokes a routed method only when it exists in `m_functions`. Unknown CCS handshake and synchronization methods are therefore ignored by vanilla peers, matching the pre-existing `ModRequired = false` transport model. Conditional admission does not require a new capability-negotiation packet or a different synchronization transport.

When the server's effective Conditional requirement is optional, a missing client consumer is accepted. If a client does advertise that Conditional consumer, normal mod-version and CCS protocol validation still applies; `ForceOptional` is not an instruction to ignore an incompatible installed copy. An author-default required consumer therefore keeps its normal minimum-version fallback even when the server permits absence. Conversely, `ForceRequired` on an author-default optional consumer uses the existing required-consumer `CurrentVersion` fallback when `MinimumRequiredVersion` was not explicitly supplied. Existing `Fixed + ModRequired = false` behavior is preserved and is not retroactively tightened.

The effective server requirement is snapshotted per `ZRpc` during `OnNewConnection`. A policy reload therefore affects only later connection attempts. It never changes an admission decision halfway through a handshake and never disconnects already connected peers. The per-peer snapshot is cleared on disconnect and session reset.

The server-only policy file is:

```text
BepInEx/config/shudnal.ConditionalConfigSync/ConditionalConfigSync.ModRequirements.cfg
```

It participates in the same stable read, file-watcher, reload, validation, status, and policy-dump pipeline as `SyncPolicy.cfg` and `HiddenConfigs.cfg`. Requirement rules are exact consumer GUIDs only; unlike config ownership policy they do not have section or setting targets.

Compatibility requirements for this feature:

- core `AssemblyVersion` remains `1.0.0.0`;
- `PluginInfoCCS.ProtocolVersion` remains `1`;
- no existing public member is removed, renamed, or changes type/signature;
- `ModRequirementMode` defaults to `Fixed`;
- unchanged consumers compiled against CCS 1.0.5 must load against the 1.0.6 core DLL without rebuilding;
- only consumers that set `ModRequirementMode.Conditional` need to declare CCS 1.0.6 as their minimum package dependency.

#### 1.0.6 implementation and validation handoff

The 1.0.6 source handoff adds `ModRequirementMode.cs`, extends `ConditionalConfigSync.cs`, `VersionCheck.cs`, `Parts/Policy.cs`, and policy watcher cleanup, updates package metadata to 1.0.6, and synchronizes README/CHANGELOG/PROJECT_CONTEXT staging copies. The stale 1.0.5 `SHA256SUMS.txt` was removed from the source handoff because no 1.0.6 binaries were built; the existing packaging target regenerates checksums from the actual compiled DLLs before creating the Thunderstore archive.

Static verification for this handoff includes structured XML/JSON parsing, normalized public-declaration comparison against the supplied 1.0.5 baseline, protocol/core-assembly identity checks, C# lexical delimiter/string/comment balance, staged-document byte equality, generated-binary absence, and accidental-Cyrillic scanning. Per the maintainer workflow for Valheim mods, this handoff is not compiled or runtime-tested here. Runtime verification must cover the Conditional requirement cases in the global regression matrix below.

### 1.0.5 full-sync snapshot cache and serialization diagnostics

The 2026-08-20 optimization request came from a ServerSync migration case where a consumer mod concatenated large YAML files into one synchronized string. The important distinction is that the inefficiency is not merely "strings instead of bytes": every network payload is bytes on the wire. The costly pattern is sending source representation to the client, which causes large text allocation plus repeated client-side parsing instead of synchronizing already parsed runtime state. CCS must not parse application YAML itself because it does not know the consumer schema, but it can avoid repeating its own serialization/compression work and can make large payloads observable.

The owner explicitly requested that 1.0.5 keep both the public API and the main wire protocol unchanged. Therefore this release contains only internal transport/serialization optimization and diagnostics. `PluginInfoCCS.ProtocolVersion` remains `1`; `AssemblyVersion` of the core library remains `1.0.0.0`; no new public codec/blob API and no new wire entry kind is introduced.

#### Full server snapshot cache

Complete server packages are now cached as immutable wire bytes after serialization and, when the existing compression threshold is exceeded, after Deflate compression. The cache is per `ConditionalConfigSync` instance and keyed by:

- monotonically increasing authoritative state revision;
- administrator versus non-administrator recipient class, because the full package contains peer-specific `LockExempt` state;
- current server mod version;
- registered config count;
- registered custom-value count.

Only two complete snapshots can be retained for one state revision: one administrator variant and one non-administrator variant. The bytes cached are the final package passed into fragmentation/distribution, so cache hits skip both `ConfigsToPackage(...)` and `CompressPackage(...)`. A new `ZPackage` wrapper is created from the immutable cached byte array for each send; the cached array itself is not mutated.

The same cache is used by all paths that require a full authoritative package:

- initial sync during peer admission;
- explicit or automatic complete resync;
- rate-limited full authoritative correction after a rejected/invalid client update.

Partial/config/custom-value broadcasts remain uncached because their contents represent individual state transitions.

The pre-compression 20 MiB safety limit is unchanged. A snapshot whose raw serialized size exceeds that limit is not made acceptable merely because it might compress well. Likewise, an invalid oversized snapshot is not retained in the cache, and CCS does not make an additional cached-byte copy of a snapshot that will be rejected. This preserves the existing memory-safety contract and avoids amplifying allocations on an already oversized payload.

The snapshot revision/cache is invalidated when CCS can observe any authoritative state change that affects a future full package:

- a registered server-controlled BepInEx config value whose value is present in a full package changes on the server; client-owned/local-only value changes do not unnecessarily discard an otherwise identical full snapshot;
- a custom synchronized value changes or is explicitly notified on the server;
- an accepted client update changes canonical server state;
- effective synchronization/hidden policy state changes;
- a config entry is registered during an active server session;
- a custom value is registered during an active server session;
- the network session resets.

`CurrentVersion` is also compared directly when looking up a cached snapshot, so changing version metadata cannot reuse an older full package even though it is a public compatibility field rather than an observable property setter. Registered counts are compared as an additional safety check. Public compatibility fields such as `OwnConfigEntryBase.SyncMode` and `SynchronizedConfig` are still intended to be configured during registration; runtime changes should continue to use supported policy/config pathways so invalidation and broadcast semantics remain coherent. Likewise, mutable custom-value objects, collections, arrays, and other reference types must call `NotifyChanged()` after in-place mutation. That call was already required for correct network publication and now also advances the full-snapshot revision. Code that silently mutates a custom object and relied on a later full resync noticing the changed object graph was already outside the documented synchronization contract and can now receive a cached earlier snapshot.

Administrator-list changes do not invalidate the cache because admin and non-admin variants are already separate. The current peer classification is evaluated each time a full snapshot is selected.

#### Serialization and compression diagnostics

Diagnostics were added without changing package bytes. The instrumentation is deliberately gated so normal operation does not pay reflection/package-size measurement costs merely for logging.

At `Verbose` level CCS logs:

- partial/full package serialization counts;
- raw serialized package size;
- package serialization elapsed time;
- compression elapsed time for ordinary outgoing packages;
- raw and compressed/wire sizes;
- compression ratio and percentage saved;
- full-snapshot revision, administrator class, build versus reuse state, and the build serialization/compression times.

Representative log shapes:

```text
[Some Mod][Server][Serialization] Serialized full package: configs=12, states=18, custom=3, metadata=2, raw=4.80 MiB (5033165 bytes), time=41.20 ms
[Some Mod][Server][Snapshot] Built full-sync snapshot: revision=17, admin=False, configs=18, custom=3, raw=4.80 MiB (...), compressed=612.0 KiB (...), ratio=12.5%, saved=87.5%, serialize=41.34 ms, compress=73.11 ms
[Some Mod][Server][Snapshot] Reusing full-sync snapshot: revision=17, admin=False, raw=4.80 MiB (...), wire=612.0 KiB (...), compressed=True, buildSerialize=41.34 ms, buildCompress=73.11 ms
```

At `Trace` level CCS additionally logs each serialized config/custom payload independently, including its identifier/type and payload byte count. This is the preferred mode for identifying which custom value dominates a large synchronization package. It is especially useful for the owner's planned texture/blob test mod.

`AddEntryToPackage` reuses the payload byte array it already has to write into the parent package when reporting per-entry size. It does not add an extra `PackageSize` reflection call for every entry. Package Stopwatch timing is enabled only when the corresponding `Verbose` diagnostics are active. This keeps the optimization from creating a new always-on profiling cost.

A large outgoing `CustomSyncedValue<string>` at or above 128 KiB emits one warning per identifier per synchronization instance per network session even when debug logging is disabled. The warning is advisory only: the value is still sent normally. Its purpose is to highlight source-text synchronization where parsed structured/binary runtime data could reduce repeated allocation and parsing. The warning set is reset with network-session state.

#### Important non-goals for 1.0.5

Do not add any of the following as part of this release unless the owner explicitly starts a separate protocol/API task:

- automatic YAML/JSON parsing in CCS;
- textual delta/patch generation for large strings;
- a new public `ISyncCodec<T>`/blob API;
- a different wire representation for `byte[]` or collections;
- schema-aware collection encoding that removes repeated type metadata;
- protocol revision negotiation or content-hash/chunk caches;
- a change from Deflate purely for this optimization.

Those can be considered after real measurements. 1.0.5 is intended to provide those measurements while improving the server-side repeated-connect/resync cost immediately.

#### 1.0.5 regression checklist

In addition to the global regression list later in this document, verify these cases when a runnable Valheim environment is available:

1. Start a server with one sync instance containing a large custom value; enable `Verbose`; first non-admin connection logs `snapshot=built`, a later non-admin connection with unchanged state logs `snapshot=reused`.
2. Connect an administrator after the non-admin snapshot exists; an admin snapshot is built once and then reused for later admins.
3. Change one synchronized config; both admin/non-admin snapshot variants are invalidated and the next full sync builds a new revision.
4. Change/notify one custom value; next full sync builds a new revision.
5. Reload policy so effective ownership or hidden state changes; next full sync builds a new revision.
6. Accept an authorized client update on the server; next full sync reflects the canonical updated value and does not reuse the old revision.
7. Register a late config/custom value; partial late-registration broadcast still occurs and subsequent full resync includes it in a new snapshot revision.
8. Issue a complete resync twice without changing state; the second request reuses the relevant snapshot.
9. Trigger a full authoritative correction after a rejected client update; it uses the same snapshot cache.
10. Enable `Trace` with a mod-name filter; verify individual config/custom payload sizes and ensure large byte/blob data is identifiable.
11. Send a `CustomSyncedValue<string>` whose serialized payload is at least 128 KiB; verify exactly one advisory warning per session for that identifier even with debug disabled.
12. Verify a string below the threshold produces no warning.
13. Verify ordinary partial packages still use the existing compression path and `Verbose` reports raw/compressed ratio and compression time.
14. Verify raw payloads above 20 MiB are still rejected before compression and are not retained as reusable snapshots.
15. Verify client/server 1.0.5 interoperates with protocol-1 1.0.4 peers wherever the existing optional handshake metadata rules allow it; no main protocol mismatch should be introduced by this release.
16. Compare package bytes produced from the same state with 1.0.4 to ensure no serialization-format change was introduced by the diagnostics/cache refactor.
17. Change a client-owned/local-only registered config on the server and verify an otherwise identical full snapshot is not discarded; change a server-controlled synchronized config and verify the revision advances.
18. Build an oversized full package and verify it is rejected under the existing limits without retaining the snapshot or creating an additional cached wire-byte copy.

#### 1.0.5 implementation and validation handoff

Files intentionally changed in the core implementation for 1.0.5:

- `ConditionalConfigSync.cs`: invalidate the reusable full snapshot when configs/custom values are registered during an active authoritative session;
- `Parts/Diagnostics.cs`: internal byte-size, elapsed-time, and compression-ratio formatting helpers;
- `Parts/Packages.cs`: gated serialization timing, per-entry payload diagnostics, and the large-string advisory;
- `Parts/Stabilization.cs`: full-snapshot revision/cache, admin/non-admin variants, cache-aware initial/resync/correction sending, and session cleanup;
- `Parts/Transport.cs`: snapshot invalidation on observable authoritative value changes, prepared-package sending, and compression timing;
- `Parts/Policy.cs`: snapshot invalidation when effective ownership/hidden policy state changes;
- `PluginInfoCCS.cs` and `PluginSelfInfo.cs`: package version `1.0.5`, protocol still `1`.

The public declaration shape of the core source was compared to the supplied 1.0.4 baseline after normalizing the package-version literal; no public declaration was added, removed, or signature-changed by this work. `VersionCheck.cs`, `Parts/PolicyControl.cs`, `SyncedConfigEntry.cs`, `ConfigSyncPolicyControlState.cs`, and `RuntimeGuard.cs` were verified unchanged from the supplied baseline. Root and staged README/CHANGELOG/PROJECT_CONTEXT copies were synchronized, project XML and manifest JSON parsed successfully, a lexical C# delimiter/string/comment balance check passed, and repository text outside binaries contained no Cyrillic.

This execution environment does not provide `dotnet`, `msbuild`, `csc`, `mcs`, or the normal Valheim/BepInEx build reference tree, so 1.0.5 was not compiled or runtime-tested here. The deliverable produced from this environment must therefore be treated as a source project. Stale 1.0.4 `bin`/`obj`, staged DLL/XML/checksum artifacts, and the old generated package ZIP from the supplied reference must be removed from that source archive rather than represented as 1.0.5 binaries. The owner will compile and profile the result in the normal Visual Studio/Valheim environment.

##### 1.0.5 compilation follow-up: `FullSyncSnapshot` field accessibility

The owner's Visual Studio build of the first 1.0.5 source handoff reported protection-level errors for `ConditionalConfigSync.FullSyncSnapshot.Revision` and the other fields of the nested snapshot container. The generated implementation declared those fields `private` but accessed them from the surrounding `ConditionalConfigSync` implementation. This was a source-level compile defect in the unreleased 1.0.5 handoff, not a protocol or runtime design issue.

The fix is intentionally narrow: every data field of the still-`private sealed` `FullSyncSnapshot` type is now `internal`. The snapshot type itself remains private to `ConditionalConfigSync`, so this does **not** add any public API surface, does not change the package format, and does not change protocol `1`. Do not add a CHANGELOG entry for this intermediate compile repair because 1.0.5 has not been released and the owner requires changelogs to contain only user-visible release changes and meaningful performance improvements.

Regression requirement: compile both core and plugin projects in Visual Studio before profiling the 1.0.5 snapshot cache. In particular, verify all accesses to `Revision`, `Admin`, `ServerVersion`, `ConfigCount`, `CustomValueCount`, `RawSize`, `WireSize`, `Compressed`, `SerializationMilliseconds`, `CompressionMilliseconds`, and `WireBytes` compile without accessibility errors.

### Visual Studio CPS project-tree compatibility

Visual Studio reported the `ConditionalConfigSync.Plugin` project as `LimitedFunctionality` while builds and Thunderstore packaging continued to work. The ActivityLog exception stated that `Thunderstore.targets` was found in an invalid project-tree state with both `ProjectImport` and `FileOnDisk`/`FileSystemEntity` flags.

The project is SDK-style, so files without another build action are implicitly included as `None` items. `Thunderstore.targets` was also explicitly imported at the bottom of `ConditionalConfigSync.Plugin.csproj`. MSBuild could evaluate this correctly, which is why compilation and packaging worked, but Visual Studio CPS attempted to represent the same physical path as both a regular project item and an imported project and failed while constructing the physical project tree.

The accepted project-file form is:

```xml
<ItemGroup>
  <None Remove="Thunderstore.targets" />
</ItemGroup>

<Import Project="$(MSBuildProjectDirectory)\Thunderstore.targets"
        Condition="Exists('$(MSBuildProjectDirectory)\Thunderstore.targets')" />
```

Preserve all three details:

1. remove only `Thunderstore.targets` from the implicit `None` list;
2. keep the packaging logic in the separate imported file;
3. use a normalized project-directory path with an existence condition.

Do not disable `EnableDefaultNoneItems` for the whole project merely to solve this collision, because that would unnecessarily remove normal non-code project files from SDK item discovery. This is a project-system compatibility fix only: package version remains `1.0.4`, wire protocol remains `1`, and no runtime assembly behavior changes.

Regression checks for packaging-project changes:

- opening or reloading `ConditionalConfigSync.Plugin.csproj` in Visual Studio must not produce `LimitedFunctionality` or a duplicate `Thunderstore.targets` project-tree node;
- `Thunderstore.targets` must still be imported and its packaging targets must remain available to MSBuild;
- a normal plugin build must still stage the Thunderstore and GitHub release artifacts;
- the project must not globally disable SDK default `None` items.

### 1.0.4 optional-server ownership fix and ConfigurationManager companion release

The 2026-07-23 investigation used two logs from the same Linux client session and the `ConfigurationManager(9).zip` source archive. The client loaded CCS `1.0.3`, Valheim Configuration Manager `1.1.15`, Jotunn `2.29.2`, and My Little UI `1.2.15`. The server sent CCS handshakes for Valheim Configuration Manager, Extra Slots, Extra Slots Custom Slots, and Longship Upgrades, but no My Little UI handshake was present. Jotunn later confirmed that the account was an administrator.

My Little UI is intentionally optional on the remote side:

- its `ConfigSync` uses `ModRequired = false`;
- its ordinary settings are registered as `Conditional`;
- its locking entry is fixed `AlwaysServerControlled`.

The client therefore had a valid optional client-only deployment: My Little UI was installed locally and absent from the server. The connection was correctly admitted, but CCS left that `ConfigSync` in the pre-sync replica state established during `ZNet.Awake`:

- `IsSourceOfTruth = false`;
- `InitialSyncDone = false`;
- `IsWritableConfig(...)` deliberately fails closed before initial synchronization;
- the process-wide administrator exemption received through another CCS consumer could not bypass the earlier `InitialSyncDone` gate.

This was a CCS lifecycle bug, not a My Little UI registration bug. It could affect any pre-connection CCS consumer with `ModRequired = false` when the connected server did not provide the same consumer.

The accepted fix is performed only after successful client `RPC_PeerInfo` completion. For every version check backed by a `ConfigSync`, CCS now resolves missing optional server presence when all of the following are true:

- the local side is a client;
- the version check is backed by a `ConfigSync`;
- `ModRequired` is false;
- no matching server handshake was received;
- peer admission completed without `ErrorVersion`.

The affected `ConfigSync` then:

1. keeps `InitialSyncDone = false`, because no complete server package was applied;
2. restores local fallback values and default ownership/visibility state;
3. returns to `IsSourceOfTruth = true`;
4. recalculates ConfigurationManager `ReadOnly` and `Browsable` metadata;
5. clears pending outbound packages and fragment state;
6. writes one normal informational log that local ownership is used for this optional mod.

It deliberately does **not** raise `InitialSyncCompleted`, because absence of a server provider is not synchronization. It also does not publish local values to the server: client broadcasting still requires `InitialSyncDone`, so the optional local instance remains local-only for that session. `SourceOfTruthChanged(true)` remains the lifecycle signal for consumers that care about the role transition.

Do not replace this fix by merely moving the administrator check ahead of the `InitialSyncDone` fail-closed gate. That would weaken the pre-sync security invariant, leave non-admin optional clients read-only, and conflate administrator authorization with the absence of a remote synchronization provider.

The supplied Configuration Manager source contained a second, independent use of vanilla administrator state:

```csharp
return hiddenSettings.Value.Count > 0 && ZNet.instance != null && !ZNet.instance.LocalPlayerIsAdminOrHost();
```

That check controls whether the server-provided hidden-settings list is applied. Configuration Manager `1.1.16` replaces it with CCS's effective administrator state:

```csharp
return hiddenSettings.Value.Count > 0 && !configSync.IsAdmin;
```

This companion change prevents delayed or stale vanilla `AdminList` data from hiding settings from an administrator. It is separate from the My Little UI read-only failure: My Little UI was blocked by CCS's unresolved optional replica state, while Configuration Manager's own hidden-settings filter directly consulted vanilla `LocalPlayerIsAdminOrHost()`.

Release relationship for this handoff:

- CCS remains package version `1.0.4`, protocol `1`, core `AssemblyVersion` `1.0.0.0`;
- Configuration Manager is raised from `1.1.15` to `1.1.16`;
- Configuration Manager `1.1.16` declares Conditional Config Sync `1.0.4` as its minimum package dependency;
- My Little UI source does not need a code change for this incident.

Known boundary: a `ConfigSync` constructed only after peer admission cannot have participated in the completed version handshake. Existing late-registration resync behavior remains in place; this 1.0.4 fix resolves optional consumers that existed during the connection handshake, which includes the reported My Little UI case.

### 1.0.4 version-diagnostics requirements

Version admission failures must be logged as unconditional errors. They must never depend on `ConditionalConfigSync.Debug.cfg`, debug level, or the mod-name filter.

Each server-side rejection produces a unique report ID. The server writes an unconditional error summary containing that ID, reason count, and remote identifier before attempting the report RPC; a current client logs the same ID when it receives the report. This provides a direct correlation key between the dedicated-server log and the rejected player's log.

The server must distinguish at least:

- no matching handshake for the required consumer mod;
- handshake received without the CCS protocol field;
- explicit protocol mismatch;
- malformed remote mod-version data;
- malformed local version requirements;
- remote mod older than the local minimum;
- local mod older than the remote minimum;
- malformed version packages.

Successful server-side receive logs include the same remote client identifier later used in rejection logs. Received handshake state is per `ZRpc`; simultaneous connections must not overwrite each other's versions, protocol, package-version metadata, timing, or rejection reason.

For a missing handshake, server diagnostics must not claim that the mod is definitely absent. The server only knows that it did not receive the required handshake. Current user-facing and administrator-facing potential causes are intentionally limited to:

- the consumer mod is missing or disabled;
- an older pre-CCS consumer-mod build is installed;
- CCS is missing or failed to load;
- a duplicate or outdated DLL is present.

Do not currently advertise a packet-order race as a likely player-facing cause.

### Incident evidence from `LogOutput (60).log`

The reported Longship Upgrades incident was analyzed by grouping the dedicated-server log into connection attempts.

Server startup showed:

- Conditional Config Sync `1.0.3`;
- Longship Upgrades `1.0.17`.

Observed clients:

| Steam ID | Player | Successful CCS handshakes | CCS rejections | Recorded behavior |
|---|---|---:|---:|---|
| `76561197977100993` | `betlog` | 5 | 0 | Always succeeded |
| `76561198822797834` | `jaren` | 3 | 0 | Always succeeded |
| `76561199128971616` | `saitamasway` | 7 | 0 | Always succeeded |
| `76561198057634224` | name not received | 0 | 4 | Always rejected |
| `76561198372601265` | name not received | 0 | 2 | Always rejected |

Totals: 21 attempts, 15 successful Longship Upgrades CCS handshakes, 6 CCS rejections, 5 unique clients.

The affected clients passed the version checks for the other ServerSync-based mods, but the server never logged a Longship Upgrades CCS receive line for them. The behavior was therefore consistent per client installation, not intermittent for the same Steam ID. This strongly supports a client-profile difference rather than a server-wide random failure.

### Player-visible CCS rejection architecture

CCS owns the rejection reason but must not own a separate modal window.

The accepted flow is:

1. The rejecting side creates one structured report containing a unique report ID and one or more reason items.
2. The server logs every rejection reason unconditionally.
3. The server sends the bounded report through the direct `ConditionalConfigSync DisconnectReason` RPC.
4. The server invokes vanilla `ErrorVersion`.
5. The current client stores the report for the active connection generation; it does not open UI from the RPC handler.
6. `FejdStartup.ShowConnectError` appends the CCS explanation to the existing Valheim error text.
7. Jotunn may copy that enriched text into its own compatibility window.
8. Otherwise the vanilla panel, optionally also modified by ServerSync, displays the combined text.
9. A one-frame deferred normalization adjusts only the still-active vanilla panel after all synchronous postfixes finish.

The central compatibility invariant is: **append, never replace**.

CCS must not:

- hide the vanilla panel;
- destroy or replace Jotunn's compatibility window;
- clear ServerSync text;
- create a second competing modal;
- assume it is the only error provider;
- repeatedly resize or move the confirmation button.

### Jotunn compatibility details

Jotunn patches `FejdStartup.ShowConnectError` with a last-priority postfix. When Jotunn has valid server version data and the status is `ErrorVersion`, it reads `m_connectionFailedError.text`, starts its own compatibility-window coroutine, and hides the vanilla panel.

CCS therefore installs its text-injection postfix at first priority and explicitly orders it before `com.jotunn.jotunn`. Jotunn then receives the CCS-enriched failed-connection text and keeps its own independent window and layout.

The CCS one-frame layout coroutine must immediately stop if the vanilla panel is no longer active. This is the expected Jotunn path, not an error.

There is no compile-time or runtime API dependency on Jotunn. The Harmony owner string is used only for deterministic ordering when both mods are installed.

### ServerSync compatibility details

ServerSync appends its own version messages to `m_connectionFailedError.text` and expands the vanilla panel in a `ShowConnectError` postfix.

CCS explicitly orders its injection before `org.bepinex.helpers.ServerSync`. ServerSync can therefore append its own diagnostics after the CCS block. CCS defers layout normalization by one frame, calculates the target from the final rendered text, and only increases the current panel dimensions by the missing delta. The confirmation button moves only by half of that additional height. Repeating the same error-display path for one connection generation must not move it again.

There is no compile-time or runtime API dependency on ServerSync.

### Disconnect-report wire layout

The current structured disconnect-report format is internal and bounded:

```text
int    report format version = 1
string report ID
string server CCS package version
int    server CCS protocol version
int    reason count (0..32)
repeat reason count:
    byte   reason code
    string mod display name (normalized and bounded)
    string English reason text (single-line, normalized and bounded)
```

Current reason codes:

- `Unknown`
- `HandshakeMissing`
- `ProtocolNotReported`
- `ProtocolMismatch`
- `RemoteVersionInvalid`
- `LocalVersionInvalid`
- `RemoteVersionTooOld`
- `LocalVersionTooOld`
- `MalformedHandshake`
- `MissingConsumerRegistration`

The receiver rejects reports larger than 512 KiB, invalid counts, unsupported small structured-format versions, and trailing bytes; it bounds all fields, escapes angle brackets before TMP display, and accepts the plain-string format used by early unreleased 1.0.4 builds as a temporary compatibility fallback.

The final displayed message is capped at 8192 characters. A structured report is associated with the connection generation captured when its direct RPC handler is registered. Reports from an older `ZRpc` cannot be shown for a newer connection.

### Pending-reason lifecycle

A pending report is retained for at most 30 seconds and is cleared when:

- a new client connection starts;
- peer admission succeeds;
- the report is appended to the error text;
- the report expires;
- the runtime shuts down normally.

A failed `ZNet` shutdown commonly happens before the main-menu error form is displayed. `ResetNetworkSessionState()` therefore preserves the UI handoff only when a current pending CCS report already exists. Such a report is created either by the server's disconnect-report RPC, by a client-side CCS admission rejection before `Logout()`, or by the bounded fallback for an unreadable CCS disconnect report. Ordinary handshake state is cleared, while the report and active connection generation survive until `ShowConnectError` consumes them. Plugin destruction still force-clears everything.

CCS must not infer ownership of an error from `ErrorVersion` alone and must not synthesize a report merely because an incomplete handshake state remains. Jotunn, ServerSync, or another mod may have caused that version error. This exact-origin rule prevents CCS text from contaminating another compatibility provider's rejection window.

This preservation is required. Clearing an already-created report unconditionally in `ZNet.Shutdown` would reproduce the original user-visible problem even though the reason RPC had been received successfully.

### Client-version behavior

- Server 1.0.4 with client 1.0.4: full server diagnostics, remote CCS package version, structured report, and player-visible reason.
- Server 1.0.4 with client 1.0.3: detailed server diagnostics still work; the old client normally sees only vanilla `ErrorVersion`.
- Server 1.0.3 with client 1.0.4: protocol 1 remains compatible and the optional package-version field is ignored by old handlers; if that old server rejects the client, it cannot send the new structured report, so the player normally sees only the vanilla error.
- Client without CCS or with CCS failing before RPC registration: the server can log the cause and send best effort, but no CCS code exists on the client to display the report.

Do not promise a player-visible CCS reason when CCS itself is absent from the rejected client.

### Files intentionally changed by the latest UI refinement

Functional code:

- `ConditionalConfigSync/VersionCheck.cs`
- `ConditionalConfigSync/GameReflection.cs`
- `ConditionalConfigSync/Parts/Stabilization.cs`

Documentation:

- `README.md`
- `CHANGELOG.md`
- `PROJECT_CONTEXT.md`
- staged GitHub and Thunderstore README/CHANGELOG copies

No policy-control, configuration-ownership, synchronization-package, or transport behavior should change as part of this UI refinement.

### 1.0.4 compilation follow-up

A user-side build of the prepared 1.0.4 source exposed `CS0103` for `Canvas.ForceUpdateCanvases()` in `VersionCheck.NormalizeConnectionErrorLayout`. The project intentionally references `UnityEngine.CoreModule` and `UnityEngine.UI`, but not `UnityEngine.UIModule`, which is where `UnityEngine.Canvas` is defined in the modular Unity assemblies used by the target setup.

The accepted fix is to remove the unnecessary `Canvas.ForceUpdateCanvases()` call instead of adding a new `UnityEngine.UIModule` reference solely for that line. Layout normalization already waits one frame, then calls `TMP_Text.ForceMeshUpdate()` and `LayoutRebuilder.ForceRebuildLayoutImmediate(...)`, which are sufficient for the bounded vanilla error-panel recalculation used here.

This correction does not change runtime ownership, synchronization, admission, disconnect-report, protocol, public API, or assembly-identity behavior. If future code directly uses `Canvas` or another type from `UnityEngine.UIModule`, the project reference must be added explicitly rather than assuming that `UnityEngine.dll` or `UnityEngine.UI.dll` provides it.

### Validation status for this handoff

The available environment does not contain a .NET/Mono compiler or the Valheim/BepInEx reference assemblies, so a real build and runtime test cannot be claimed.

Before release, perform at minimum:

- compile both assemblies against the intended stable/publicized Valheim references;
- connect a current rejected client without Jotunn or ServerSync and verify the vanilla panel;
- connect with ServerSync installed and verify both text blocks and one stable button position;
- connect with Jotunn installed and verify only Jotunn's compatibility window is visible and contains the CCS text;
- connect with both Jotunn and ServerSync;
- reject for every reason code;
- test two simultaneous clients with different results;
- verify an old client still disconnects normally;
- verify normal successful connection clears pending data;
- verify a failed shutdown preserves the report until the main menu displays it;
- verify a second connection never shows the first connection's report;
- verify no public API or assembly identity change;
- verify protocol remains 1;
- scan all repository text for accidental Cyrillic;
- synchronize staged release documentation.

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

### `ConditionalConfigSync.ModRequirements.cfg`

Mod-requirement rules target one exact registered consumer GUID. `+` means `ForceRequired`; `-` means `ForceOptional`. Rules are effective only for consumers whose authors selected `ModRequirementMode.Conditional`; fixed consumers log/validate the record as ignored.

Every record is preserved in file order for diagnostics. A rule that changes the author default is a warning because the administrator is deliberately changing the mod author's recommended admission contract. A same-as-default rule is informational. Unknown GUIDs and malformed/duplicate/conflicting records are reported by policy diagnostics.

Requirement policy is evaluated locally on the server before normal config synchronization and is not broadcast as config state. A reload changes the effective requirement used for new `ZRpc` admission snapshots only. Existing connected peers and in-progress snapshots are not rewritten or disconnected.

### Stable file loading

Policy reads retry until file metadata is stable. This protects against common editor save patterns such as truncate-write-rename and avoids replacing a valid policy with a partially written snapshot. File watcher callbacks queue work to the Unity main thread before mutating runtime state.

### Runtime policy toggle API

Compatible configuration UIs may request an exact-setting ownership toggle only when:

- the setting is `Conditional`;
- the server advertises the capability;
- the requesting client is an administrator;
- the request resolves to the expected registered setting.

The server persists a minimal exact rule and remains the source of truth. The UI request is not a direct local ownership mutation.

`SynchronizationPolicyControlState` exposes the reason policy control is unavailable without requiring a UI to infer network or administrator state. It distinguishes fixed ownership, an unavailable or incompatible server session, missing administrator access, and an available operation.

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

### Initial peer handshake ordering

During server-side `RPC_PeerInfo` processing, CCS temporarily wraps the peer socket so the CCS full synchronization package can be sent before buffered vanilla initialization traffic is released. The wrapper must preserve the relative order of the initial vanilla RPCs.

`PlayerList` and `AdminList` are registered by the client while it processes `PeerInfo`. They therefore must be buffered together with `PeerInfo`, `RoutedRPC`, and `ZDOData`; otherwise they can overtake `PeerInfo`, arrive before their handlers exist, and leave the initial player list or `LocalPlayerIsAdminOrHost()` state incorrect until a later refresh.

The buffering implementation must preserve the original `ZPackage` cursor after inspecting the RPC method hash. Buffered packages must be cloned because Valheim may reuse the original package instance. `VersionMatch` must be replayed at the same logical position relative to the buffered packages, and the buffer must always be flushed from a `finally` path if CCS synchronization fails.

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

A late resync updates data and policy state. It does not retroactively repeat the completed Valheim peer admission and version check. Mods requiring connection admission enforcement must create their `ConfigSync` and set `ModRequired`/`ModRequirementMode` before connection.

## `ModRequired` and mod-requirement policy semantics

`ModRequired` is the owning mod author's default remote-installation requirement. It is independent from the local hard dependency on CCS itself. `ModRequirementMode` determines whether the server may override that default for incoming clients.

### Fixed requirement

`ModRequirementMode.Fixed` is the default and preserves the pre-1.0.6 contract exactly:

- on a client with `ModRequired = true`, the server must have a compatible copy of the owning mod;
- on a server with `ModRequired = true`, connecting clients must have a compatible copy;
- with `ModRequired = false`, the remote side may lack the owning mod;
- server requirement-policy records are ignored.

This default is the binary-compatibility path for consumers compiled against CCS 1.0.5 and earlier supported 1.x APIs. They do not know about `ModRequirementMode`, so the new property remains `Fixed` automatically when they run against the newer DLL.

### Conditional requirement

`ModRequirementMode.Conditional` is an explicit author opt-in. The author's `ModRequired` value remains the default and still controls the local client's requirement that the server provide the mod. The server may independently override whether **incoming clients** must provide it:

- `+ ModGuid` in `ConditionalConfigSync.ModRequirements.cfg` forces `Required`;
- `- ModGuid` forces `Optional`;
- no matching rule preserves the author's `ModRequired` default.

A Conditional consumer always sends its normal version handshake from a modded client, including when the author default is optional. This allows the server to force an optional-by-default consumer to required. If the server's effective requirement is optional, complete absence of the consumer is allowed; if the client advertises the consumer, its normal version/protocol compatibility checks still apply.

The server snapshots the effective requirement per `ZRpc` at `OnNewConnection`. Policy reload changes only later connection attempts and never retroactively disconnects peers. The snapshot must be cleared on disconnect and session reset.

After successful client peer admission, a client instance whose own author default is optional and which received no matching server handshake returns to local source-of-truth ownership instead of remaining a fail-closed replica. That local fallback path does not set `InitialSyncDone`, does not raise `InitialSyncCompleted`, and does not allow client-to-server publication. A server-side `ForceOptional` override does not alter this client-side rule.

Set `ModRequired` and `ModRequirementMode` before connection. Late changes cannot retroactively redo an already completed handshake. A mod such as Seasons that fundamentally requires matching runtime definitions should normally remain `Fixed + true`; a mod author should choose `Conditional` only when the server may knowingly accept degraded behavior from clients that cannot install the mod.

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
using TMPro;
using UnityEngine;
using UnityEngine.UI;
```

Do not conflate package version, dependent mod version, assembly identity, the main CCS protocol, and the disconnect-report format. They solve different problems:

- package/file version identifies the release;
- core `AssemblyVersion` provides stable ABI identity;
- dependent mod version controls that mod's compatibility policy;
- `ProtocolVersion` controls ordinary CCS network compatibility;
- `DisconnectReportFormatVersion` versions only the optional player-facing rejection report.

The ordinary version packet remains:

```text
string consumer GUID
string remote minimum consumer version
string remote current consumer version
int    CCS protocol version
string optional CCS package version
```

Protocol-1 peers that omit the optional final string are valid and are logged as `not reported`.

`VersionCheck` stores received client handshakes by `ZRpc`, not in process-global current-version fields. Validation and logging must always use the state belonging to the peer currently entering `RPC_PeerInfo`.

The direct disconnect-report RPC is registered during the client's `OnNewConnection` prefix. The handler closure captures the current connection generation. It only records bounded data and never opens UI directly.

The `ShowConnectError` postfix uses first priority and explicit `before` ordering for Jotunn and ServerSync. It appends the CCS block synchronously, then defers only the vanilla-panel layout calculation by one frame.

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
- Late-created ConfigSync instances cannot retroactively reject a peer that already completed admission. A late-created optional client instance also cannot use the completed handshake to prove server absence; it relies on the existing explicit resync path.
- Exact protocol matching does not guarantee that every dependent mod has identical optional content unless that mod correctly sets `ModRequired` and its own version requirements.
- Very large custom values can still be expensive within the accepted hard limits; limits protect memory, not application-level efficiency.
- The library cannot guarantee that a mod is semantically safe when an administrator forces a conditional setting to client ownership. Mod authors must choose fixed modes for settings that cannot diverge.

## Regression-prone areas

Exercise extra care in the following code paths:

- source-of-truth transitions during `ZNet.Awake` and `ZNet.Shutdown`;
- initial sync ordering relative to `InitialSyncDone` and read-only recalculation;
- successful optional-mod absence resolution after `RPC_PeerInfo`, including restoration of local ownership without raising `InitialSyncCompleted`;
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

25. Client has an early-created `ModRequired = false` consumer that is absent on the server: connection succeeds, the instance returns to `IsSourceOfTruth = true`, local settings are writable, `InitialSyncDone` remains false, and no client update is sent.
26. Repeat while another CCS consumer provides the process-wide admin exemption: the optional absent consumer must still resolve by ownership, not by weakening the initial-sync gate.
27. Optional consumer exists on both sides: the normal handshake and full sync complete; it must not be incorrectly converted back to local ownership.
28. Required consumer is absent on either side: version admission still rejects the connection and the optional-absence resolver must not run.
29. Subscribe to `SourceOfTruthChanged`, `InitialSyncCompleted`, and `ServerConnectionReset`: optional absence raises only the role transition required by the implementation and never reports a completed server sync.
30. Keep Configuration Manager open during connection: My Little UI rows become editable after successful admission when the server lacks My Little UI, without reopening the window.
31. Provide a Configuration Manager hidden-settings list from the server and connect as an administrator: Configuration Manager `1.1.16` must use `configSync.IsAdmin` and must not hide those rows because of stale vanilla `AdminList` state.
32. Change a local fallback, connect to a server value, disconnect: local fallback is restored and remains persisted.
33. Switch policy server -> client -> server at runtime: active values and read-only state follow the effective owner.
34. Late register a config and custom value on server and client: one batched resync supplies correct state.
35. Reconnect to a different server: no fragment, policy, admin, pending-update, or value state leaks from the previous session.

### Conditional mod requirements

36. Unchanged consumer DLL compiled against CCS 1.0.5 with `ModRequired = true`: load it against the 1.0.6 core without rebuilding; behavior remains fixed-required and a `ModRequirements.cfg` override is ignored.
37. Repeat with an unchanged `ModRequired = false` consumer: fixed-optional one-sided handshake and optional-server local fallback remain unchanged.
38. `Conditional + default Required`, no policy rule: modded client/server connect; an unmodded client is rejected.
39. `Conditional + default Required`, `- ModGuid`: an unmodded/crossplay client with no CCS consumer is admitted and safely ignores unknown CCS RPCs, while a modded client still synchronizes normally.
40. A modded client with `Conditional + default Required` still rejects a server where the consumer is completely absent; server `ForceOptional` never weakens that client-side author default.
41. `Conditional + default Optional`, no policy rule: client advertises the consumer but an unmodded client is accepted.
42. `Conditional + default Optional`, `+ ModGuid`: a compatible modded client is accepted and a missing client consumer is rejected; when no explicit `MinimumRequiredVersion` exists, server admission uses the required-consumer `CurrentVersion` fallback.
43. Effective Optional with an advertised incompatible Conditional consumer: normal version/protocol validation rejects it; optionality permits absence, not incompatible presence.
44. Reload Required -> Optional or Optional -> Required while a client is already handshaking: that connection keeps the requirement snapshot captured at `OnNewConnection`; only later attempts use the new rule. Existing connected peers are never disconnected.
45. `policy_validate` reports malformed, duplicate, conflicting, unknown, and Fixed-mode requirement records; `policy_dump` and `status` show author mode/default and current effective server requirement.

### Custom values

46. Normal custom value equal assignments coalesce/suppress as documented.
47. Sequenced equal assignments are delivered as separate events in order.
48. Unauthorized direct custom-value change on a client is restored.
49. Deferred custom changes made during package processing flush with correct state/event semantics.

### Transport and robustness

50. Compression and fragmentation boundaries round-trip correctly.
51. Duplicate, missing, inconsistent, expired, and oversized fragments are rejected and cleaned.
52. Payload and entry limits reject explicitly without unbounded memory growth.
53. One failing subscriber or one failed entry does not crash synchronization processing for unrelated entries.
54. During initial connection, `PeerInfo`, `PlayerList`, and `AdminList` are released in a safe order; the initial player list is populated and `LocalPlayerIsAdminOrHost()` is correct without waiting for a later refresh.
55. Initial handshake buffering preserves package cursors, package contents, and the relative `VersionMatch` position for both normal completion and synchronization failure paths.
56. Successful server-side version receive logs include the same remote identifier later used in disconnect diagnostics.
57. Missing handshake, missing protocol field, explicit protocol mismatch, invalid version strings, client-too-old, and server-too-old cases produce distinct unconditional error messages.
58. Two overlapping client handshakes retain independent per-peer received state and cannot change each other's rejection reason.
59. A current rejected client receives one bounded structured disconnect report before `ErrorVersion`; report count, field lengths, reason codes, and trailing bytes are validated.
60. An older client without the report handler still disconnects normally and the server retains the detailed unconditional error log.
61. A current client preserves the pending report across failed `ZNet.Shutdown`, appends it once on the main menu, and clears it after display.
62. A new connection generation cannot display a report captured by the previous `ZRpc`.
63. A successful admission clears pending report, malformed-handshake, and unknown-consumer state.
64. A report older than the configured lifetime is ignored.
65. A Jotunn-, ServerSync-, or vanilla-originated `ErrorVersion` without an already-created CCS report does not cause CCS to append a speculative explanation.
66. Without Jotunn or ServerSync, the vanilla panel displays the CCS explanation and the button moves only by the final missing height.
67. With ServerSync, both diagnostics remain present and the deferred layout pass does not repeatedly move the button.
68. With Jotunn, the vanilla panel is hidden by Jotunn, only Jotunn's compatibility window remains, and its failed-connection area includes the CCS text.
69. With both Jotunn and ServerSync, Jotunn receives the final combined text and no second CCS modal is created.
70. Protocol 1 peers without the optional CCS package-version string remain compatible and are logged as `not reported`.
71. Early unreleased 1.0.4 plain-string disconnect reports are accepted by the current client fallback.
72. Malformed report format, excessive reason count, an oversized total package, and trailing data produce a bounded generic client explanation and an unconditional client error log; individually overlong display fields are normalized and truncated within the accepted package.

### Compatibility

73. Load an unchanged test consumer DLL compiled against the first public 1.x CCS API with the new core DLL; do not rebuild the consumer.
74. Compare the new public API against the retained 1.x baseline and investigate every removal or signature change.
75. Test a compatible older CCS client/server format whenever validation or package entry handling changes.

The 1.0.6 requirement-policy feature is additive. API compatibility review must specifically confirm that adding `ModRequirementMode` and the new property did not change the metadata identity or signatures of existing members. The unchanged old-consumer test must cover both fixed-required and fixed-optional consumers. Protocol-1 interoperability must be checked because Conditional consumers send the same existing version packet in an additional previously-optional case.

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
6. Run the permission, stale-UI, canonicalization, policy, lifecycle, and transport test matrix relevant to the change.
7. Build both assemblies against the intended Valheim/BepInEx references with no new warnings.
8. Verify the plugin package contains both runtime DLLs and the core XML documentation.
9. Verify dependent mod packages do not embed either CCS assembly.
10. Generate and verify SHA-256 hashes for both runtime DLLs.
11. Confirm README, CHANGELOG, PROJECT_CONTEXT, packaging documentation, and staged release copies are synchronized.
12. Scan repository text for accidental Cyrillic outside intentional localization resources.
13. Remove stale PDB/MDB files and stale binaries from package staging before creating release archives.
14. Test installation on a clean client and dedicated server profile.
15. Test CCS rejection UI with neither compatibility library, ServerSync only, Jotunn only, and both installed.
16. Confirm a rejected current client sees the structured report, while an older or missing CCS client still receives the vanilla version failure without destabilizing the connection flow.
17. Confirm a failed `ZNet` shutdown preserves the pending report and a successful connection or new attempt clears it.
18. Rebuild the project archive only after `PROJECT_CONTEXT.md` has been updated to match the final source.

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
- `ConfigSyncMode.cs`: config ownership mode contract.
- `ModRequirementMode.cs`: fixed/conditional remote-mod admission contract.
- `SyncedConfigEntry.cs`: config wrapper, local/server/accepted value state, typed API.
- `CustomSyncedValue.cs`: state and sequenced runtime value APIs.
- `ConfigurationManagerAttributes.cs`: UI interoperability tags.
- `GameReflection.cs`: validated runtime binding layer for non-public/publicized Valheim differences.
- `RuntimeGuard.cs`: standalone assembly enforcement and Harmony identity.
- `VersionCheck.cs`: peer admission, per-peer version/protocol diagnostics, structured disconnect reports, pending client-reason lifecycle, Jotunn/ServerSync-aware error-text injection, and vanilla-panel layout normalization.
- `SynchronizationEvents.cs`: public lifecycle/policy/rejection event arguments.
- `PluginInfoCCS.cs`: canonical package, plugin, repository, and protocol metadata.
- `PluginSelfInfo.cs`: retained compatibility metadata alias; do not remove while existing consumers may reference it.

### `Parts`

- `Runtime.cs`: one-time initialization, Harmony patch installation, main-thread queue, embedded-copy diagnostics.
- `ConfigState.cs`: write permissions, UI metadata, fallback serialization, rejected-write restoration.
- `Packages.cs`: entry serialization/deserialization, config state application, custom value application, package construction.
- `Transport.cs`: RPC handlers, client authorization, canonical broadcast, compression, fragmentation, queues, shutdown reset.
- `Policy.cs`: config ownership, hidden-setting, and mod-requirement policy files; record resolution; effective-state computation; reload/validation/dump/status diagnostics.
- `Stabilization.cs`: RPC registration, late registration, resync, protocol validation, session cleanup.
- `Diagnostics.cs`: debug config, events, rejection reporting, status and console diagnostics.

### Plugin project

- plugin entry point and lifecycle;
- package targets and publication scripts;
- Thunderstore and GitHub release staging.

Keep responsibilities separated. Do not grow one giant synchronization class file again merely because partial classes make cross-file access easy.

## Historical decisions that must not be accidentally reversed

- An optional consumer absent from the connected server is a local-only owner after successful admission, not a permanently read-only replica. Preserve the `InitialSyncDone` fail-closed gate for real remote synchronization and resolve absence through the explicit source-of-truth transition.
- Configuration Manager administrator-sensitive behavior should use CCS effective administrator state when it is operating on CCS-synchronized data; do not reintroduce a direct vanilla `LocalPlayerIsAdminOrHost()` dependency for its hidden-settings filter.

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
- Source code remains C# 11 with conventional explicit syntax.
- Core `AssemblyVersion` remains stable for compatible 1.x releases.
- CCS owns its rejection reason but appends to the existing connection-error text instead of owning a competing modal window.
- Jotunn's compatibility window and ServerSync's appended diagnostics must remain independently functional.
- Pending player-visible rejection data is connection-generation-bound, bounded, short-lived, and preserved across failed shutdown only long enough for display.
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
10. For admission failures, correlate the server and client logs by the CCS disconnect report ID when both sides use 1.0.4 or newer.
11. For a missing handshake, record the remote Steam ID/endpoint and compare repeated attempts by that identifier before calling the issue intermittent.
12. Request the rejected client's startup log from `Chainloader started` through `Chainloader startup complete`, including all `ConditionalConfigSync`, dependent-mod, `Exception`, and `Error` lines.
13. Do not claim that a missing consumer handshake proves the mod is absent; distinguish missing/disabled, pre-CCS build, CCS load failure, and duplicate/outdated DLL possibilities until the client startup log resolves them.

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
