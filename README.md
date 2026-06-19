# Conditional Config Sync

Conditional Config Sync is a shared infrastructure library for Valheim mods. It does not add gameplay content, items, UI, or configuration options of its own. Install it when another mod lists it as a dependency.

The package provides centralized config synchronization, version checks, server-side policy overrides, protected locking, and synchronized runtime values. Keeping this logic in one standalone dependency means fixes can be shipped by updating this package instead of rebuilding every mod that uses it.

It is an independent synchronization option for mod authors who need per-setting ownership policy, hidden-setting controls, and expanded runtime-value behavior. Jotunn and ServerSync remain separate libraries with their own use cases and development paths.

## For players

Normally, a mod manager installs Conditional Config Sync automatically as a dependency. Keep it installed while any enabled mod depends on it.

This package:

- does **not** replace, modify, or intercept `ServerSync` used by other mods;
- does **not** migrate or modify configuration belonging to other mods;
- can run alongside Jotunn and mods that use `ServerSync`;
- only handles mods that were explicitly built against Conditional Config Sync.

Installing this package alone has no gameplay effect.

## Package contents

The Thunderstore package contains:

- `ConditionalConfigSync.Plugin.dll` - the BepInEx bootstrap registered as `_shudnal.ConditionalConfigSync`;
- `ConditionalConfigSync.dll` - the public API and synchronization runtime used by dependent mods;
- `ConditionalConfigSync.xml` - IntelliSense documentation for IDEs and mod authors;
- `SHA256SUMS.txt` - SHA-256 hashes of both release DLL files.

Both DLL files must remain installed together. The XML and documentation files are optional at runtime but useful for development, verification, and redistribution.

## Compatibility

Conditional Config Sync uses its own BepInEx and Harmony identifier:

```text
_shudnal.ConditionalConfigSync
```

It does not patch, replace, or disable Jotunn synchronization or `ServerSync`. These libraries can coexist in the same process because each mod continues using the synchronization library it was compiled against.

Embedding or ILRepacking `ConditionalConfigSync.dll` into another mod is intentionally unsupported. An embedded copy rejects initialization and reports an explicit error. Use the standalone Thunderstore dependency instead.

## For server administrators

### Default behavior

Every registered setting has one of three ownership modes selected by its mod author:

- `AlwaysServerControlled` - always synchronized from the server and cannot be released by policy. Mod authors should use it for shared mechanics and synchronized state that must use the same effective value on the server and every client. Typical examples are gameplay rules, world-state generation, shared events, network-visible calculations, and feature switches that would malfunction when peers disagree;
- `Conditional` - uses the mod-defined server/client default and may be overridden by server policy. It is appropriate when an administrator may reasonably choose between one shared server value and per-client behavior;
- `AlwaysClientControlled` - always local to each client and cannot be forced by sync policy. It is intended for presentation, local UI, controls, and other behavior that does not participate in shared mechanics.

A mod may also register one locking setting that controls whether ordinary clients may publish changes to server-controlled settings. Hidden-state policy is independent and may hide settings from compatible configuration managers in any ownership mode.

Policy files are created in `BepInEx/config/shudnal.ConditionalConfigSync` on the server. Existing files are read synchronously during server startup before the file watchers are used. Reads are retried until the file metadata is stable, so common editor save patterns such as truncate/write/rename do not replace a working policy with a partial snapshot. The files are then watched for changes and can be edited while the server is running.
### Locking behavior

Conditional Config Sync provides a locking model familiar to ServerSync users and adds explicit protection for the locking setting itself.

When the lock is enabled:

- ordinary clients cannot publish changes to server-controlled settings;
- server administrators receive an exemption and may edit them;
- client-controlled settings remain local and editable.

When the lock is disabled, ordinary clients may publish changes to server-controlled settings if the mod author enabled unlocked-client updates.

The locking setting is always protected:

- it is always server-controlled;
- it cannot be forced to client-controlled through policy;
- a non-admin client cannot change it even while the rest of the configuration is unlocked.

This prevents a client from attempting to grant itself permission by changing the lock setting first.

### Sync policy

File:

```text
BepInEx/config/shudnal.ConditionalConfigSync/ConditionalConfigSync.SyncPolicy.cfg
```

A rule may target one exact setting or an entire config section:

```text
ModGuid.Section.Key
ModGuid.Section
```

Use `+` to force server ownership and `-` to force client ownership:

```ini
# Exact setting
+ author.mod.General.Damage multiplier
- author.mod.Interface.Show status panel

# Every Conditional setting in a section
+ author.mod.Gameplay
- author.mod.Interface
```

Rules:

