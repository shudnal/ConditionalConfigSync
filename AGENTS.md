# Repository guidance

## Current review work

- Use `fix/full-repository-review-20260908` for this review and its follow-up corrections. It starts from `master` at `d0dcaa36fd6bc19e424cf2b092a7b926102f0fef`.
- Do not use PR #1 or its head branch as a baseline. That branch is broken. Its individual observations may be checked against the current source only after an independent review.
- Do not change package versions, assembly versions, the wire protocol, or any CHANGELOG copy. Do not add new public features as part of the review.
- Keep repository content, comments, diagnostics, documentation, and commit/PR text in English.
- Do not build, run, or test the Valheim mod in the assistant environment. Source inspection and static repository checks are allowed; runtime validation belongs to the maintainer's game environment.
- Read `PROJECT_CONTEXT.md` for design constraints. Source code is authoritative for the actual implementation.
- When game implementation details are needed, first read `shudnal/assemblies_combined`.

## Review guidelines

Review the entire repository, including unchanged files and cross-file interactions, not merely the pull request diff. Cover the core library, plugin bootstrap, project/build/packaging definitions, and documentation contracts. State which areas were examined and which checks were not performed.

Assume clients run the intended, compatible version of the mod. Do not frame findings around hostile clients, modified clients, deliberately malformed packets, forged RPCs, adversarial deserialization, or speculative security hardening. Existing guards should remain intact, but changing the threat model is outside this task.

Focus on reproducible defects in ordinary operation: connection/session lifecycle, complete and partial synchronization, ordering and fragmentation of valid updates, local fallback restoration, policy transitions, administrator changes, late registration, snapshot invalidation, supported serialization, file reloads, and consumer callbacks.

For every finding, give the normal trigger, affected code, user-visible consequence, and a concrete correction. Distinguish confirmed defects from assumptions. Preserve existing successful wire encodings and public API contracts. Do not report intentional limitations as new feature requests.

Report confirmed defects in unchanged code too. If the review interface cannot attach an inline comment outside changed lines, include the finding with its file and line range in the review summary or a regular PR comment instead of dropping it.
