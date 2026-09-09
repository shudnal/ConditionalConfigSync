from pathlib import Path


def replace_once(path, old, new):
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one replacement target, found {count}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8", newline="\n")


# 1. Preserve a payload snapshot for each sequenced notification while coalescing
# ordinary state at its first ordering position until the notification cascade settles.
custom_path = "ConditionalConfigSync/CustomSyncedValue.cs"
replace_once(
    custom_path,
    '''    private sealed class PendingPublication
    {
        internal readonly CustomSyncedValueBase Value;
        internal readonly Action Publisher;

        internal PendingPublication(CustomSyncedValueBase value, Action publisher)
        {
            Value = value;
            Publisher = publisher;
        }
    }

    [ThreadStatic]
    private static int notificationDepth;

    [ThreadStatic]
    private static List<PendingPublication>? pendingPublications;
''',
    '''    private sealed class PendingPublication
    {
        internal readonly CustomSyncedValueBase Value;
        internal readonly object? SequencedValueSnapshot;

        internal PendingPublication(CustomSyncedValueBase value, object? sequencedValueSnapshot)
        {
            Value = value;
            SequencedValueSnapshot = sequencedValueSnapshot;
        }
    }

    [ThreadStatic]
    private static int notificationDepth;

    [ThreadStatic]
    private static List<PendingPublication>? pendingPublications;

    [ThreadStatic]
    private static HashSet<CustomSyncedValueBase>? pendingStatePublications;
''')

old_raise = '''    private void RaiseValueChanged()
    {
        Action? handlers = ValueChanged;
        if (!hasBoxedValue || handlers == null)
        {
            return;
        }

        Delegate[] invocationList = handlers.GetInvocationList();
        if (invocationList.Length == 0)
        {
            return;
        }

        // AddCustomValue installs the CCS publisher before a derived constructor can attach consumer handlers.
        // Keep that internal callback out of the consumer phase so normalization/reentrant callbacks can settle
        // the active value first. Publication is flushed after the outermost nested notification returns.
        Action publisher = (Action)invocationList[0];
        (pendingPublications ??= new List<PendingPublication>()).Add(new PendingPublication(this, publisher));
        ++notificationDepth;
        try
        {
            for (int index = 1; index < invocationList.Length; ++index)
            {
                try
                {
                    ((Action)invocationList[index])();
                }
                catch (Exception e)
                {
                    owner.ReportCustomValueSubscriberFailure(Identifier, e);
                }
            }
        }
        finally
        {
            --notificationDepth;
            if (notificationDepth == 0)
            {
                FlushPendingPublications();
            }
        }
    }

    private static void FlushPendingPublications()
    {
        List<PendingPublication>? publications = pendingPublications;
        pendingPublications = null;
        if (publications == null || publications.Count == 0)
        {
            return;
        }

        // Latest-state values publish at their last occurrence in the notification cascade. Sequenced
        // values retain every notification in invocation order. Publishers read the now-canonical active
        // value, so normalization callbacks cannot expose a pre-normalized intermediate payload.
        Dictionary<CustomSyncedValueBase, int> lastStatePublication = new();
        for (int index = 0; index < publications.Count; ++index)
        {
            if (!publications[index].Value.PreserveUpdateSequence)
            {
                lastStatePublication[publications[index].Value] = index;
            }
        }

        for (int index = 0; index < publications.Count; ++index)
        {
            PendingPublication publication = publications[index];
            if (!publication.Value.PreserveUpdateSequence && lastStatePublication[publication.Value] != index)
            {
                continue;
            }

            try
            {
                publication.Publisher();
            }
            catch (Exception e)
            {
                publication.Value.owner.ReportCustomValueSubscriberFailure(publication.Value.Identifier, e);
            }
        }
    }
'''
new_raise = '''    private void RaiseValueChanged()
    {
        if (!hasBoxedValue)
        {
            return;
        }

        List<PendingPublication> publications = pendingPublications ??= new List<PendingPublication>();
        if (PreserveUpdateSequence)
        {
            // A sequenced value represents events, so every notification owns the payload that existed
            // when that notification started, even if a handler immediately assigns a follow-up event.
            publications.Add(new PendingPublication(this, boxedValue));
        }
        else if ((pendingStatePublications ??= new HashSet<CustomSyncedValueBase>()).Add(this))
        {
            // A state value keeps the ordering position of its first notification in this cascade but
            // publishes the settled active value after all synchronous normalization callbacks finish.
            publications.Add(new PendingPublication(this, null));
        }

        Action? handlers = ValueChanged;
        ++notificationDepth;
        try
        {
            if (handlers != null)
            {
                foreach (Action handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler();
                    }
                    catch (Exception e)
                    {
                        owner.ReportCustomValueSubscriberFailure(Identifier, e);
                    }
                }
            }
        }
        finally
        {
            --notificationDepth;
            if (notificationDepth == 0)
            {
                FlushPendingPublications();
            }
        }
    }

    private static void FlushPendingPublications()
    {
        List<PendingPublication>? publications = pendingPublications;
        pendingPublications = null;
        pendingStatePublications = null;
        if (publications == null || publications.Count == 0)
        {
            return;
        }

        foreach (PendingPublication publication in publications)
        {
            object? publicationValue = publication.Value.PreserveUpdateSequence
                ? publication.SequencedValueSnapshot
                : publication.Value.BoxedValue;
            try
            {
                publication.Value.owner.PublishCustomValueChange(publication.Value, publicationValue);
            }
            catch (Exception e)
            {
                publication.Value.owner.ReportCustomValuePublicationFailure(publication.Value.Identifier, e);
            }
        }
    }
'''
replace_once(custom_path, old_raise, new_raise)

