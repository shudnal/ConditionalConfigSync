using System;
using System.Reflection;

namespace ConditionalConfigSync;

internal static class RuntimeGuard
{
    internal const string StandaloneAssemblyName = "ConditionalConfigSync";
    internal const string PluginGuid = PluginSelfInfo.PluginGuid;
    internal const string HarmonyId = PluginGuid;

    internal static bool IsStandaloneAssembly => string.Equals(
        typeof(RuntimeGuard).Assembly.GetName().Name,
        StandaloneAssemblyName,
        StringComparison.Ordinal);

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
            "and add the BepInEx hard dependency '{PluginInfo.PluginGuid}'.");
    }
}
