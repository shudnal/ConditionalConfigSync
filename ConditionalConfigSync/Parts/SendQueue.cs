using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;

namespace ConditionalConfigSync;

public partial class ConditionalConfigSync
{
    private sealed class SendSlot
    {
        internal readonly long Generation;
        internal readonly ZNet Session;
        internal readonly bool Sequenced;
        internal LinkedListNode<SendSlot>? Node;
        internal bool Waiting = true;
        internal bool Released;

        internal SendSlot(long generation, ZNet session, bool sequenced)
        {
            Generation = generation;
            Session = session;
            Sequenced = sequenced;
        }
    }

    private readonly LinkedList<SendSlot> sendQueue = new();
    private SendSlot? pendingStateSendSlot;
    private long transportGeneration;
    private int waitingSequencedSendCount;
    private bool initialSyncRepairRequested;
    private static int packagePreparationDepth;
    private static bool flushingAllPendingBroadcasts;

    private SendSlot? ReserveSendSlot(bool sequenced = false, LinkedListNode<SendSlot>? before = null)
    {
        if (!sessionActive || GameReflection.ZNetInstance is not { } session
            || before != null && before.List != sendQueue)
        {
            return null;
        }

        if (sequenced && waitingSequencedSendCount >= maxPendingSequencedUpdates)
        {
            RejectSync($"Rejected newest sequenced update: the send queue already contains {maxPendingSequencedUpdates} waiting events.", null, incoming: false);
            return null;
        }

        SendSlot slot = new(transportGeneration, session, sequenced);
        slot.Node = before == null ? sendQueue.AddLast(slot) : sendQueue.AddBefore(before, slot);
        if (sequenced)
        {
            ++waitingSequencedSendCount;
        }
        return slot;
    }

    private bool IsCurrentSend(SendSlot slot)
    {
        return !slot.Released && slot.Generation == transportGeneration && sessionActive
            && ReferenceEquals(GameReflection.ZNetInstance, slot.Session);
    }

    private void StartSendSlot(SendSlot slot)
    {
        if (slot.Waiting)
        {
            slot.Waiting = false;
            if (slot.Sequenced)
            {
                --waitingSequencedSendCount;
            }
        }
    }

    private void ReleaseSendSlot(SendSlot slot)
    {
        if (slot.Released)
        {
            return;
        }

        slot.Released = true;
        if (slot.Generation != transportGeneration)
        {
            return;
        }

        if (slot.Waiting && slot.Sequenced)
        {
            --waitingSequencedSendCount;
        }
        if (slot.Node?.List == sendQueue)
        {
            sendQueue.Remove(slot.Node);
        }
        FlushPendingBroadcastsForAllIfIdle();
    }

    private void QueuePendingConfigBroadcast(ConfigEntryBase config)
    {
        pendingStateSendSlot ??= ReserveSendSlot();
        if (pendingStateSendSlot != null)
        {
            pendingConfigBroadcasts.Add(config);
        }
    }

    private void QueuePendingCustomValueBroadcast(CustomSyncedValueBase value)
    {
        pendingStateSendSlot ??= ReserveSendSlot();
        if (pendingStateSendSlot != null)
        {
            pendingCustomValueBroadcasts.Add(value);
        }
    }

    private void ClearPendingBroadcasts()
    {
        SendSlot? stateSlot = pendingStateSendSlot;
        SendSlot[] sequencedSlots = pendingSequencedCustomValuePackages.Select(value => value.Reservation).ToArray();
        pendingStateSendSlot = null;
        pendingConfigBroadcasts.Clear();
        pendingCustomValueBroadcasts.Clear();
        pendingSequencedCustomValuePackages.Clear();

        // Clear the bookkeeping before releasing reservations: release can request another flush.
        if (stateSlot != null)
        {
            ReleaseSendSlot(stateSlot);
        }
        foreach (SendSlot slot in sequencedSlots)
        {
            ReleaseSendSlot(slot);
        }
    }