- `+ identifier` forces matching `Conditional` settings to be server-controlled;
- `- identifier` forces matching `Conditional` settings to be client-controlled;
- an exact setting rule takes precedence over a whole-section rule;
- no matching entry preserves the mod author's default;
- `AlwaysServerControlled` and `AlwaysClientControlled` ignore sync-policy ownership overrides;
- malformed, duplicate, conflicting, unknown, and ignored entries can be reported by `conditionalconfigsync_policy_validate`;
- forcing the protected locking setting to client-controlled is ignored.

Policy is edited only through these files. Console commands intentionally provide reload, validation, status, and dump operations but do not add, remove, or rewrite rules. File editing keeps the complete policy visible, reviewable, and easy to revert.

Typical uses:

- enforce an otherwise client-local gameplay option on a public server;
- release a cosmetic or UI setting from server control;
- switch a complete section without listing every setting;
- override one setting differently from the rest of its section;
- temporarily test a different ownership model without rebuilding the mod.

### Hidden settings

File:

```text
BepInEx/config/shudnal.ConditionalConfigSync/ConditionalConfigSync.HiddenConfigs.cfg
```

Add either an exact setting identifier or a whole-section identifier per line:

```ini
# Exact settings
author.mod.Advanced.Internal multiplier
author.mod.Debug.Enable verbose output

# Every setting in a section
author.mod.Advanced
```

An exact or section match marks the setting as not browsable in compatible configuration managers. Hiding is a presentation policy, not a security boundary: the setting still exists, and its synchronization behavior is controlled separately by `SyncPolicy.cfg`, its `ConfigSyncMode`, and the lock state.

Typical uses:

- hide advanced or dangerous options from ordinary configuration UI;
- simplify a public server's visible settings;
- hide a whole internal section with one line;
- keep diagnostic options available in the config file without advertising them in the manager.

### Debug logging

Local debug settings are stored in:

```text
BepInEx/config/shudnal.ConditionalConfigSync/ConditionalConfigSync.Debug.cfg
```

Console commands for diagnostics:

```text
conditionalconfigsync_debug
conditionalconfigsync_debug_server
```

Server policy commands:

```text
conditionalconfigsync_status
conditionalconfigsync_policy_reload
conditionalconfigsync_policy_validate
conditionalconfigsync_policy_dump
```

- `status` shows registered mods, mode counts, effective server-controlled settings, hidden settings, and policy-file paths;
- `policy_reload` reads and applies both policy files immediately on the Unity main thread;
- `policy_validate` reports syntax errors, duplicates, conflicts, unknown identifiers, and rules ignored by fixed modes;
- `policy_dump` writes `ConditionalConfigSync.PolicyDump.txt` with copy-ready exact and section identifiers. Each setting is followed only by its policy mode and default ownership; the variable type is intentionally omitted.

Debug logging supports `Basic`, `Verbose`, and `Trace` levels and an optional mod-name filter. Normal startup stays quiet; warnings and errors remain visible without debug logging.

## For mod authors

Conditional Config Sync is a third-party standalone dependency. Do not embed it with ILRepack and do not ship private copies inside individual mods.

Reference only:

```text
ConditionalConfigSync.dll
```

Then declare the standalone BepInEx plugin as a hard dependency:

```csharp
[BepInDependency(
    "_shudnal.ConditionalConfigSync",
    BepInDependency.DependencyFlags.HardDependency)]
```

Use the namespace:

```csharp
using ConditionalConfigSync;
```

The public API keeps familiar `ConfigSync` names also used by ServerSync:

```csharp
internal static readonly ConfigSync configSync = new ConfigSync(pluginID)
{
    DisplayName = pluginName,
    CurrentVersion = pluginVersion,
    MinimumRequiredVersion = pluginVersion,
    ModRequired = true
};
```

### Requiring the mod on the remote side

`ModRequired` controls whether the **owning mod** must also be installed and compatible on the remote peer. This is separate from the BepInEx hard dependency on Conditional Config Sync itself:

- the hard dependency requires CCS on the same machine where the owning mod is installed;
- `ModRequired = true` requires a compatible copy of the owning mod on the other side of the connection.

Set it before a connection is established, preferably in the `ConfigSync` object initializer:

```csharp
internal static readonly ConfigSync configSync = new ConfigSync(pluginID)
{
    DisplayName = pluginName,
    CurrentVersion = pluginVersion,
    MinimumRequiredVersion = pluginVersion,
    ModRequired = true
};
```

The behavior is symmetric:

