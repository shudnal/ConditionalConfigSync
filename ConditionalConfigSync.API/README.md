# Conditional Config Sync API

This project is the optional-integration adapter for mods that should continue to work when Conditional Config Sync (CCS) is not installed. Full optional integration requires **CCS 1.0.10 or newer**.

It intentionally has **no compile-time reference to ConditionalConfigSync.dll**. It depends only on BepInEx and resolves the official reflection bridge from the already active CCS BepInEx plugin.

## Integration modes

Use either of these approaches:

1. Copy `ConditionalConfigSyncAPI.cs` into your mod project.
2. Reference `ConditionalConfigSync.API.dll` while developing and ILRepack/internalize it into your mod assembly.

Do **not** ship `ConditionalConfigSync.API.dll` as a separate runtime DLL. It is an embedding helper, not another shared dependency.

In both cases declare CCS as a soft BepInEx dependency on your plugin:

```csharp
[BepInDependency(
    ConditionalConfigSyncAPI.ConfigSync.PluginGuid,
    BepInDependency.DependencyFlags.SoftDependency)]
```

This gives BepInEx the correct startup ordering when CCS is installed while still allowing the mod to load when CCS is absent.

The minimum CCS version for this optional bridge is **1.0.10**. BepInEx 5 cannot combine a minimum version with `SoftDependency`: its versioned dependency constructor is always hard. The adapter therefore checks the installed CCS plugin version at runtime. CCS 1.0.5-1.0.9 are treated like CCS being absent and remain local-only.

## Behavior

The adapter always creates normal BepInEx `ConfigEntry<T>` objects.

- CCS absent: entries remain ordinary local BepInEx settings.
- CCS 1.0.5-1.0.9: entries remain ordinary local BepInEx settings; the optional bridge is not activated.
- CCS 1.0.10 or newer: the same entries are registered with CCS and participate in synchronization/policy.
- CCS 1.0.10+ with an unavailable or incompatible bridge: the adapter falls back to local BepInEx behavior and can log the reason through the supplied plugin logger.

Discovery never scans `AppDomain` for an assembly named `ConditionalConfigSync`. It resolves the plugin through the BepInEx GUID and reflects only the bridge from that active plugin assembly. Passive copies loaded by tools such as AzuAntiCheat therefore cannot be selected accidentally.

## Example

```csharp
using BepInEx;
using BepInEx.Configuration;
using ConditionalConfigSyncAPI;
using UnityEngine;

[BepInPlugin(PluginID, PluginName, PluginVersion)]
[BepInDependency(
    ConfigSync.PluginGuid,
    BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginID = "author.MyMod";
    public const string PluginName = "My Mod";
    public const string PluginVersion = "1.0.0";

    private ConfigEntry<bool> configLocked;
    private ConfigEntry<bool> useDurability;
    private ConfigEntry<KeyCode> snappingKey;

    private void Awake()
    {
        var sync = new ConfigSync(
            PluginID,
            PluginName,
            PluginVersion,
            minimumRequiredVersion: PluginVersion,
            modRequired: false,
            logger: Logger);

        configLocked = sync.BindLocking(
            Config,
            "General",
            "Lock configuration",
            true,
            "Server administrators control synchronized settings.");

        useDurability = sync.Bind(
            Config,
            "Gameplay",
            "Use durability",
            true,
            "Enable durability.",
            SyncMode.AlwaysServerControlled);

        snappingKey = sync.Bind(
            Config,
            "Controls",
            "Snapping key",
            KeyCode.LeftShift,
            "Local snapping key.",
            SyncMode.AlwaysClientControlled);
    }
}
```

The caller receives ordinary `ConfigEntry<T>` instances regardless of whether CCS is installed.

Existing entries can also be registered after binding:

```csharp
ConfigEntry<bool> useDurability = Config.Bind(
    "Gameplay",
    "Use durability",
    true,
    "Enable durability.");

sync.RegisterConfigEntry(
    useDurability,
    SyncMode.AlwaysServerControlled);
```

## ILRepack example

Reference `ConditionalConfigSync.API.dll` from the mod project and merge it into the final mod DLL:

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

The final distributed mod should contain the merged mod DLL only, not `ConditionalConfigSync.API.dll`.

## Important semantic limitation

A soft dependency makes synchronization optional **locally**.

For example, if a server runs MyMod + CCS but one client runs MyMod without CCS, that client has no CCS consumer and its entries remain local. `AlwaysServerControlled` does not by itself make CCS mandatory on that client.

Use a hard dependency when the mod requires synchronized behavior to function correctly. Use this optional adapter only when unsynchronized local fallback is an acceptable operating mode.
