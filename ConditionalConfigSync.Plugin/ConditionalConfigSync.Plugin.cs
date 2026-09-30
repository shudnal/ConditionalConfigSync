using System;
using System.ComponentModel;
using System.Reflection;
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
    public const string PluginGuid = PluginInfoCCS.PluginGuid;
    /// <summary>Display name of the standalone BepInEx plugin.</summary>
    [Description("Display name of the standalone BepInEx plugin.")]
    public const string PluginName = PluginInfoCCS.PluginName;
    /// <summary>Current package version, sourced from <see cref="PluginInfoCCS.PluginVersion"/>.</summary>
    [Description("Package version sourced from PluginInfo.PluginVersion.")]
    public const string PluginVersion = PluginInfoCCS.PluginVersion;

    private const string PluginAssemblyName = "ConditionalConfigSync.Plugin";
    private const string CoreAssemblyName = "ConditionalConfigSync";
    private const string ActiveBootstrapKey = "_shudnal.ConditionalConfigSync.ActiveBootstrapInstance";

    private bool ownsBootstrap;

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

        if (!TryClaimBootstrap())
        {
            enabled = false;
            return;
        }

        try
        {
            global::ConditionalConfigSync.ConditionalConfigSync.InitializeRuntime();
        }
        catch
        {
            ReleaseBootstrap();
            enabled = false;
            throw;
        }
    }

    private bool TryClaimBootstrap()
    {
        AppDomain domain = AppDomain.CurrentDomain;
        lock (domain)
        {
            object? active = domain.GetData(ActiveBootstrapKey);
            if (active != null && !ReferenceEquals(active, this))
            {
                Assembly activeAssembly = active.GetType().Assembly;
                Assembly attemptedAssembly = typeof(ConditionalConfigSyncPlugin).Assembly;
                Logger.LogFatal(
                    "A second ConditionalConfigSync bootstrap attempted to initialize while another bootstrap is already active. " +
                    $"Active bootstrap: {DescribeAssembly(activeAssembly)}. Attempted bootstrap: {DescribeAssembly(attemptedAssembly)}.");
                return false;
            }

            domain.SetData(ActiveBootstrapKey, this);
            ownsBootstrap = true;
            return true;
        }
    }

    private static string DescribeAssembly(Assembly assembly)
    {
        string location;
        try
        {
            location = assembly.IsDynamic ? "<dynamic>" : assembly.Location;
        }
        catch
        {
            location = "<unknown location>";
        }

        return $"'{assembly.FullName}' at '{location}'";
    }

    private void ReleaseBootstrap()
    {
        if (!ownsBootstrap)
        {
            return;
        }

        AppDomain domain = AppDomain.CurrentDomain;
        lock (domain)
        {
            if (ReferenceEquals(domain.GetData(ActiveBootstrapKey), this))
            {
                domain.SetData(ActiveBootstrapKey, null);
            }
        }
        ownsBootstrap = false;
    }

    private void OnDestroy()
    {
        if (!ownsBootstrap)
        {
            return;
        }

        try
        {
            global::ConditionalConfigSync.ConditionalConfigSync.ShutdownRuntime();
        }
        finally
        {
            ReleaseBootstrap();
        }
    }
}
