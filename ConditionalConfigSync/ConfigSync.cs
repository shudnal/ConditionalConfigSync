using System.ComponentModel;

namespace ConditionalConfigSync;

/// <summary>
/// Compatibility name for code originally written against ServerSync's <c>ConfigSync</c> class.
/// </summary>
/// <remarks>
/// It has the same behavior as <see cref="ConditionalConfigSync"/>. New code may use either name; keeping this alias
/// allows existing declarations to migrate by changing only the namespace import.
/// </remarks>
[Description("Compatibility alias for code originally written against ServerSync ConfigSync.")]
public class ConfigSync : ConditionalConfigSync
{
    /// <summary>Creates a compatibility synchronization instance for one mod.</summary>
    /// <param name="name">Stable unique identifier, normally the mod's BepInEx GUID.</param>
    [Description("Creates a compatibility synchronization instance. Pass a stable mod GUID as the name.")]
    public ConfigSync(string name) : base(name)
    {
    }
}
