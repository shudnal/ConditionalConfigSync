# 1.0.2
* exposed a structured policy-control availability state so configuration UIs can distinguish a missing compatible server session from missing administrator access
* renamed the public package metadata type from `PluginSelfInfo` to `PluginInfoCCS`
* added public read-only effective synchronization metadata for configuration UIs and diagnostics
* exposed normalized default ownership, effective ownership, policy initialization, hidden state, and effective ownership overrides
* effective ownership falls back to the normalized mod default until policy state is initialized
* added an administrator-authorized API for toggling individual Conditional settings between server and client ownership
* policy changes requested by compatible configuration UIs are persisted as minimal exact-setting rules in SyncPolicy.cfg
* fixed-mode settings and unauthorized clients cannot use the policy toggle API
* compatible servers advertise policy-control support through an optional extension of the existing lock-exemption entry
* no network protocol changes
* fixed stale configuration-manager permissions by failing closed during connection and re-evaluating read-only metadata after initial synchronization
* added a high-priority runtime guard that restores protected BepInEx config values before an unauthorized change notification can be processed
* removed local fallback presence from write authorization; effective ownership, initial sync, admin status, server lock, and explicit unlocked-update opt-in now determine writability
* changed `AllowClientConfigUpdatesWhenUnlocked` to a secure disabled-by-default opt-in for non-admin clients
* hardened server-side client update validation with ready-peer checks, per-entry ownership and permission checks, duplicate/unknown/trailing-data rejection, and all-or-nothing parsing before application
* client updates no longer send authoritative config state; matching legacy state claims remain accepted only as non-authoritative compatibility validation
* stopped forwarding original client packages and now redistribute canonical values and server-computed state to every client, including the initiator
* added rate-limited authoritative correction or full resynchronization after rejected client updates
* added explicit accepted/rejected client-update audit logs with sender and authorization state
* ensured an already registered locking entry is upgraded to protected always-server-controlled state
* stabilized the public core assembly identity at `1.0.0.0` for compatible 1.x releases while retaining current file and informational versions
* renamed the policy reload summary tag from `[Policy]` to `[SyncPolicy]`
* added comprehensive architecture, rationale, compatibility, security, test, and release context in `PROJECT_CONTEXT.md`

# 1.0.1
* renamed PluginInfo class to resolve conflict with BepInEx class with similar name

# 1.0.0

Initial release of Conditional Config Sync, a standalone shared dependency for Valheim mods.

## For players

- Provides synchronization infrastructure for mods that declare Conditional Config Sync as a dependency.
- Adds no gameplay content, items, UI, or standalone configuration of its own.
- Can run alongside Jotunn and mods that use ServerSync without replacing, modifying, or intercepting either library.
- Keeps synchronization fixes centralized, so dependent mods can receive runtime fixes by updating this package.

## For server administrators

- Provides three setting ownership modes: `AlwaysServerControlled`, `Conditional`, and `AlwaysClientControlled`.
- Supports exact-setting and whole-section ownership rules through `ConditionalConfigSync.SyncPolicy.cfg`.
- Supports exact-setting and whole-section visibility rules through `ConditionalConfigSync.HiddenConfigs.cfg`.
- Loads policy during server startup and applies stable file changes at runtime on the Unity main thread.
- Provides a protected locking setting with administrator exemption and non-admin write validation.
- Includes `status`, `policy_reload`, `policy_validate`, and `policy_dump` commands for policy operation and diagnostics.
- Synchronizes regular BepInEx configuration values and runtime custom values with bounded payloads, fragmentation support, and explicit rejection errors.
- Restores local client values and ownership state correctly when policy changes or the server connection ends.

## For mod authors

- Distributed as a standalone BepInEx hard dependency; reference `ConditionalConfigSync.dll` instead of embedding or ILRepacking a private copy.
- Provides familiar `ConfigSync`, `SyncedConfigEntry`, `CustomSyncedValue`, and `VersionCheck` APIs with XML IntelliSense documentation.
- Lets each mod explicitly require a compatible copy on the remote client or server through the documented `ModRequired` option.
- Supports one-call bind-and-register overloads that assign ownership before registration.
- Provides fixed and policy-controlled config modes for shared mechanics, client-local presentation, and administrator-selectable behavior.
- Provides state-like and sequenced custom values, priorities, equality comparers, explicit assignment modes, and `ISerializableParameter` support.
- Uses TOML conversion for regular BepInEx entries and length-prefixed package entries for safer parsing and forward handling.
- Provides lifecycle, policy, lock, connection-reset, rejection, and initial-sync events.
- Supports late registration, automatic resynchronization, and explicit `RequestFullSync()`.
- Isolates failures from individual `SettingChanged` handlers so remaining package entries can still be applied.
- Uses an independent numeric wire protocol and requires an exact client/server protocol match.
- Includes release SHA-256 hashes for both runtime DLL files.
