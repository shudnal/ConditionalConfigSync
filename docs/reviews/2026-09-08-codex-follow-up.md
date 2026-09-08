# Codex review follow-up: PR #4

## Scope and baseline

This follow-up addresses all six inline findings in Codex review of commit `e3004ffc8d0250ea1d41ef2583712d5b178f0c0c` in PR #4. Work remains on `fix/full-repository-review-20260908`. The original repository-wide review is recorded in `2026-09-08-repository-review.md`.

The operating model is unchanged: peers run the intended compatible mod implementation. Findings here describe ordinary lifecycle, callback, serialization, and transport behavior, not hostile/modified/malformed-client scenarios. No package/assembly version, protocol, public API, CHANGELOG, manifest, build definition, or packaging script is changed by this follow-up.

## Confirmed findings and corrections

### 1. Deferred state must precede a later sequenced event

Review comment: https://github.com/shudnal/ConditionalConfigSync/pull/4#discussion_r3960712457

Trigger: while a large update is sending, a config or latest-state custom value changes, followed by a sequenced custom event. Previously the event was transferred to the send FIFO before pending state, leaving the receiver's event handler with stale state.

Correction: the first deferred state update now reserves its position in the shared send FIFO immediately. Config and latest-state custom updates coalesce in that reservation until it reaches the head. Deferred sequenced events also reserve their final FIFO positions before serialization, retaining those reservations when the captured packages are scheduled. Queue limits count each waiting event once. Batch serialization fallback uses an ordering barrier so individually serialized healthy entries cannot fall behind later events. Session reset and optional-server fallback release or invalidate abandoned reservations.

This retains the documented latest-state semantics: pending ordinary state may coalesce; it is not converted into an event history. Sequenced values continue to retain each accepted assignment. The correction guarantees that earlier pending state is not deferred until after a later event merely because a send is active.

Affected implementation: `Parts/SendQueue.cs`, `Parts/Transport.cs`, and the reset entry point in `Parts/ConfigState.cs`.

### 2. Late custom-value construction must not publish the initial local value

Review comment: https://github.com/shudnal/ConditionalConfigSync/pull/4#discussion_r3960712463

Trigger: an administrator or authorized unlocked client constructs a custom value after initial synchronization, or a server constructs a late sequenced value. The initial `Value` assignment previously notified the synchronization subscriber before the scheduled late-registration operation.

Correction: the generic constructor uses an internal non-notifying initialization path. It establishes the active boxed value, accepted value, and replica fallback without invoking `ValueChanged`. The existing deferred registration operation publishes the server registration or requests the client's canonical snapshot. Both normal and sequenced constructors use this path; post-construction assignments keep their existing semantics.

Affected implementation: `CustomSyncedValue.cs`. Correction commit: `61e5dff8342aa4a9e00e0173014eb74b5358fbf6`.

### 3. Interface-declared collections need a constructible receiver type

Review comment: https://github.com/shudnal/ConditionalConfigSync/pull/4#discussion_r3960712473

Trigger: a value declared as `ICollection<int>` contains a `List<int>`. Encoding succeeded, but the receiver attempted to instantiate the interface and skipped the custom value.

Correction: supported `ICollection<T>`-based interfaces resolve to an assignable `List<T>`, `HashSet<T>`, or `Dictionary<TKey, TValue>` as appropriate. The declared collection's constructibility is validated on the writer too, so unsupported abstract/interface declarations fail before producing a collection payload the matching reader cannot instantiate. Existing concrete collection, array, dictionary, and `List<string>` encodings remain unchanged. No null-element marker or wire-layout extension is introduced.

Affected implementation: `Parts/CustomValueSerialization.cs`. Correction commit: `0b88bce99f407458ca2384db1bd523c6b741df8a`.

### 4. Policy restoration callbacks must retain local assignments

Review comment: https://github.com/shudnal/ConditionalConfigSync/pull/4#discussion_r3960712479

Trigger: server policy returns a setting to client ownership; restoring its fallback invokes a `SettingChanged` callback that calls `AssignLocalValue`. Previously the presence of the old fallback redirected that assignment into a field cleared immediately after the callback.