| Local side running the mod | `ModRequired` | Remote side without the mod | Result |
|---|---:|---|---|
| Client | `true` | Server | Connection is rejected on the client |
| Server | `true` | Client | The server rejects that client |
| Client or server | `false` | Remote peer | Connection is allowed and this mod instance is not synchronized with the missing remote copy |

Use `true` for server-authoritative, world-state, gameplay, or other two-sided mods. A mod such as Seasons, which synchronizes the current season, day, settings, and runtime state, must require its remote copy. Leave the default `false` only for a genuinely client-only mod or an optional integration that remains correct when the other side does not have it.

When the remote copy exists, `CurrentVersion`, `MinimumRequiredVersion`, and the CCS wire protocol are used for compatibility checks. Late registration or `RequestFullSync()` does not repeat connection admission, so configure `ModRequired` while creating the `ConfigSync` instance.

Register an existing BepInEx entry with its mode in one call:

```csharp
ConfigEntry<float> damage = Config.Bind(
    "Gameplay",
    "Damage multiplier",
    1f,
    "Server gameplay setting");

configSync.AddConfigEntry(
    damage,
    ConfigSyncMode.AlwaysServerControlled);
```

Use `AlwaysServerControlled` whenever the setting controls a common mechanic whose result must remain consistent across the server and all clients. Do not leave such a setting `Conditional` merely to make it configurable through policy: allowing one client to restore a local value can make shared calculations, events, state transitions, or network-visible behavior diverge.

Or bind and register in one call:

```csharp
SyncedConfigEntry<bool> showPanel = configSync.AddConfigEntry(
    Config,
    "Interface",
    "Show status panel",
    true,
    new ConfigDescription("Local UI setting"),
    ConfigSyncMode.AlwaysClientControlled);
```

For policy-controlled settings, specify the default ownership without assigning a second property after registration:

```csharp
SyncedConfigEntry<float> scale = configSync.AddConfigEntry(
    Config,
    "Gameplay",
    "World scale",
    1f,
    new ConfigDescription("May be reassigned through SyncPolicy"),
    ConfigSyncMode.Conditional,
    serverControlledByDefault: true);
```

Compatibility overloads remain available:

```csharp
configSync.AddConfigEntry(entry);        // Conditional, server-controlled by default
configSync.AddConfigEntry(entry, false); // Conditional, client-controlled by default
```

Passing the mode/default directly prevents a temporary server-controlled registration state and avoids duplicate policy-state transitions during initialization.

Runtime values are also available:

```csharp
public static readonly CustomSyncedValue<string> mapData =
    new(configSync, "Map data", "");
```

Use `CustomSyncedValue<T>` for state. Equal assignments are suppressed by its comparer. Use `SequencedCustomSyncedValue<T>` for event-like data where repeated equal payloads must still be delivered in order.

The XML file shipped beside the DLL documents the public API, assignment modes, comparers, priorities, version checks, and integration details directly in IDE IntelliSense.

### Protocol compatibility

Conditional Config Sync uses a numeric wire protocol version that is independent from the DLL and package version.

The first public release uses protocol `1`. Clients and servers must use the same protocol version. The protocol number is increased only when an incompatible network-format change is introduced; ordinary library updates do not require a protocol bump while the wire contract remains compatible.

### Late registration and resynchronization

Configs and custom values registered after the network session starts are handled automatically:

- the server batches newly registered values and broadcasts their current state;
- a client that registers values after `InitialSyncCompleted` requests a complete resync from the server;
- multiple registrations in the same frame are coalesced into one operation.

A `ConfigSync` instance created after the normal connection startup window also registers its RPC handlers and requests its first complete package automatically. A mod that rebuilds a dynamic registration set can call `configSync.RequestFullSync()` explicitly. The method returns `false` when no remote server is connected.

Connection admission and mod-version validation still happen during Valheim's normal peer handshake. Mods that require version enforcement should create their `ConfigSync` instance before connecting; late resync updates data but does not retroactively repeat the completed connection check.

### Lifecycle and diagnostic events

Each `ConfigSync` instance exposes optional events for mods that need to rebuild derived runtime state or provide their own diagnostics:

- `InitialSyncCompleted` after the first complete server package has been applied on a client;
- `ServerConnectionReset` after server values are cleared and local fallback values are restored;
- `PolicyStateChanged`, `ServerControlledChanged`, and `HiddenStateChanged` when effective policy changes;
- `LockStateChanged` when the effective lock or administrator exemption changes;
- `SyncRejected` when a package or outgoing update is rejected for permissions, malformed data, queue overflow, or the 20 MiB safety limit.

Subscriber exceptions are isolated and logged, so one consumer cannot interrupt synchronization for the remaining mods or entries. Full usage notes are included in the XML documentation.

