# PR #4: local-world runtime correction and fourth Codex follow-up

## Scope

This pass starts from `510cb1be3aa9184a53922280efcfd1768fd5e4ff` on `fix/full-repository-review-20260908`. The source corrections are committed in `cf81fa9ea0dbf32d16427536ec49abc60e5db67d`; subsequent commits only refine this review record. The pass addresses the maintainer-observed local-world warning `Could not start the synchronization sender for the active session.` and the three findings from Codex's review of `510cb1b`.

Operating assumptions remain normal intended compatible clients. No attacker, modified/malformed-client, forged-RPC or speculative security-hardening analysis is included. Package version 1.0.5, protocol 1 and core AssemblyVersion 1.0.0.0 remain unchanged.

## Local-world sender warning

In a local world there can be zero routed network peers. CCS still reserved a send slot, serialized a package and started `SendZPackage`. The resulting sender could finish before yielding any asynchronous work, while `StartBroadcastPackageCore` treated a null coroutine handle as a failed sender startup and emitted a Reject warning for every synchronization instance.

Broadcast entry points now treat an empty target/peer set as a successful no-op and release an existing reservation without serializing. `StartBroadcastPackageCore` also advances the sender iterator once before handing remaining work to Unity: immediate completion is successful, while a sender that actually has asynchronous work is continued through a wrapper coroutine. A null coroutine result after a real first yield remains a genuine startup failure and keeps the existing Reject diagnostic.

This preserves ordered send-slot cleanup and also avoids useless serialization in the common local-world zero-peer case.

## Codex finding 1: sequenced payload snapshots

Every sequenced notification now stores the payload reference/value that existed when that notification started. Ordinary state values instead reserve one publication position at their first notification in a nested callback cascade and publish the settled final active value from that position.

The internal CCS publisher is no longer installed as a hidden `ValueChanged` subscriber. `ValueChanged` contains only consumer subscriptions; `CustomSyncedValueBase` invokes those subscribers with exception isolation and then calls the owning synchronization instance explicitly at the outermost completion boundary. Sequenced value assignments such as `1` followed reentrantly by `2` therefore publish `1, 2`, while an ordinary state normalization coalesces to its final value without moving behind nested events.

For mutable reference-type sequenced payloads the recorded object is the assigned reference, not a deep clone. The protocol/API does not define a general deep-copy mechanism; mods requiring immutable event snapshots should use immutable/value payloads or their own serialized snapshot type. No new cloning behavior is introduced in this maintenance pass.

## Codex finding 2: unsupported non-generic collection encodings

Concrete/non-generic `ICollection` declarations such as `Queue<T>` or `ArrayList` no longer enter the custom count/element writer unless they are arrays or have the matching `ICollection<T>` materialization path. Unsupported collection declarations are rejected during outgoing serialization with an explicit diagnostic. Existing arrays, generic supported collections and the legacy `List<string>` path retain their successful protocol-1 layouts.

## Codex finding 3: priority order on receive

Parsed custom values are now applied with explicit `Priority` descending and `RegistrationIndex` ascending ordering. Receive callback order no longer depends on `Dictionary` enumeration behavior of the target Mono runtime.

## Static verification performed

The delivery script used exact single-match assertions for every source transformation. The workflow ran `git diff --check`, asserted version/protocol constants, rejected any CHANGELOG modification and scanned edited text for accidental Cyrillic before committing. Temporary delivery files were removed in the final source commit.

No build, compilation, dependency restore, Valheim/mod execution, fake harness or automated/runtime test was performed here.

## Maintainer validation scenarios (not executed here)

1. Start a local world with several CCS consumers and exercise ordinary config/custom synchronization. No `Could not start the synchronization sender for the active session.` warning should appear when there are zero network peers.
2. Join a multiplayer server/host and verify small immediate packages and fragmented/queued packages still release FIFO slots and arrive in order.
3. Assign sequenced value `1` with a handler that assigns `2`; remote observers should receive `1, 2`, including when an earlier fragmented send forces queuing.
4. Normalize an ordinary state value reentrantly and trigger a nested sequenced value. The state keeps its first ordering position but publishes only its settled value.
5. Synchronize supported arrays, dictionaries, lists and ICollection<T>-based interfaces. `Queue<T>`/`ArrayList`-style unsupported declarations should reject outgoing serialization rather than complete with unreadable remote state.
6. Use custom values with distinct priorities in one full package and verify higher-priority callbacks observe their dependencies already applied.