Correction: `ShouldStoreLocalConfigValue` uses current ownership, not the presence of an old fallback. Once the policy is client-controlled, the callback writes the active local value. The existing package-application order establishes policy before invoking the callback.

Affected implementation: `Parts/ConfigState.cs`. Correction commit: `295b604c804e1d44d4cfaeac3766835cfd9f0e42`.

### 5. Disconnect and optional-server restoration need local ownership during callbacks

Review comment: https://github.com/shudnal/ConditionalConfigSync/pull/4#discussion_r3960712487

Trigger: fallback restoration invokes config/custom-value callbacks before ownership has returned to the local side. Callback assignments were treated as replica fallbacks and then erased.

Correction: a full local reset updates the internal source-of-truth state and custom-value ownership before applying fallbacks. Config policy defaults are established before value callbacks. Each restored fallback is detached before assignment, so a replacement established by a callback is not cleared afterward. A failed assignment retains the old fallback unless another one was established. The public `SourceOfTruthChanged` notification remains after restoration; `ServerConnectionReset` remains after network cleanup. Replacement full snapshots do not switch the instance to local ownership.

Affected implementation: `Parts/ConfigState.cs`, shared by shutdown and optional-server fallback. Correction commit: `295b604c804e1d44d4cfaeac3766835cfd9f0e42`.

### 6. Administrator edit permission must not overwrite local persisted fallbacks

Review comment: https://github.com/shudnal/ConditionalConfigSync/pull/4#discussion_r3960712496

Trigger: a writable administrator or authorized unlocked client receives an update and `ConfigFile.Save` runs. The original serialization prefix used writability as a reason to persist the active server value into the client's cfg file.

Correction: an existing fallback is serialized for every server-controlled replica, independent of edit permission. Server/local-source values and client-controlled settings retain their ordinary save behavior. Permission to edit a remote canonical value does not make that value the client's persisted local setting.

Affected implementation: `Parts/ConfigState.cs`, covering `Parts/Packages.cs` saves as well as other `ConfigFile.Save` callers. Correction commit: `295b604c804e1d44d4cfaeac3766835cfd9f0e42`.

## Source review and verification boundaries

Inspected the six findings against their full methods and related source paths: custom construction/registration, typed local assignment, config application and persistence, source-of-truth and lifecycle events, shutdown, optional-server admission, pending queues, sequenced serialization, full snapshot sending, and session cleanup. Reviewed the changed-file diffs for unintended edits. This follow-up is targeted source inspection, not a claim that the external repository-wide audit has completed.

No build, compilation, dependency restore, mod execution, automated test, fake game harness, or runtime test was performed. The following are maintainer validation scenarios, not claimed passing tests:

- Hold a fragmented send active; change a config and a latest-state custom value, then emit repeated equal sequenced events. Check receiver ordering and continued state coalescing under backpressure.
- Include a failing config converter in a deferred state batch with healthy values, then queue an event. Confirm healthy fallback sends retain their FIFO position and no reservation remains stuck.
- Reset/disconnect with pending state/events; reconnect and verify the new session is not blocked by old reservations. Exercise optional-server local restoration separately.
- Construct late normal and sequenced values on an admin client, an authorized unlocked client, and the server. Confirm construction itself does not overwrite server state or duplicate the late registration event.
- Round-trip interface-declared `ICollection<int>`, `IList<string>`, `ISet<int>`, and `IDictionary<int, string>` through the ordinary custom-value path. Confirm existing concrete collections keep their established format.
- In a policy fallback callback and in disconnect fallback callbacks, call `AssignLocalValue` conditionally with a distinct value. Confirm the final active value is retained and final lifecycle notifications see restored state.
- Give a client local and server values that differ. Receive partial/full updates as an administrator and as an allowed unlocked client; save, disconnect, and restart. Confirm the client's original/local fallback persists.

The next Codex pass must re-check these corrections and inspect the whole repository tree, including unchanged files and cross-file contracts, under the same operating assumptions and no-build/no-test restrictions.