### Why a standalone dependency

Compared with embedding a sync source file or ILRepacking a private DLL:

- all dependent mods use one maintained implementation;
- fixes to networking, policy, serialization, or security require updating only this package;
- duplicate Harmony patches, watchers, commands, and static registries are avoided;
- server administrators get one common policy layer for all participating mods;
- dependent mod packages remain smaller and simpler.

For Thunderstore, add this package to the mod's dependencies. Do not copy either Conditional Config Sync DLL into the dependent mod's own package.

## Functional differences from ServerSync

### Distribution and isolation

- Delivered as a standalone BepInEx hard dependency instead of being embedded with ILRepack.
- Uses a dedicated namespace, BepInEx GUID, Harmony ID, RPC names, and package format.
- Does not modify or intercept ServerSync instances used by other mods.
- Detects and rejects unsupported embedded Conditional Config Sync copies.

### Config ownership and policy

- Retains separate local and active server values on clients.
- Supports runtime switching between server-controlled and client-controlled state.
- Adds three explicit config modes: `AlwaysServerControlled`, `Conditional`, and `AlwaysClientControlled`.
- Adds server-side `SyncPolicy.cfg` overrides for exact settings and complete sections.
- Adds exact-setting and section-level `HiddenConfigs.cfg` rules for Configuration Manager visibility.
- Sends effective config state together with server values.

### Locking and administration

- Keeps the lock and administrator exemption model familiar from ServerSync.
- Protects the locking setting itself even while the configuration is unlocked.
- Rejects non-admin network attempts to change the protected lock.
- Uses the connected peer identity for current Valheim admin checks and clearer logs.

### Custom synchronized values

- Distinguishes state-like `CustomSyncedValue<T>` from event-like `SequencedCustomSyncedValue<T>`.
- Supports explicit `AssignLocalValue`, `AssignLocalValueIfChanged`, and `AssignLocalValueAndNotify` behavior.
- Supports custom equality comparers for collections and domain types.
- Supports priorities and preserves ordering for sequenced values.
- Defers outgoing updates safely instead of dropping changes during active synchronization.

### Serialization and networking

- Regular `ConfigEntry` values use BepInEx TOML conversion based on the local setting type.
- Custom values retain typed Valheim package serialization and support `ISerializableParameter`.
- Uses length-prefixed entries so unknown package entries can be skipped safely.
- Adds compression, fragmentation, queue limits, payload limits, and clearer failure logging.
- Adds an independent numeric protocol version with exact client/server matching.
- Supports automatic late registration and explicit complete resynchronization.

### Diagnostics and documentation

- Adds stable/retried policy reads so partial editor writes do not replace the active policy.
- Cleans up policy watchers, network caches, pending queues, registered session handlers, and Harmony ownership when the relevant session or bootstrap ends.
- Adds per-mod receive logs with config/custom-value names for single-value updates.
- Includes optional filtered debug logging and server policy status/reload/validate/dump commands.
- Ships XML IntelliSense documentation and metadata descriptions for important public API members.

## Migration note

Using Conditional Config Sync in a mod selects its synchronization implementation and protocol for that mod. Server and clients should update the dependent mod and this dependency together. Other installed mods that use ServerSync are unaffected.

## License and acknowledgements

Original Conditional Config Sync contributions are released under the [Unlicense](https://github.com/shudnal/ConditionalConfigSync/blob/main/LICENSE). The project uses the ServerSync approach and adapted code as a foundation; applicable ServerSync portions remain covered by its MIT-0 terms. Some implementation and compatibility ideas were informed by Jotunn. The retained notices are available in [THIRD_PARTY_NOTICES.md](https://github.com/shudnal/ConditionalConfigSync/blob/main/THIRD_PARTY_NOTICES.md).

## Security and privacy

Conditional Config Sync:

- contains no telemetry and sends no data to external services;
- makes no HTTP requests and opens no network connections outside the active Valheim client/server session;
- does not download, update, or execute external programs;
- does not access the Windows registry or ship native libraries;
- communicates only through Valheim RPC connections between the current server and its connected clients;
- writes only its policy, debug, dump, and diagnostic files under `BepInEx/config/shudnal.ConditionalConfigSync`;
- uses reflection only to access Valheim runtime members whose accessibility differs from publicized development assemblies;
- publishes unobfuscated binaries, XML API documentation, source code, and SHA-256 hashes for both DLL files.

## Links

- [Source repository](https://github.com/shudnal/ConditionalConfigSync)
- [Buy Me a Coffee](https://buymeacoffee.com/shudnal)
- [Discord](https://discord.gg/e3UtQB8GFK)
