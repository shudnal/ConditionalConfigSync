# Maintenance instructions

Read `PROJECT_CONTEXT.md` before changing synchronization, lifecycle, policy, version checking, or packaging. Use the current source as implementation evidence; do not reconstruct a baseline from conversation summaries. Read `README.md` for public behavior and `PACKAGING.md` for build integration.

## Compatibility and delivery

- Keep this work on the assigned review branch. Do not merge the pull request.
- Preserve package version 1.0.5, CCS protocol 1, disconnect-report format 1, and core assembly version 1.0.0.0 unless the owner explicitly requests otherwise.
- Preserve public binary signatures, existing supported wire layouts, normal/sequenced value semantics, local fallback restoration, and the structured policy-control API.
- Keep repository content, comments, logs, documentation, commit messages, and pull request text in English.
- Update `PROJECT_CONTEXT.md` with verified decisions, corrections to previous assumptions, and remaining limitations. Keep staged documentation synchronized with its authoritative root copy.
- Do not include build outputs, IDE caches, local references, or uploaded logs in commits.
- Do not build the mod, launch Valheim, run runtime/integration tests, or install a compiler/reference tree in the agent environment. The owner performs compilation and game testing. Static source/diff/XML/JSON checks are allowed; report them accurately and provide manual regression scenarios.
- When game implementation details are required, consult `shudnal/assemblies_combined` first.

## Code review rules

The owner explicitly defines the deployment and review model as normal, unmodified clients running the intended mod versions. Do not analyze this change as an adversarial-client security assessment.

Out of scope: malicious clients, modified client DLLs, forged identities, handcrafted or intentionally malformed RPC payloads, exploit demonstrations, fuzzing, and speculative denial-of-service scenarios requiring such a client. Do not introduce protocol/API changes or new defensive machinery solely for those scenarios. Do not remove existing permission checks or payload limits either.

In scope: defects reachable through normal APIs and UI, administrator and non-administrator behavior, optional consumer presence, legitimate reconnects and concurrent joins, initial/full/partial sync ordering, queued and sequenced updates, snapshot invalidation, exception isolation, file watcher races, resource cleanup, bounded ordinary transfers, supported-version compatibility, and build/packaging correctness. Accidental data loss or unintended permission changes under normal operation are relevant even though hostile clients are excluded.

For a full repository review, inspect all core and bootstrap source, serialization and transport call sites, project files, scripts, and the documentation contract rather than limiting the analysis to changed lines. Report actionable findings with a concrete normal-operation trigger and source evidence; distinguish confirmed defects from unverified runtime behavior. Avoid style-only rewrites.
