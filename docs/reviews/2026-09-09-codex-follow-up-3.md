# PR #4: third Codex follow-up and PR #1 target transfer

## Scope

This pass addresses the three new Codex findings reported against `9c059ce09ecac248ebd1d6fe9970127a9dab0ea1` and carries forward the one independently verified build-integration target that remained useful in broken PR #1. The working branch remains `fix/full-repository-review-20260908` and the baseline before this pass is `edf461868f4d2b617e554782db5850e8672cec28` (the same source tree as `9c059ce` plus the completed branch-cleanup history).

The operating assumptions are unchanged: intended compatible mod clients, ordinary lifecycle and callbacks, no attacker/modified/malformed-client analysis. Package version 1.0.5, core AssemblyVersion 1.0.0.0 and wire protocol 1 remain unchanged. No changelog or public README is changed.

## 1. Polymorphic collection elements

Codex finding: `CustomSyncedValue<List<object>>` could serialize a runtime `string` while the receiver decoded the same bytes as declared `object`.

Correction: collection element serialization now uses the declared element encoding and rejects a non-null runtime element whose effective runtime type differs from the declared element type. Nullable value-type boxing is handled through the nullable underlying type. ISerializableParameter elements still require a receiver-constructible exact declared type. This is a validation correction only; no new marker or successful wire layout is introduced.

The same exact-runtime-type rule is applied to root ISerializableParameter values because Valheim's writer dispatches on the runtime object while the reader constructs the declared type.
The legacy ZRpc fallback for other reference types is checked the same way: a declared base/interface/object value cannot hide a different runtime encoding that the declared-type reader would not consume symmetrically.

## 2. Custom-value normalization before publication

Codex finding: the CCS ValueChanged subscriber was installed before consumer subscribers, so a server could publish a pre-normalized custom value before a later consumer callback clamped or otherwise canonicalized it.

Correction: CustomSyncedValueBase now treats publication as the final phase of a nested notification cascade. The CCS publisher remains the first registered delegate for compatibility, but RaiseValueChanged separates it from consumer callbacks and executes deferred publishers only after the outermost synchronous notification returns. Consumer exceptions remain isolated.

Pending latest-state publications coalesce at the value's last occurrence in the cascade. Sequenced notifications keep every publication occurrence in invocation order. Publishers read the settled active value, so a normalization callback cannot expose the earlier candidate. A nested change to another sequenced custom value is also ordered after the outer publication record rather than overtaking it.

## 3. Compatible type identities across consumer assembly versions

Codex finding: AssemblyQualifiedName contains assembly Version metadata, while CCS version admission explicitly allows compatible consumer releases whose CurrentVersion/MinimumRequiredVersion ranges overlap.

Correction: the existing AssemblyQualifiedName bytes remain on the wire. Read-side root and reflected-field checks first accept exact identity and then compare the same identity with only `Version=...` assembly tokens removed, including nested generic argument identities. Type name, assembly simple name, culture and public-key-token metadata remain part of the comparison. This therefore permits compatible assembly-version changes without changing protocol 1 or weakening unrelated type identity checks.

## 4. Plugin reference copy target from PR #1

PR #1 was rechecked only as a secondary source. Its runtime branch is not authoritative and was not merged or cherry-picked. The useful remaining item was `CopyConditionalConfigSyncPluginReference`: the core project already copies `ConditionalConfigSync.dll` and XML documentation to `ManagersAssembliesPath`, but the plugin project did not refresh `ConditionalConfigSync.Plugin.dll` there.

That target is now implemented directly in the current branch with a design-time-build guard, and PACKAGING.md documents the three manager/reference artifacts. No other PR #1 code is imported.

## Static verification performed

- Exact text replacements are assertion-guarded; the workflow aborts if the reviewed source shape differs.
- `git diff --check` is executed before commit.
- The changed-file list is checked to ensure no CHANGELOG file is touched.
- Plugin version 1.0.5 and protocol 1 are asserted from source.
- Edited repository text is scanned for accidental Cyrillic.
- No build, compilation, dependency restore, game/mod execution, fake harness or runtime test is performed.

## Maintainer validation scenarios (not executed here)

1. Assign a server-owned custom value whose ValueChanged handler clamps a candidate. Verify consumers and remote peers observe only the settled canonical value; repeat with nested changes to another sequenced custom value and with repeated equal sequenced notifications.
2. Round-trip exact-type primitive/enum/struct/collection/ISerializableParameter values. Confirm polymorphic `List<object>`-style values are rejected during outgoing preparation rather than producing a package the reader skips.
3. Run compatible consumer mod versions with different assembly Version metadata but unchanged synchronized type names/layouts. Confirm root custom values and reflected struct fields deserialize; confirm different type/assembly/token identities remain rejected.
4. Build the plugin in the maintainer environment and verify ManagersAssembliesPath receives current `ConditionalConfigSync.dll`, `ConditionalConfigSync.xml` and `ConditionalConfigSync.Plugin.dll` while design-time project loading does not perform the copy.
