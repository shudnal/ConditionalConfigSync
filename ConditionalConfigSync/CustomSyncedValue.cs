using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace ConditionalConfigSync;

/// <summary>
/// Non-generic base for runtime values synchronized independently from BepInEx config files.
/// </summary>
/// <remarks>
/// Use <see cref="CustomSyncedValue{T}"/> for state and <see cref="SequencedCustomSyncedValue{T}"/> for event-like
/// messages where repeated equal assignments must be preserved.
/// </remarks>
[Description("Base class for runtime values synchronized independently from BepInEx config files.")]
public abstract class CustomSyncedValueBase
{
    /// <summary>
    /// Raised after the active value is applied or explicitly re-notified.
    /// </summary>
    /// <remarks>
    /// ConditionalConfigSync also listens to this event to publish changes. Avoid assigning the same value recursively
    /// from the handler. Use <see cref="NotifyChanged"/> after mutating a reference-type value in place.
    /// </remarks>
    [Description("Raised when the active value is applied or explicitly re-notified.")]
    public event Action? ValueChanged;

    /// <summary>
    /// Compatibility alias for <see cref="NotifyChanged"/>. Re-publishes and re-processes the current active value.
    /// </summary>
    [Description("Compatibility alias for NotifyChanged. Re-processes and republishes the current value.")]
    public void Update() => ValueChanged?.Invoke();

    /// <summary>
    /// Explicitly notifies subscribers that the current value must be processed again.
    /// </summary>
    /// <remarks>
    /// This is useful after mutating an object in place, because assigning the same object reference can be suppressed
    /// by the equality comparer. Call it only from a side that is allowed to publish the current value.
    /// <example>
    /// <code>
    /// syncedList.Value.Add(newItem);
    /// syncedList.NotifyChanged();
    /// </code>
    /// </example>
    /// </remarks>
    [Description("Forces subscribers and synchronization to process the current value again.")]
    public void NotifyChanged() => ValueChanged?.Invoke();

    /// <summary>
    /// The local fallback retained by a client while a server value is active.
    /// </summary>
    /// <remarks>Prefer the typed assignment methods instead of modifying this field directly.</remarks>
    [Description("The local fallback retained while a server-owned custom value is active.")]
    public object? LocalBaseValue;
    internal bool HasLocalBaseValue;

    /// <summary>
    /// Unique identifier of this custom value within its owning synchronization instance.
    /// </summary>
    [Description("Unique stable identifier within the owning synchronization instance.")]
    public readonly string Identifier;

    /// <summary>
    /// Runtime type used for package serialization and validation.
    /// </summary>
    [Description("Runtime type used for package serialization and validation.")]
    public readonly Type Type;

    private object? boxedValue;
    private bool hasBoxedValue;

    /// <summary>
    /// Gets or sets the active value through the non-generic API.
    /// </summary>
    /// <remarks>
    /// Prefer <c>Value</c> and the typed <c>AssignLocalValue...</c> methods. Direct assignment here does not provide
    /// the local-fallback semantics that a connected client usually needs. Equal assignments are suppressed for a
    /// normal custom value and preserved for a sequenced custom value.
    /// </remarks>
    [Description("Non-generic active value. Prefer Value and AssignLocalValue methods in mod code.")]
    public object? BoxedValue
    {
        get => boxedValue;
        set => AssignBoxedValue(value, notifyIfEqual: PreserveUpdateSequence);
    }

