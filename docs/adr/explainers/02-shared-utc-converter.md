# Explainer: UTC DateTime conversion in the shared database context

These are study notes for writing and defending this PR in your own words. Do not paste them as PR text.

- **Branch:** `multidb/02-shared-utc-converter`, two commits on top of PR 01 (`7567e55900`). It must be rebased on `upstream/master` and, if PR 01 has not merged, opened after it or rebased past it.
  - `f1f6254a12` Add tests for DateTime round trips through the database
  - `86a8fcc6ed` Apply UTC DateTime conversion in the shared database context
- **Size:** 6 files, +122/-69 in total. Commit 1 is one test file (+85). Commit 2 is +37/-69, including 16 lines in the same test file for the model test.
- **Status:** ready for you. Not pushed.

## What the PR is, in one sentence

It moves the rule "store dates as UTC and read them back marked as UTC" out of the SQLite provider package and into the shared `JellyfinDbContext`, with no change in what SQLite stores or returns.

## Be upfront: this is a refactor with no user-visible change

- Nothing a user sees changes. Same stored values, same values read back, same schema, no migration.
- The reason for the PR is structural. The rule is not specific to SQLite, but it lives in the SQLite provider (`DateTimeKindValueConverter` and `SetDefaultDateTimeKind`). Any other provider would have to copy it, or it would store `Local` and `Unspecified` values differently.
- Say that plainly. Do not present it as a bug fix.

## What changed

- New `UtcDateTimeConverter` in `Jellyfin.Database.Implementations/ValueConverters/`. It writes `ToUniversalTime()` and reads `DateTime.SpecifyKind(value, Utc)`, the same two operations the SQLite converter performed.
- One registration in `JellyfinDbContext.ConfigureConventions`, before the provider's own conventions: `Properties<DateTime>().HaveConversion<UtcDateTimeConverter>()`.
  - In EF Core a `DateTime` convention also applies to `DateTime?` properties, so a second `DateTime?` registration is not needed. It was in an earlier version and review removed it (ADR-0004 R5).
  - It runs first so that a provider can replace it by registering its own `Properties<DateTime>()` afterwards.
- Removed from `Jellyfin.Database.Providers.Sqlite`: `DateTimeKindValueConverter`, `SetDefaultDateTimeKind`, the `ModelBuilderExtensions` class that held it and `UseValueConverterForType`, and the call in `SqliteDatabaseProvider.OnModelCreating`.

## Why the deleted code can go without a deprecation step

`Jellyfin.Database.Providers.Sqlite` has no `PackageId` and is not in the ci-compat API list, so it is not a published API. Plugins do not compile against it. If a maintainer asks, that is the answer. If they prefer an `[Obsolete]` period anyway, that is a small change to make.

## How we know SQLite behaves the same

- **Model equivalence.** The EF model was compared before and after across 312 properties. The only difference is the converter class name on 21 `DateTime` properties (7 `DateTime`, 14 `DateTime?`). `HasPendingModelChanges` is false, and converters are not part of the model snapshot, so there is no migration.
- **Tests written first.** Commit 1 adds the round-trip tests and they pass on the old code. Commit 2 changes the code and they still pass. That is the evidence that behaviour is unchanged.
  - Every `DateTimeKind` (Utc, Local, Unspecified) comes back as UTC and equal to the input converted to UTC.
  - `DateTime.MinValue` and `MaxValue` as UTC round-trip exactly.
  - Each value is written and read back through two nullable properties on `BaseItem` (`DateCreated`, `PremiereDate`) and one non-nullable property on `ActivityLog` (`DateCreated`).
- **Model test (commit 2).** `Model_DateTimeProperties_UseSharedUtcConverter` asserts that every `DateTime` and `DateTime?` property in the model uses `UtcDateTimeConverter`.
- **A provider override reaches all 21 properties.** A throwaway provider that registered only `Properties<DateTime>()` with another converter changed all 21 (7 `DateTime`, 14 `DateTime?`). Without it, all 21 use the shared converter. `HasPendingModelChanges` was false in both cases.
  - Checked twice, once by the implementer and once independently against `86a8fcc6ed`.
  - Control: putting back the removed `Properties<DateTime?>()` registration made the same override reach only 7 of 21. That is the defect review found, and the probe catches it.
  - The probe was not committed. If a maintainer wants this guarded permanently, it can become a test.