# The event is now consumer-only; the base class calls the owner explicitly after the cascade.
ccs_path = "ConditionalConfigSync/ConditionalConfigSync.cs"
replace_once(
    ccs_path,
    '''        InvalidateFullSyncSnapshot($"registered custom value: {customValue.Identifier}");
        customValue.ValueChanged += () => OnCustomValueChanged(customValue);
        DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Register", $"Added custom value {customValue.Identifier}, type={customValue.Type.Name}, priority={customValue.Priority}, sequenced={customValue.PreserveUpdateSequence}");
''',
    '''        InvalidateFullSyncSnapshot($"registered custom value: {customValue.Identifier}");
        DebugLog(ConditionalConfigSyncDebugLevel.Trace, "Register", $"Added custom value {customValue.Identifier}, type={customValue.Type.Name}, priority={customValue.Priority}, sequenced={customValue.PreserveUpdateSequence}");
''')
replace_once(
    ccs_path,
    '''    internal void ReportCustomValueSubscriberFailure(string identifier, Exception exception)
    {
        DebugWarning("CustomValue", $"ValueChanged subscriber for '{identifier}' failed; continuing with the remaining subscribers. Error: {exception}");
    }
''',
    '''    internal void ReportCustomValueSubscriberFailure(string identifier, Exception exception)
    {
        DebugWarning("CustomValue", $"ValueChanged subscriber for '{identifier}' failed; continuing with the remaining subscribers. Error: {exception}");
    }

    internal void ReportCustomValuePublicationFailure(string identifier, Exception exception)
    {
        DebugWarning("CustomValue", $"Failed to publish custom value '{identifier}' after its notification callbacks completed. Error: {exception}");
    }
''')

