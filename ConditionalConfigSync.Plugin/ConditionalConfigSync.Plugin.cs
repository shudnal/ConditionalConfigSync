using System;
using System.ComponentModel;
using BepInEx;

namespace ConditionalConfigSync;

/// <summary>
/// BepInEx bootstrap for the standalone ConditionalConfigSync package.
/// </summary>
/// <remarks>
/// Dependent mods reference ConditionalConfigSync.dll for the public API and declare this plugin GUID as a hard
/// dependency. They do not need a compile-time reference to ConditionalConfigSync.Plugin.dll.
/// </remarks>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[Description("Standalone BepInEx bootstrap that initializes ConditionalConfigSync.dll.")]
public sealed class ConditionalConfigSyncPlugin : BaseUnityPlugin
{
    /// <summary>BepInEx dependency identifier used by dependent mods.</summary>
    [Description("BepInEx hard-dependency identifier used by mods that consume ConditionalConfigSync.")]
    public const string PluginGuid = PluginSelfInfo.PluginGuid;
    /// <summary>Display name of the standalone BepInEx plugin.</summary>
    [Description("Display name of the standalone BepInEx plugin.")]
    public const string PluginName = PluginSelfInfo.PluginName;
    /// <summary>Current package version, sourced from <see cref="PluginSelfInfo.PluginVersion"/>.</summary>
    [Description("Package version sourced from PluginInfo.PluginVersion.")]
    public const string PluginVersion = PluginSelfInfo.PluginVersion;

    private const string PluginAssemblyName = "ConditionalConfigSync.Plugin";
    private const string CoreAssemblyName = "ConditionalConfigSync";

    private void Awake()
    {
        string pluginAssemblyName = typeof(ConditionalConfigSyncPlugin).Assembly.GetName().Name ?? string.Empty;
        if (!string.Equals(pluginAssemblyName, PluginAssemblyName, StringComparison.Ordinal))
        {
            Logger.LogFatal(
                $"Embedded ConditionalConfigSync bootstrap detected inside assembly '{pluginAssemblyName}'. " +
                "The bootstrap plugin must only run from ConditionalConfigSync.Plugin.dll.");
            enabled = false;
            return;
        }

        string coreAssemblyName = typeof(global::ConditionalConfigSync.ConditionalConfigSync).Assembly.GetName().Name ?? string.Empty;
        if (!string.Equals(coreAssemblyName, CoreAssemblyName, StringComparison.Ordinal))
        {
            Logger.LogFatal(
                $"ConditionalConfigSync core was resolved from unsupported assembly '{coreAssemblyName}'. " +
                "Remove embedded copies and install the standalone ConditionalConfigSync mod.");
            enabled = false;
            return;
        }

        global::ConditionalConfigSync.ConditionalConfigSync.InitializeRuntime();
    }

    private void OnDestroy()
    {
        global::ConditionalConfigSync.ConditionalConfigSync.ShutdownRuntime();
    }
}
