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
