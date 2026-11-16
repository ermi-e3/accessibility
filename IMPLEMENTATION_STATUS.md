# AccessLearn Implementation Status

Updated: 2026-11-16

## Current Delivery

| Item | Value |
|---|---|
| Active managed branch | `nosy-degree` |
| Intended feature | Offline Learning |
| Base commit | `main` at `8df0344767d1f67f96e4b19a34885c016d709d58` |
| Feature state | Implemented; final static review clear; runtime/browser testing skipped by user instruction |
| Tests | Earlier compile/no-service/frontend checks passed before final hardening; final post-review runtime/browser suite intentionally not run |

The repository's original worktree currently owns the clean `feature/offline-learning` branch. This managed fork preserves its work on `nosy-degree` rather than overwriting another worktree.

## Offline Learning Delivered

- Durable Hangfire generation and retry lifecycle for one lightweight package per immutable publication/profile.
- Lease owner/token, heartbeat, attempt-specific workspace, expired-lease reconciliation, bounded transient backoff, and periodic stale-package/object cleanup.
- Publication-pinned offline audio, reviewed WebVTT captions, immutable transcript text, validated semantic HTML documents, shell assets, public package manifest, and deterministic portable ZIP.
- Package-owned immutable object rows with SHA-256, byte size, content type, language, role, required flag, stable relative path, and retry-safe metadata.
- Preflight package-size enforcement and source-object checksum/size validation before activation.
- Owner/admin/scoped APIs for generation, status, and retry; anonymous current-publication manifest and stable ZIP download route.
- Hosted `/offline/` PWA with application shell service worker, IndexedDB library, storage estimates, per-asset SHA-256 verification, resumable temporary caches, atomic activation, update detection, removal, and network-off audio/text/caption/document access.
- Old ready revisions stay available until the new package is fully verified and committed; signed asset URLs refresh once on expiry and are never persisted locally.
- Partial downloads are visible and removable/resumable; required cache entries are revalidated before activation and open.
- Visible audio-caption selector/cue region, caption download links, English/Amharic core strings, sandboxed/CSP-protected documents, responsive controls, and offline portable HTML.
- Player offline-download link directs embedded learners to the same-origin PWA without attempting cross-origin service-worker control.
- `AddOfflineLearning` and `AddOfflineReliability` migrations add package lifecycle/lease fields and package-owned asset metadata with legacy-row normalization.

## Existing Completed Features

- Project foundation, PostgreSQL/Identity, S3-compatible storage, localization, errors, health, and security.
- Lesson/media ingest, API-key scopes, quarantine/hash/signature/ffprobe validation.
- Accessible Web Component player, immutable publications, reviewed captions/transcripts, sign-language and described-audio delivery.
- Durable processing pipeline, FFmpeg renditions, whisper.cpp Draft transcripts, Google English/Amharic Draft translation, imports/edits/exports/retries/staleness.

## Approved Future Document Tooling

- `DocumentFormat.OpenXml` 3.5.1 approved.
- WeasyPrint 70 and pinned veraPDF CLI approved.
- Liblouis/BRF deferred as an optional bonus.

## Validation State

Completed before testing was stopped:

- Solution build succeeded with zero warnings/errors.
- EF model reported no pending changes after `AddOfflineLearning`, `AddOfflineReliability`, and `AddOfflineSnapshotMetadata` generation.
- Formatting verification passed.
- No-service .NET suite passed 37 tests with 15 service-backed skips.
- TypeScript typecheck and production player/PWA bundle passed; generated SDK was 49.54 kB, 13.27 kB gzip.
- Playwright 1.65.1 and Chromium were installed.

Not executed after the final hardening changes, per user instruction to skip testing:

- Final compile/migration recheck and migration apply/rollback.
- PostgreSQL/S3/Hangfire package generation and public API runtime.
- ZIP extraction/integrity and cross-OS byte comparison.
- Playwright interruption, activation, offline reload, update/removal, mobile tests.
- S3 CORS/signed expiry, storage quota/eviction, Android, NVDA/TalkBack.

The final read-only implementation review found no release-blocking, high, or medium defects.

## Known Follow-Ups

- Docker Desktop remains unavailable in the host environment; later testing may require the previously approved native PostgreSQL/Moto fallback.
- Cache eviction, low-memory hashing, interrupted activation, signed-object CORS, range seeking, browser quota behavior, and TalkBack/NVDA require the testing prompt.
- Document HTML is included only after the future Documents feature marks it validated.
- The real Savanna Rails/React source remains unavailable; only the framework-neutral player/fixture integration exists.
- Final runtime/browser verification remains intentionally outstanding because testing was explicitly skipped.

## Next Review Gate

Review and test Offline Learning before starting `feature/accessible-documents`. Do not merge or start Documents from this worktree until the Offline feature is accepted.