    private void FlushPendingStateBroadcastIfReady()
    {
        if (pendingStateSendSlot is not { } slot || sendQueue.First != slot.Node)
        {
            return;
        }

        pendingStateSendSlot = null;
        bool scheduled = false;
        SendSlot? fallbackBarrier = null;
        try
        {
            ConfigEntryBase[] configs = pendingConfigBroadcasts
                .Where(config => GetConfigData(config) is { } data && ShouldBroadcastConfigChange(data)).ToArray();
            CustomSyncedValueBase[] values = pendingCustomValueBroadcasts
                .OrderByDescending(value => value.Priority).ThenBy(value => value.RegistrationIndex).ToArray();
            pendingConfigBroadcasts.Clear();
            pendingCustomValueBroadcasts.Clear();
            if (!IsCurrentSend(slot) || configs.Length == 0 && values.Length == 0)
            {
                return;
            }

            // Keep state coalesced until its reserved turn. If a batch converter fails, its
            // healthy entries must still occupy this position, ahead of later events/snapshots.
            fallbackBarrier = ReserveSendSlot(before: slot.Node!.Next);
            if (fallbackBarrier == null)
            {
                return;
            }
            scheduled = StartBroadcastPackage(GameReflection.Everybody,
                () => ConfigsToPackage(configs: configs, customValues: values, includeConfigStates: isServer),
                reservation: slot);
            if (scheduled || !IsCurrentSend(fallbackBarrier))
            {
                return;
            }

            foreach (ConfigEntryBase config in configs)
            {
                SendSlot? replacement = ReserveSendSlot(before: fallbackBarrier.Node);
                if (replacement == null)
                {
                    break;
                }
                StartBroadcastPackage(GameReflection.Everybody,
                    () => ConfigsToPackage(configs: new[] { config }, includeConfigStates: isServer),
                    reservation: replacement);
            }
            foreach (CustomSyncedValueBase value in values)
            {
                SendSlot? replacement = ReserveSendSlot(before: fallbackBarrier.Node);
                if (replacement == null)
                {
                    break;
                }
                StartBroadcastPackage(GameReflection.Everybody,
                    () => ConfigsToPackage(customValues: new[] { value }), reservation: replacement);
            }
        }
        finally
        {
            if (!scheduled)
            {
                ReleaseSendSlot(slot);
            }
            if (fallbackBarrier != null)
            {
                ReleaseSendSlot(fallbackBarrier);
            }
        }
    }

    private void ResetTransportState()
    {
        ++transportGeneration;
        sendQueue.Clear();
        pendingStateSendSlot = null;
        waitingSequencedSendCount = 0;
        processingCount = 0;
        flushingPendingBroadcasts = false;
        initialSyncRepairRequested = false;
        lastHandledPackageWasFull = false;
        pendingConfigBroadcasts.Clear();
        pendingCustomValueBroadcasts.Clear();
        pendingSequencedCustomValuePackages.Clear();
        foreach (string cacheKey in configValueCache.Keys.ToArray())
        {
            RemoveFragmentAssembly(cacheKey);
        }
    }

    private static void FlushPendingBroadcastsForAllIfIdle()
    {
        if (ProcessingServerUpdate || packagePreparationDepth > 0 || flushingAllPendingBroadcasts || !sessionActive)
        {
            return;
        }

        flushingAllPendingBroadcasts = true;
        try
        {
            foreach (ConditionalConfigSync configSync in configSyncs.ToArray())
            {
                try
                {
                    configSync.FlushPendingBroadcastsIfIdle();
                }
                catch (Exception e)
                {
                    configSync.DebugWarning("Pending", $"Failed to flush deferred synchronization; continuing with other instances. Error: {e}");
                }
            }
        }
        finally
        {
            flushingAllPendingBroadcasts = false;
        }
    }
}