# 2. Publish the value belonging to each sequenced occurrence instead of re-reading final BoxedValue.
transport_path = "ConditionalConfigSync/Parts/Transport.cs"
replace_once(
    transport_path,
    '''    private void OnCustomValueChanged(CustomSyncedValueBase customValue)
    {
        if (customValuesBeingApplied.Contains(customValue))
        {
            return;
        }

        if (IsSourceOfTruth)
        {
            customValue.StoreLastAcceptedValue(customValue.BoxedValue);
            if (isServer)
            {
                InvalidateFullSyncSnapshot($"custom value changed: {customValue.Identifier}");
            }
        }
''',
    '''    internal void PublishCustomValueChange(CustomSyncedValueBase customValue, object? publicationValue)
    {
        if (customValuesBeingApplied.Contains(customValue))
        {
            return;
        }

        if (IsSourceOfTruth)
        {
            customValue.StoreLastAcceptedValue(publicationValue);
            if (isServer)
            {
                InvalidateFullSyncSnapshot($"custom value changed: {customValue.Identifier}");
            }
        }
''')
replace_once(
    transport_path,
    '''        if (!IsSourceOfTruth)
        {
            customValue.StoreLastAcceptedValue(customValue.BoxedValue);
        }

        if (ShouldDeferOutgoingBroadcasts)
        {
            if (customValue.PreserveUpdateSequence)
            {
                if (TryEnqueueSequencedPackage(() => ConfigsToPackage(customValues: new[] { customValue }), customValue.Identifier))
                {
                    DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Queued sequenced {customValue.Identifier}, priority={customValue.Priority}");
                }
                FlushPendingBroadcastsForAllIfIdle();
            }
            else
            {
                QueuePendingCustomValueBroadcast(customValue);
                DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Queued latest-state {customValue.Identifier}, priority={customValue.Priority}");
            }
            return;
        }

        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Broadcast {customValue.Identifier}, priority={customValue.Priority}");
        StartBroadcastPackage(GameReflection.Everybody, () => ConfigsToPackage(customValues: new[] { customValue }), customValue.PreserveUpdateSequence);
    }
''',
    '''        if (!IsSourceOfTruth)
        {
            customValue.StoreLastAcceptedValue(publicationValue);
        }

        if (ShouldDeferOutgoingBroadcasts)
        {
            if (customValue.PreserveUpdateSequence)
            {
                if (TryEnqueueSequencedPackage(
                    () => ConfigsToPackage(packageEntries: new[] { PackageEntry.CustomValue(customValue, publicationValue) }),
                    customValue.Identifier))
                {
                    DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Queued sequenced {customValue.Identifier}, priority={customValue.Priority}");
                }
                FlushPendingBroadcastsForAllIfIdle();
            }
            else
            {
                QueuePendingCustomValueBroadcast(customValue);
                DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Queued latest-state {customValue.Identifier}, priority={customValue.Priority}");
            }
            return;
        }

        DebugLog(ConditionalConfigSyncDebugLevel.Verbose, "CustomValue", $"Broadcast {customValue.Identifier}, priority={customValue.Priority}");
        StartBroadcastPackage(
            GameReflection.Everybody,
            () => ConfigsToPackage(packageEntries: new[] { PackageEntry.CustomValue(customValue, publicationValue) }),
            customValue.PreserveUpdateSequence);
    }
''')

# 3. A local world has no network recipients. Treat that as a successful no-op before
# serialization/reservation. Also distinguish an iterator that completes synchronously from a
# genuine failure to start a Unity coroutine.
replace_once(
    transport_path,
    '''    private bool StartBroadcastPackage(long target, Func<ZPackage> createPackage, bool sequenced = false, SendSlot? reservation = null)
        => StartBroadcastPackageCore(createPackage, (package, slot) => SendZPackage(target, package, sequenced, slot), sequenced, reservation);

    private bool StartBroadcastPackage(List<ZNetPeer> peers, Func<ZPackage> createPackage)
        => StartBroadcastPackageCore(createPackage, (package, slot) => SendZPackage(peers, package, reservation: slot));
''',
    '''    private bool StartBroadcastPackage(long target, Func<ZPackage> createPackage, bool sequenced = false, SendSlot? reservation = null)
    {
        List<ZNetPeer> peers = GameReflection.GetRoutedPeers();
        if (target != GameReflection.Everybody)
        {
            peers = peers.Where(peer => GameReflection.GetPeerUid(peer) == target).ToList();
        }
        if (peers.Count == 0)
        {
            if (reservation != null)
            {
                ReleaseSendSlot(reservation);
            }
            return true;
        }

        return StartBroadcastPackageCore(
            createPackage,
            (package, slot) => SendZPackage(target, package, sequenced, slot),
            sequenced,
            reservation);
    }

    private bool StartBroadcastPackage(List<ZNetPeer> peers, Func<ZPackage> createPackage, bool sequenced = false, SendSlot? reservation = null)
    {
        if (peers.Count == 0)
        {
            if (reservation != null)
            {
                ReleaseSendSlot(reservation);
            }
            return true;
        }

        return StartBroadcastPackageCore(
            createPackage,
            (package, slot) => SendZPackage(peers, package, sequenced: sequenced, reservation: slot),
            sequenced,
            reservation);
    }
''')