    /// <summary>
    /// Assigns a boxed active value and reports whether an event was raised.
    /// </summary>
    /// <param name="value">The candidate active value.</param>
    /// <param name="notifyIfEqual">When true, notify even if the configured comparer reports equality.</param>
    /// <returns>True when the value was accepted and <see cref="ValueChanged"/> was raised.</returns>
    /// <remarks>Intended for custom derived value types. Most mods should use the typed public assignment methods.</remarks>
    protected bool AssignBoxedValue(object? value, bool notifyIfEqual)
    {
        if (hasBoxedValue && BoxedValuesEqual(boxedValue, value) && !notifyIfEqual)
        {
            return false;
        }

        boxedValue = value;
        hasBoxedValue = true;
        ValueChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// True when the local side currently owns and may publish this value.
    /// </summary>
    protected bool localIsOwner;

    /// <summary>
    /// Ordering priority used when several custom values are batched or flushed together. Higher values come first.
    /// </summary>
    /// <remarks>
    /// Values with the same priority keep their registration order. Priorities are useful when one value must be
    /// processed before another, for example loading environment definitions before selecting the current environment.
    /// They do not turn a state value into an event queue; use <see cref="SequencedCustomSyncedValue{T}"/> for that.
    /// </remarks>
    [Description("Batch ordering priority. Higher values are processed before lower values.")]
    public readonly int Priority;
    internal readonly long RegistrationIndex;
    internal virtual bool PreserveUpdateSequence => false;
    internal virtual bool BoxedValuesEqual(object? current, object? next) => Equals(current, next);

    private static long nextRegistrationIndex;

    /// <summary>Initializes a custom synchronized value base.</summary>
    /// <param name="configSync">The synchronization instance that owns the value.</param>
    /// <param name="identifier">Unique stable identifier within the synchronization instance.</param>
    /// <param name="type">Runtime serialization type.</param>
    /// <param name="priority">Batch ordering priority. Higher values come first.</param>
    protected CustomSyncedValueBase(ConditionalConfigSync configSync, string identifier, Type type, int priority)
    {
        Priority = priority;
        Identifier = identifier;
        Type = type;
        RegistrationIndex = ++nextRegistrationIndex;
        configSync.AddCustomValue(this);
        localIsOwner = configSync.IsSourceOfTruth;
        configSync.SourceOfTruthChanged += truth => localIsOwner = truth;
    }

    internal void StoreLocalBaseValue(object? value)
    {
        LocalBaseValue = value;
        HasLocalBaseValue = true;
    }

    internal void ClearLocalBaseValue()
    {
        LocalBaseValue = null;
        HasLocalBaseValue = false;
    }
}

/// <summary>
/// Synchronizes a runtime state value and suppresses assignments considered equal by its comparer.
/// </summary>
/// <typeparam name="T">The synchronized value type.</typeparam>
/// <remarks>
/// Use this type for current state such as JSON text, selected modes, generated settings, or cached metadata. If the
/// same state is assigned again, subscribers and the network are not notified unless an explicit notify method is used.
/// Handlers attached after construction do not receive the constructor's initial assignment. After subscribing, use
/// <see cref="AssignLocalValueAndNotify(T)"/> when derived runtime data must be built at least once.
/// Custom values use Valheim package serialization. Complex domain types can implement <c>ISerializableParameter</c>
/// when the built-in primitive, enum, collection, and value-type paths are not sufficient.
/// <example>
/// <code>
/// var mapData = new CustomSyncedValue&lt;string&gt;(configSync, "Map data", "");
/// mapData.ValueChanged += () =&gt; ApplyMapData(mapData.Value);
/// mapData.AssignLocalValueIfChanged(ReadMapData());
/// </code>
/// </example>
/// </remarks>
[Description("State-like synchronized value. Equal assignments are suppressed by the configured comparer.")]
public class CustomSyncedValue<T> : CustomSyncedValueBase
{
    private readonly IEqualityComparer<T> valueComparer;

    internal override bool BoxedValuesEqual(object? current, object? next)
    {
        if (ReferenceEquals(current, next))
        {
            return true;
        }

        if (current is null || next is null)
        {
            return false;
        }

        return valueComparer.Equals((T)current, (T)next);
    }

    /// <summary>
    /// Gets or sets the currently active synchronized value.
    /// </summary>
    /// <remarks>
    /// For a normal custom value, assigning an equal value is ignored. Prefer the <c>AssignLocalValue...</c> methods
    /// when code can run on a connected client, because they preserve the client's local fallback.
    /// </remarks>
    [Description("The currently active synchronized value. Equal assignments are suppressed for normal values.")]
    public T Value
    {
        get => (T)BoxedValue!;
        set => BoxedValue = value;
    }

    /// <summary>
    /// Creates a state-like custom synchronized value.
    /// </summary>
    /// <param name="configSync">The synchronization instance that owns this value.</param>
    /// <param name="identifier">A unique stable name within <paramref name="configSync"/>.</param>
    /// <param name="value">Initial local value.</param>
    /// <param name="priority">Batch ordering priority. Higher values are processed before lower values.</param>
    /// <param name="valueComparer">
    /// Optional equality comparer. It controls duplicate suppression for local assignments and received values. Supply a
    /// content comparer for arrays, lists, dictionaries, or domain objects when reference equality is not sufficient.
    /// </param>
    /// <example>
    /// <code>
    /// var settings = new CustomSyncedValue&lt;Dictionary&lt;int, string&gt;&gt;(
    ///     configSync, "Season settings", new Dictionary&lt;int, string&gt;(),
    ///     priority: 100, valueComparer: DictionaryContentComparer.Instance);
    /// </code>
    /// </example>
    [Description("Creates a state value. Use valueComparer for content equality of collections or domain objects.")]
    public CustomSyncedValue(ConditionalConfigSync configSync, string identifier, T value = default!, int priority = 0, IEqualityComparer<T>? valueComparer = null) : base(configSync, identifier, typeof(T), priority)
    {
        this.valueComparer = valueComparer ?? EqualityComparer<T>.Default;
        Value = value;
    }

