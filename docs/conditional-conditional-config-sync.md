# *Conditional* Conditional Config Sync

*Conditional* Conditional Config Sync is the optional-integration path for mods that should work normally without Conditional Config Sync, but automatically gain CCS synchronization when CCS is installed in the current modpack.

Instead of making CCS a hard runtime dependency, the mod embeds the small `ConditionalConfigSync.API` adapter into its own project. The adapter always creates normal BepInEx `ConfigEntry<T>` values. If an active compatible CCS runtime is present, those same entries are registered with CCS; if CCS is absent, they simply remain ordinary local BepInEx configuration.

This is intended for mods where unsynchronized local configuration is still a valid operating mode.

## What gets embedded

The optional adapter is available in two equivalent forms:

- copy `ConditionalConfigSync.API/ConditionalConfigSyncAPI.cs` directly into the mod project; or
- reference `ConditionalConfigSync.API.dll` during development and ILRepack/internalize it into the final mod DLL.

The API DLL is a developer artifact. Do **not** distribute `ConditionalConfigSync.API.dll` as another standalone runtime dependency.

The embedded adapter has no compile-time reference to `ConditionalConfigSync.dll` or `ConditionalConfigSync.Plugin.dll`. It references only BepInEx.

## Add CCS as a soft dependency

Add the normal BepInEx soft dependency to the consuming plugin:

```csharp
[BepInDependency(
    ConditionalConfigSyncAPI.ConfigSync.PluginGuid,
    BepInDependency.DependencyFlags.SoftDependency)]
```

When CCS is installed, this gives BepInEx the correct load ordering. When CCS is not installed, the consumer mod still loads normally.

## Create one optional sync context

Create one adapter instance for the mod:

```csharp
var configSync = new ConditionalConfigSyncAPI.ConfigSync(
    PluginID,
    PluginName,
    PluginVersion,
    minimumRequiredVersion: PluginVersion,
    modRequired: false,
    logger: Logger);
```

The rest of the mod can continue to use ordinary `ConfigEntry<T>` fields.

## Bind configuration transparently

Instead of calling `Config.Bind(...)` directly, call `configSync.Bind(...)`.

Server-controlled example:

```csharp
useDurability = configSync.Bind(
    Config,
    "Gameplay",
    "Use durability",
    true,
    "Enable durability.",
    ConditionalConfigSyncAPI.SyncMode.AlwaysServerControlled);
```

Client-controlled example:

```csharp
enableSnappingKey = configSync.Bind(
    Config,
    "Controls",
    "Enable snapping key",
    new KeyboardShortcut(KeyCode.LeftShift),
    "Key used for snapping.",
    ConditionalConfigSyncAPI.SyncMode.AlwaysClientControlled);
```

Protected locking entry:

```csharp
configLocked = configSync.BindLocking(
    Config,
    "General",
    "Lock Configuration",
    true,
    "Configuration is locked and can be changed by server admins only.");
```

Every call still returns a normal BepInEx `ConfigEntry<T>`.

## Existing Config.Bind calls can stay

Existing mods do not need to rewrite all binding helpers immediately.

An already-bound entry can be registered afterwards:

```csharp
ConfigEntry<bool> useDurability = Config.Bind(
    "Gameplay",
    "Use durability",
    true,
    "Enable durability.");

configSync.RegisterConfigEntry(
    useDurability,
    ConditionalConfigSyncAPI.SyncMode.AlwaysServerControlled);
```

If CCS is absent, `RegisterConfigEntry` simply returns `false` and the entry remains local-only.

This makes it easy to migrate an existing helper such as `serverConfig(...)` by changing only the helper implementation instead of every call site.

## Runtime behavior

| Local installation | Result |
| --- | --- |
| Consumer mod without CCS | Normal BepInEx configuration only |
| Consumer mod with compatible CCS | Same `ConfigEntry<T>` values are registered and synchronized through CCS |
| Consumer mod with incompatible/older CCS bridge | Local BepInEx fallback; optional warning through the supplied logger |
| CCS DLL loaded passively by another tool, but no active CCS BepInEx plugin | Ignored |

The adapter does not scan `AppDomain` for assemblies named `ConditionalConfigSync`. It resolves the actual active BepInEx plugin by GUID and reflects only the official `SoftDependencyBridge` exposed by that instantiated plugin assembly. Passive copies loaded by tools such as AzuAntiCheat cannot be selected accidentally.

## Source-file integration

For the simplest integration, copy:

```text
ConditionalConfigSync.API/ConditionalConfigSyncAPI.cs
```

into the mod project and compile normally.

No CCS assembly reference is required.

## ILRepack integration

Alternatively, build/reference `ConditionalConfigSync.API.dll` while developing and merge it into the final mod assembly:

```xml
<Target Name="ILRepacker" AfterTargets="Build">
  <ItemGroup>
    <InputAssemblies Include="$(TargetPath)" />
    <InputAssemblies Include="$(OutputPath)ConditionalConfigSync.API.dll" />
  </ItemGroup>

  <ILRepack
    Parallel="true"
    Internalize="true"
    InputAssemblies="@(InputAssemblies)"
    OutputFile="$(TargetPath)"
    TargetKind="SameAsPrimaryAssembly"
    LibraryPath="$(OutputPath)" />
</Target>
```

Ship only the final merged mod DLL.

## When to use this

This optional path is appropriate when the mod's behavior is still valid with local configuration only, for example:

- client-side quality-of-life mods;
- UI/input preferences;
- mods where server synchronization is an optional convenience rather than a correctness requirement;
- existing mods whose authors do not want CCS to become a mandatory installation.

Use the normal CCS hard dependency instead when peer disagreement in configuration would break gameplay, shared state, world generation, networking, or authoritative calculations.

## Important limitation

A soft dependency makes synchronization optional on each installation.

For example:

```text
Server:
    MyMod + CCS

Client:
    MyMod without CCS
```

The client has no CCS consumer and continues to use its local BepInEx values. `AlwaysServerControlled` cannot force synchronization onto an installation where CCS itself is absent.

If that situation is not acceptable for the mod, use CCS as a hard dependency instead.

## API details

The embedded adapter reflects only a small stable bridge owned by CCS:

- bridge API version;
- runtime-ready state;
- create one consumer context;
- register an existing config entry;
- register a protected locking entry.

CCS-specific enum values cross the reflection boundary by stable names instead of numeric values. Internal CCS fields, properties, constructors, overload discovery, runtime ownership and duplicate-runtime guards remain implementation details of CCS itself.

For the adapter source and buildable DLL project, see [ConditionalConfigSync.API](../ConditionalConfigSync.API/).
