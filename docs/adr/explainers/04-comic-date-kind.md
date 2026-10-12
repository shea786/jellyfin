# Explainer: step 04, comic publication dates as UTC calendar dates

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text. The whole feature goes upstream as one PR (`pr-plan.md` S1), so this step is one commit in that series.

- **Branch:** `multidb/04-comic-date-kind`, one commit on top of step 03 (`bb1f10913b`).
  - `51c81ac7ee` Store comic publication dates as UTC calendar dates
- **Size:** 5 files, +102/-2. Production code is two lines; the rest is tests.
- **Status:** gated and merged into `feature/multi-db`. Not pushed.

## What the step is, in one sentence

It fixes a bug that users on SQLite can see today: on a server east of UTC, a comic's publication date shows as the day before the date written in the file.

## This one fixes something on SQLite

Steps 01 to 03 had no SQLite benefit. This one does, and the commit message says so.

- **The cause.** `ComicInfoReader.cs:214` and `ComicBookInfoProvider.cs:238` built the premiere date with `DateTimeKind.Unspecified`. The database converter calls `ToUniversalTime()` on every write, and .NET treats `Unspecified` as server-local time. The SQLite `DateTimeKindValueConverter` already did this before the series (`v => v.ToUniversalTime()` at 61f9a9df23), so the bug predates it. Step 02 only moved the rule into a shared converter.
- **What it did.**
  - Server in Asia/Tokyo: 2021-03-01 was stored as 2021-02-28T15:00Z. The book showed 28 February.
  - Server in America/New_York: the day was right but the value carried a time of day, 05:00.
  - Server in UTC: nothing visible. This is why nobody noticed it in CI or on most Docker hosts.
- **The fix.** Both constructors now pass `DateTimeKind.Utc`. A publication date is a calendar date with no zone, so "midnight UTC on that date" is the stored value in every zone.
- **The precedent.** Upstream already does this for other date-only fields. `PremiereDateComparer.cs:52` builds its year key with `DateTimeKind.Utc`, and the NFO parsers go through `XmlReaderExtensions.cs:54,74`, which parse with `AssumeUniversal | AdjustToUniversal`, giving `Kind == Utc`. `BaseNfoParser.cs:560` uses that for premiered and release dates.

## Why it belongs in a multi-database series

Not because of PostgreSQL. The shared converter (step 02) makes every provider apply `ToUniversalTime()` the same way, so the date shift is identical on all engines. The reason to fix this one is narrower: it is the only date producer that the step 02 audit found where the fix is unambiguous (a comic date is a calendar date with no time) and tiny. The others need checking against what their source returns (see the last section).

## Tests

- **Parser tests (6 cases).** Both readers: the kind is `Utc`, and the year, month and day are those in the file. ComicInfo has a three-part date including a leap day. ComicBookInfo has a two-part date, which uses the first of the month. These fail without the change in every zone, including UTC, because they assert the kind directly.
- **Database test (2 cases)**, `ComicPremiereDateStorageTests`. It stores a parsed ComicInfo date and reads it back. This is the user-visible behaviour.
  - Be upfront: **it passes before the fix when the machine's zone is UTC**, which is what CI uses. It only fails under another zone. It therefore protects developers running tests locally in a non-UTC zone, not CI. ADR-0004 R6 accepts that trade-off for converter tests, but the reviewer pointed out that here a zone-independent guard (the parser tests) already exists, so this test mostly documents the bug. It was kept, and you should say that candidly if asked.
  - It is also the only test in `Jellyfin.Server.Implementations.Tests` that imports `MediaBrowser.Providers`, which it reaches through the integration test project reference.

## Dates already stored are not corrected automatically

This is the part to be careful about. The review corrected the first wording of the commit message here.

- Changing the reader fixes dates read from now on. Stored dates keep their shifted value.
- A refresh with "Search for missing metadata" does not rewrite them. `MetadataService.cs:914-917` leaves `shouldReplace` false for a plain full refresh, and `:1250` overwrites `PremiereDate` only when `replaceData` is true or the date is empty.
- "Replace all metadata" corrects them. So does a default scan after the comic file itself has changed.
- This was established by reading the code, not by running a refresh.

## How it was tested

- Release build: 0 warnings. Debug build: 0 errors and 211 warnings, identical (sorted, paths stripped) to upstream's own Debug build, none in a file this step touches. `git diff --check` is clean.
- Release tests: Providers 519 passed; `Jellyfin.Server.Implementations.Tests` 1123 passed, 12 skipped; `Jellyfin.Server.Tests` 39 passed; `Jellyfin.Server.Integration.Tests` 129 passed, 3 skipped.
- Time-zone matrix, run with and without the fix (the fix reverted in a throwaway worktree):

  | | Tokyo | New York | UTC |
  |---|---|---|---|
  | With fix, parser (6) | pass | pass | pass |
  | With fix, database (2) | pass | pass | pass |
  | Without fix, parser (6) | fail | fail | fail |
  | Without fix, database (2) | fail (day 31 read back as 30, month 3 as 2) | fail (05:00 instead of 00:00) | pass |

- Each reader reverted on its own fails its own three cases, so both are guarded independently.
- An independent code review failed the commit on one point, the message sentence about refreshing. It was reworded with an identical tree (`527584955d` to `51c81ac7ee`).
- To reproduce: `TZ=Asia/Tokyo dotnet test tests/Jellyfin.Providers.Tests --filter "FullyQualifiedName~Comic"` and the same filter on `tests/Jellyfin.Server.Implementations.Tests`.

## What was deliberately left out

Other producers have the same kind of problem. They are listed in ADR-0004 U2 and are not part of this series.

- Local time converted to UTC, which moves the date east of UTC: `LibraryManager.cs:3360` and the TMDb providers (`TmdbEpisodeProvider.cs:186`, `TmdbMissingEpisodeProvider.cs:571`, `TmdbMovieProvider.cs:79`, `TmdbPersonProvider.cs:127`, `TmdbSeriesProvider.cs:149,166`).
- Kind left `Unspecified`: `AudioFileProber.cs:403`, `OmdbItemProvider.cs:199-201`, `OpfReader.cs:131-134`.
- Kind not checked: several TMDb and MusicBrainz sites, `PhotoProvider.cs:112`, `AudioFileProber.cs:390`.
- Each needs checking against what its source returns first. A TMDb date is a calendar date, but EXIF and audio tags can carry a real time, so a blanket change would be wrong.
- The list goes to the user as a follow-up, not into this PR.

## Questions to be ready for

- **"Is this a PostgreSQL change?"** No. It is an existing SQLite bug. It sits in the series because the same date handling is being made uniform, and a reviewer reading step 02 would otherwise ask why comic dates were skipped.
- **"Why not fix the converter instead?"** The converter cannot tell a calendar date from a real instant. Only the producer knows. Treating `Unspecified` as UTC in the converter would break values that really are local.
- **"Why not fix the other producers?"** Each one needs its own check; see above.
- **"Will existing libraries be fixed?"** Only by a Replace all metadata refresh.
