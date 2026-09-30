using System;
using System.Reflection;

namespace ConditionalConfigSync;

internal static class RuntimeGuard
{
    internal const string StandaloneAssemblyName = "ConditionalConfigSync";
    internal const string PluginGuid = PluginInfoCCS.PluginGuid;
    internal const string HarmonyId = PluginGuid;

    // Shared through the AppDomain rather than a static field, because Assembly.LoadFile can create another
    // assembly with the same identity and its own independent copies of every CCS static field.
    private const string ActiveRuntimeKey = "_shudnal.ConditionalConfigSync.ActiveCoreAssembly";

    internal static bool IsStandaloneAssembly => string.Equals(
        typeof(RuntimeGuard).Assembly.GetName().Name,
        StandaloneAssemblyName,
        StringComparison.Ordinal);

    internal static void ClaimActiveRuntime()
    {
        ThrowIfEmbedded();
        AppDomain domain = AppDomain.CurrentDomain;
        Assembly current = typeof(RuntimeGuard).Assembly;
        lock (domain)
        {
            if (domain.GetData(ActiveRuntimeKey) is Assembly active && !ReferenceEquals(active, current))
            {
                throw new InvalidOperationException(
                    "A second ConditionalConfigSync core attempted to initialize while another core is already active. " +
                    $"Active: {DescribeAssembly(active)}. Attempted: {DescribeAssembly(current)}.");
            }

            domain.SetData(ActiveRuntimeKey, current);
        }
    }

    internal static void ThrowIfDifferentActiveRuntime()
    {
        Assembly current = typeof(RuntimeGuard).Assembly;
        if (AppDomain.CurrentDomain.GetData(ActiveRuntimeKey) is Assembly active && !ReferenceEquals(active, current))
        {
            throw new InvalidOperationException(
                "This mod resolved a second ConditionalConfigSync core instead of the active shared runtime. " +
                $"Active: {DescribeAssembly(active)}. Resolved: {DescribeAssembly(current)}. " +
                "Reference the standalone ConditionalConfigSync.dll normally and keep only one active CCS bootstrap.");
        }
    }

    internal static void ReleaseActiveRuntime()
    {
        AppDomain domain = AppDomain.CurrentDomain;
        Assembly current = typeof(RuntimeGuard).Assembly;
        lock (domain)
        {
            if (domain.GetData(ActiveRuntimeKey) is Assembly active && ReferenceEquals(active, current))
            {
                domain.SetData(ActiveRuntimeKey, null);
            }
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

    internal static void ThrowIfEmbedded()
    {
        if (IsStandaloneAssembly)
        {
            return;
        }

        Assembly assembly = typeof(RuntimeGuard).Assembly;
        throw new InvalidOperationException(
            $"An embedded copy of ConditionalConfigSync was detected inside assembly '{assembly.GetName().Name}'. " +
            "Embedded copies are not supported. Remove the embedded library, reference ConditionalConfigSync.dll normally, " +
            $"and add the BepInEx hard dependency '{PluginGuid}'.");
    }
}