old_start = '''            scheduled = GameReflection.StartCoroutine(createSender(package, slot), slot.Session) != null;
            if (!scheduled)
            {
                RejectSync("Could not start the synchronization sender for the active session.", null, incoming: false);
            }
            return scheduled;
'''
new_start = '''            IEnumerator sender = createSender(package, slot);
            bool hasPendingWork;
            try
            {
                hasPendingWork = sender.MoveNext();
            }
            catch
            {
                (sender as IDisposable)?.Dispose();
                throw;
            }

            if (!hasPendingWork)
            {
                // Unity may return null when an iterator finishes before its first yield. That is a
                // successful synchronous/no-recipient completion, not a failed sender startup.
                (sender as IDisposable)?.Dispose();
                scheduled = true;
                return true;
            }

            object? firstYield = sender.Current;
            IEnumerator ContinueSender(IEnumerator activeSender, object? initialYield)
            {
                try
                {
                    yield return initialYield;
                    while (activeSender.MoveNext())
                    {
                        yield return activeSender.Current;
                    }
                }
                finally
                {
                    (activeSender as IDisposable)?.Dispose();
                }
            }

            scheduled = GameReflection.StartCoroutine(ContinueSender(sender, firstYield), slot.Session) != null;
            if (!scheduled)
            {
                (sender as IDisposable)?.Dispose();
                RejectSync("Could not start the synchronization sender for the active session.", null, incoming: false);
            }
            return scheduled;
'''
replace_once(transport_path, old_start, new_start)

# 4. Reject collection declarations that use the custom count/element writer without a matching
# custom reader. Arrays and ICollection<T>-based declarations keep their existing symmetric paths.
serialization_path = "ConditionalConfigSync/Parts/CustomValueSerialization.cs"
replace_once(
    serialization_path,
    '''            if (!effectiveType.IsArray && collectionType != null)
            {
                // Validate the declared type, not just the runtime collection. The matching reader
                // must be able to create an assignable instance without changing the wire layout.
                GetCollectionImplementationType(effectiveType, collectionType);
            }
            else if (effectiveType.IsInterface && value is ICollection)
            {
                throw new NotSupportedException($"Collection interface '{effectiveType.FullName}' has no supported materialization. Declare an ICollection<T>-based type or implement ISerializableParameter.");
            }
''',
    '''            if (!effectiveType.IsArray && collectionType != null)
            {
                // Validate the declared type, not just the runtime collection. The matching reader
                // must be able to create an assignable instance without changing the wire layout.
                GetCollectionImplementationType(effectiveType, collectionType);
            }
            else if (!effectiveType.IsArray && value is ICollection)
            {
                throw new NotSupportedException(
                    $"Collection type '{effectiveType.FullName}' has no symmetric count/element reader in the current protocol. " +
                    "Declare an ICollection<T>-based type or implement ISerializableParameter.");
            }
''')

# 5. Do not rely on Dictionary enumeration order for the documented custom-value priority contract.
packages_path = "ConditionalConfigSync/Parts/Packages.cs"
replace_once(
    packages_path,
    '''        foreach (KeyValuePair<CustomSyncedValueBase, object?> configKv in configs.customValues)
        {
''',
    '''        foreach (KeyValuePair<CustomSyncedValueBase, object?> configKv in configs.customValues
                     .OrderByDescending(entry => entry.Key.Priority)
                     .ThenBy(entry => entry.Key.RegistrationIndex))
        {
''')
replace_once(
    packages_path,
    '''        public static PackageEntry ServerVersion(string version) => new() { kind = PackageEntryKind.ServerVersion, value = version };
        public static PackageEntry LockExempt(bool value, int? capabilities = null) => new() { kind = PackageEntryKind.LockExempt, value = value, capabilities = capabilities };
        public static PackageEntry ConfigState(ConfigEntryBase config, bool serverControlled, bool hidden) => new() { kind = PackageEntryKind.ConfigState, section = config.Definition.Section, key = config.Definition.Key, serverControlled = serverControlled, hidden = hidden };
''',
    '''        public static PackageEntry ServerVersion(string version) => new() { kind = PackageEntryKind.ServerVersion, value = version };
        public static PackageEntry LockExempt(bool value, int? capabilities = null) => new() { kind = PackageEntryKind.LockExempt, value = value, capabilities = capabilities };
        public static PackageEntry ConfigState(ConfigEntryBase config, bool serverControlled, bool hidden) => new() { kind = PackageEntryKind.ConfigState, section = config.Definition.Section, key = config.Definition.Key, serverControlled = serverControlled, hidden = hidden };
        public static PackageEntry CustomValue(CustomSyncedValueBase customValue, object? value) => new()
        {
            kind = PackageEntryKind.CustomValue,
            key = customValue.Identifier,
            type = customValue.Type,
            value = value,
        };
''')

