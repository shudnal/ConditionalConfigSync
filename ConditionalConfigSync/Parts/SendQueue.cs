using System;
using System.Collections.Generic;
using System.Linq;

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
    private long transportGeneration;
    private int waitingSequencedSendCount;
    private bool initialSyncRepairRequested;
    private static int packagePreparationDepth;
    private static bool flushingAllPendingBroadcasts;

    private SendSlot? ReserveSendSlot(bool sequenced = false)
    {
        if (!sessionActive || GameReflection.ZNetInstance is not { } session)
        {
            return null;
        }

        if (sequenced && waitingSequencedSendCount >= maxPendingSequencedUpdates)
        {
            RejectSync($"Rejected newest sequenced update: the send queue already contains {maxPendingSequencedUpdates} waiting events.", null, incoming: false);
            return null;
        }

        SendSlot slot = new(transportGeneration, session, sequenced);
        slot.Node = sendQueue.AddLast(slot);
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

    private void ResetTransportState()
    {
        ++transportGeneration;
        sendQueue.Clear();
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