    /// <summary>
    /// Assigns the local value using the default semantics of this custom-value type.
    /// </summary>
    /// <param name="value">The new local value.</param>
    /// <remarks>
    /// For <see cref="CustomSyncedValue{T}"/>, equal values are suppressed. For
    /// <see cref="SequencedCustomSyncedValue{T}"/>, every assignment is preserved, including repeated equal values.
    /// On a client that is not the source of truth, this changes only the local fallback and does not notify handlers.
    /// </remarks>
    [Description("Assigns using the type default: suppress duplicates for state values, preserve them for sequenced values.")]
    public void AssignLocalValue(T value)
    {
        if (localIsOwner)
        {
            Value = value;
        }
        else
        {
            StoreLocalBaseValue(value);
        }
    }

    /// <summary>
    /// Assigns the local value only when the configured comparer reports a real change.
    /// </summary>
    /// <param name="value">The candidate local value.</param>
    /// <remarks>
    /// This method suppresses equal values for both normal and sequenced custom values. Use it for polling, file
    /// watchers, or repeated recalculation where only changed state should trigger handlers and network traffic.
    /// <example>
    /// <code>
    /// settingsJson.AssignLocalValueIfChanged(File.ReadAllText(path));
    /// </code>
    /// </example>
    /// </remarks>
    [Description("Assigns only when the configured comparer reports a real change, even for sequenced values.")]
    public void AssignLocalValueIfChanged(T value)
    {
        if (localIsOwner)
        {
            AssignBoxedValue(value, notifyIfEqual: false);
        }
        else if (!HasLocalBaseValue || !BoxedValuesEqual(LocalBaseValue, value))
        {
            StoreLocalBaseValue(value);
        }
    }

    /// <summary>
    /// Assigns the local value and forces one notification even when the value compares equal.
    /// </summary>
    /// <param name="value">The value to assign and process.</param>
    /// <remarks>
    /// Use this for initial processing, explicit reload commands, or rebuilding derived data when handlers must run at
    /// least once. On a client that is not the source of truth, only the local fallback is updated; the active server
    /// value is not re-notified.
    /// <example>
    /// <code>
    /// // Initial load must build runtime objects even if the text equals the default value.
    /// settingsJson.AssignLocalValueAndNotify(ReadSettings());
    ///
    /// // Later reloads should run only after a real change.
    /// settingsJson.AssignLocalValueIfChanged(ReadSettings());
    /// </code>
    /// </example>
    /// </remarks>
    [Description("Assigns and forces one notification even when the value compares equal. Useful for initial processing.")]
    public void AssignLocalValueAndNotify(T value)
    {
        if (localIsOwner)
        {
            AssignBoxedValue(value, notifyIfEqual: true);
        }
        else
        {
            // The active value still belongs to the server. Only update the local fallback;
            // notifying here would make subscribers process the unchanged server value.
            StoreLocalBaseValue(value);
        }
    }
}

/// <summary>
/// Synchronizes an event-like sequence where every assignment must be delivered in order, including equal values.
/// </summary>
/// <typeparam name="T">The event payload type.</typeparam>
/// <remarks>
/// A normal <see cref="CustomSyncedValue{T}"/> represents the latest state and may coalesce pending updates. A sequenced
/// value snapshots every deferred assignment into its own package. Use it for commands, pulses, combat events, or any
/// message where <c>A, A</c> means two events rather than one state. Do not use it merely to force initial processing;
/// use <see cref="CustomSyncedValue{T}.AssignLocalValueAndNotify(T)"/> for that.
/// <example>
/// <code>
/// var playEffect = new SequencedCustomSyncedValue&lt;int&gt;(configSync, "Play effect");
/// playEffect.ValueChanged += () =&gt; SpawnEffect(playEffect.Value);
///
/// playEffect.AssignLocalValue(7);
/// playEffect.AssignLocalValue(7); // delivered as a second event
///
/// // This explicit method still suppresses a duplicate when that is desired.
/// playEffect.AssignLocalValueIfChanged(7);
/// </code>
/// </example>
/// </remarks>
[Description("Event-like synchronized value. Every assignment is preserved in order, including equal payloads.")]
public sealed class SequencedCustomSyncedValue<T> : CustomSyncedValue<T>
{
    internal override bool PreserveUpdateSequence => true;

    /// <summary>
    /// Creates an event-like custom synchronized value that preserves every assignment.
    /// </summary>
    /// <param name="configSync">The synchronization instance that owns this value.</param>
    /// <param name="identifier">A unique stable name within <paramref name="configSync"/>.</param>
    /// <param name="value">Initial local payload value.</param>
    /// <param name="priority">Batch ordering priority. Higher values are processed before lower values.</param>
    /// <param name="valueComparer">
    /// Optional comparer used only by explicit change-checking operations such as
    /// <see cref="CustomSyncedValue{T}.AssignLocalValueIfChanged(T)"/>. Normal sequenced assignment does not suppress equality.
    /// </param>
    [Description("Creates an event stream. Use for commands or pulses where repeated equal values are separate events.")]
    public SequencedCustomSyncedValue(ConditionalConfigSync configSync, string identifier, T value = default!, int priority = 0, IEqualityComparer<T>? valueComparer = null) : base(configSync, identifier, value, priority, valueComparer)
    {
    }
}