report = '''# PR #4: local-world runtime correction and fourth Codex follow-up

## Scope

This pass starts from `510cb1be3aa9184a53922280efcfd1768fd5e4ff` on `fix/full-repository-review-20260908`. It addresses the maintainer-observed local-world warning `Could not start the synchronization sender for the active session.` and the three findings from Codex's review of that head.

Operating assumptions remain normal intended compatible clients. No attacker, modified/malformed-client, forged-RPC or speculative security-hardening analysis is included. Package version 1.0.5, protocol 1 and core AssemblyVersion 1.0.0.0 remain unchanged.

## Local-world sender warning

In a local world there can be zero routed network peers. CCS still reserved a send slot, serialized a package and started `SendZPackage`. That iterator could finish before its first yield, after which Unity's coroutine start result was treated as a failed sender and emitted a Reject warning for every synchronization instance.

Broadcast entry points now treat an empty target/peer set as a successful no-op and release an existing reservation without serializing. `StartBroadcastPackageCore` also advances the sender iterator once before handing remaining work to Unity: immediate completion is successful, while a sender that actually has asynchronous work is continued through a wrapper coroutine. A null coroutine result after a real first yield remains a genuine startup failure and keeps the existing Reject diagnostic.

This preserves ordered send-slot cleanup and also avoids useless serialization in the common local-world zero-peer case.

## Codex finding 1: sequenced payload snapshots

Every sequenced notification now stores the payload that existed when that notification started. Ordinary state values instead reserve one publication position at their first notification in a nested callback cascade and publish the settled final active value from that position.

The internal CCS publisher is no longer installed as a hidden `ValueChanged` subscriber. `CustomSyncedValueBase` invokes consumer subscribers with exception isolation, then calls the owning synchronization instance explicitly at the outermost completion boundary. Sequenced records therefore publish `1, 2` for a handler that reacts to `1` by assigning `2`, while an ordinary state normalization coalesces to its final value without moving behind nested events.

## Codex finding 2: unsupported non-generic collection encodings

Concrete/non-generic `ICollection` declarations such as `Queue<T>` or `ArrayList` no longer enter the custom count/element writer unless they are arrays or have the matching `ICollection<T>` materialization path. Unsupported collection declarations are rejected during outgoing serialization with an explicit diagnostic. Existing arrays, generic supported collections and the legacy `List<string>` path retain their successful protocol-1 layouts.

## Codex finding 3: priority order on receive

Parsed custom values are now applied with explicit `Priority` descending and `RegistrationIndex` ascending ordering. Receive callback order no longer depends on `Dictionary` enumeration behavior of the target Mono runtime.

## Static verification performed

The delivery script uses exact single-match assertions for every source transformation. The workflow runs `git diff --check`, asserts version/protocol constants, rejects any CHANGELOG modification and scans edited text for accidental Cyrillic before committing. Temporary delivery files are removed in the same final commit.

No build, compilation, dependency restore, Valheim/mod execution, fake harness or automated/runtime test is performed here.

## Maintainer validation scenarios (not executed here)

1. Start a local world with several CCS consumers and exercise ordinary config/custom synchronization. No `Could not start the synchronization sender for the active session.` warning should appear when there are zero network peers.
2. Join a multiplayer server/host and verify small immediate packages and fragmented/queued packages still release FIFO slots and arrive in order.
3. Assign sequenced value `1` with a handler that assigns `2`; remote observers should receive `1, 2`, including when an earlier fragmented send forces queuing.
4. Normalize an ordinary state value reentrantly and trigger a nested sequenced value. The state keeps its first ordering position but publishes only its settled value.
5. Synchronize supported arrays, dictionaries, lists and ICollection<T>-based interfaces. `Queue<T>`/`ArrayList`-style unsupported declarations should reject outgoing serialization rather than complete with unreadable remote state.
6. Use custom values with distinct priorities in one full package and verify higher-priority callbacks observe their dependencies already applied.
'''
Path("docs/reviews/2026-09-09-runtime-codex-follow-up-4.md").write_text(report, encoding="utf-8", newline="\n")