## Why the `ActivityLog` coverage is in

The `BaseItem` properties are nullable. `ActivityLog.DateCreated` is a non-nullable `DateTime`, and the two cases go through different parts of EF's conversion. It is a few lines, not an extra feature.

## Limits of the tests (be ready for these)

- **The `Kind` checks are weak on UTC machines.** If the local zone is UTC, as on GitHub runners, `Local` and `Unspecified` inputs already equal their UTC value, so the tests only prove the value is read back with `Kind == Utc`. The `ToUniversalTime()` step is exercised only under a non-UTC zone.
  - Verified by hand with `TZ=America/New_York` and `TZ=Europe/London`. No per-zone CI job was added; it is not worth one for a single converter. (ADR-0004 R6.)
  - To show a maintainer: `TZ=America/New_York dotnet test tests/Jellyfin.Server.Implementations.Tests --filter "FullyQualifiedName~DateTimeRoundTripTests"`.
- **Mutation check.** Removing the `ToUniversalTime()` call fails the round-trip test under New York time. Do not quote that as a CI guarantee.
- **Comparison is to within a microsecond.** The stored precision is not what is being pinned, so the helper allows 10 ticks.
- **Sentinels.** `MinValue` and `MaxValue` round-trip exactly only when their kind is `Utc`. A sentinel with `Unspecified` or `Local` kind is shifted by the zone offset on write, exactly as before this PR. So there is no regression, and also no new guarantee.

## Result of the sweep for exact date comparisons

While in this area, every place that compares a stored timestamp for equality was re-checked (ADR-0004 R4).

- `BaseDynamicImageProvider.cs:299` is the one place comparing a stored value with a file-system time for equality. It is out of scope here and is the subject of the next PR in the stack.
- Seven more comparisons are against `DateTime.MinValue` or `default`: `Folder.cs:239`, `BaseItemMapper.cs:307` and `:310`, `MetadataService.cs:95`, `ChannelManager.cs:1187`, `PeopleValidator.cs:76`, `ArtistsValidator.cs:87`. They are unaffected.
- Line numbers are on this branch and may shift after a rebase.

## What was deliberately left out

- No second provider's behaviour. The PR does not add one and does not mention one in code or comments.
- No microsecond truncation on write. It was considered and rejected (ADR-0004 R1): it would change stored SQLite values, and tolerant comparison handles the real cases.
- No migration, no change to column types.

## How it was tested

- Release build: 0 warnings, 0 errors. `git diff --check` is clean.
- `Jellyfin.Server.Implementations.Tests`: 1108 passed, 12 skipped. `Jellyfin.Server.Tests`: 39 passed. `Jellyfin.Server.Integration.Tests`: 129 passed, 3 skipped.
- Commit 1 on its own passes under `Europe/London` and `America/New_York`; with commit 2 all pass under New York.
- An independent code review failed the first version for the redundant `DateTime?` registration. It was removed, with the dead helper and a test comment that referred to other databases. The fixes were folded into the two commits.

## Before you open the PR

- The commit messages are factual and minimal. Reword them freely.
- The PR template has a **Code assistance** section. Jellyfin's LLM policy requires you to describe honestly how assistance was used.
- Expect the question "why now, if nothing changes?". The honest answer is that the rule belongs in shared code, and this is the smallest step that puts it there. It is reasonable to mention it in the Meta discussion as the second small step after the tests-only PR.

## Later correction

The "no regression, no new guarantee" note on sentinels above was right about PR 02 but hid an inherited bug: an `Unspecified` or `Local` sentinel was stored as a value that depended on the server's time zone. Step 02b fixes it. See `02b-utc-sentinels.md` and ADR-0004 R11.
