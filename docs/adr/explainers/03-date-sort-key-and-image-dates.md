# Explainer: step 03, year-only sort key and image date tolerance

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text. The whole feature now goes upstream as one PR (`pr-plan.md` S1), so this step is three commits in that series that a reviewer will read one at a time.

- **Branch:** `multidb/03-date-sort-key-and-tolerant-compare`, three commits on top of step 02 (`86a8fcc6ed`).
  - `70f7358820` Add tests pinning the sort order of year-only premiere dates
  - `fea29ae902` Build the year-only premiere sort key from a fixed date
  - `bb1f10913b` Compare generated image dates with a one second tolerance
- **Size:** 4 files, +207/-2. Production code is +6/-3; the rest is tests.
- **Status:** gated and merged into `feature/multi-db`. Not pushed.

## What the step is, in one sentence

It removes two places where the code relies on SQLite storing dates exactly as .NET does. The sort order on SQLite is unchanged, and the image check changes only in a corner case no user would notice.

## Be upfront: this fixes nothing on SQLite

Both production changes exist so that the same code works on a database that stores dates differently. Say so plainly. A reviewer will ask "what does this fix on SQLite?", and the honest answer is "nothing; it is for PostgreSQL".

- The sort key change is exactly equivalent on SQLite.
- The image check is not quite. With a one-second tolerance, a file rewritten within a second of its stored time is no longer treated as changed. Upstream accepted the same trade-off in #18278, and the commit message says so. An earlier wording claimed "unchanged on SQLite", and review corrected it.

## Commit 1: tests for year-only sort order

**What users see.** Sorting by release date, an item with only a production year (no full premiere date) is placed as if it premiered on 1 January of that year. An item with neither comes first in ascending order.

**What the test seeds.** Ten movies with distinct names, so no assertion depends on how ties are broken:

- items with only a year: 1, 1975, 1999, 2010, 2011, 2021 and 9999;
- items with a full date: 1 January 2000 and 15 June 2010;
- two dated neighbours either side of "Year 2010": 31 December 2009 23:59:59 and 1 January 2010 00:00:01;
- one item with neither.

The neighbours pin "start of the year" exactly. Moving the base to 2 January makes the test fail. The first version of the test did not catch that, and review found it.

The test passes on the old code and the new code. That is its job: it proves commit 2 does not change the order.

## Commit 2: the sort key

**Old code.** The key was `DateTime.MinValue.AddYears(ProductionYear - 1)`.

**New code.** The key is `new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddYears(ProductionYear - 2000)`. Same dates, built from an ordinary date.

**Why.** Npgsql maps `DateTime.MinValue` to PostgreSQL's `-infinity`. `-infinity` plus any interval is still `-infinity`, so on PostgreSQL every year-only item would get the same key and they would sort as equal. Verified during the engine probe: PostgreSQL returned 2021 before 1999.

**Evidence that SQLite is unchanged.**

- The generated SQL differs only in the base literal and the offset: `'0001-01-01 00:00:00'` with `- 1` before, `'2000-01-01 00:00:00'` with `- 2000` after.
- Both expressions were evaluated in SQLite for every year from -3000 to 20000. All 23,001 keys are identical, including NULL for years from 10000 up.
- The SQL was captured under the time zones America/New_York, Asia/Tokyo and UTC. It is the same in all three, before and after. EF puts the constant into the SQL as a literal inside `strftime`, and the date converter does not touch it, so the key never depended on the server's zone. (An earlier note in our design said it did. That was wrong and is withdrawn in ADR-0004 R8.)

## Commit 3: image date comparison

**What users would see without it.** Jellyfin regenerates a generated image (the collages for collections, playlists, genres, artists and folders) when the modification time stored for it differs from the file's time. A database that keeps only microseconds returns a stored time a few ticks off the file time, so every refresh would regenerate every such image.

**The change.** At `BaseDynamicImageProvider.cs:302` the exact `!=` becomes `Math.Abs((image.DateModified - modificationDate).TotalSeconds) > 1`.

**Why one second and not one microsecond.** Upstream already fixed the same problem elsewhere.

- Upstream PR #18278, for issue #18274, is in our base as the backport `a2bd48c1d2`, merged by crobibero. It uses this exact one-second form in `BaseItem.cs` and `ItemImageProvider.cs`, with the comment "Stores such as PostgreSQL keep only microseconds…".
- `BaseItemExtensions.HasChanged` and `LibraryManager` already used one second.
- Our first version added a new public microsecond helper. Review pointed out that this would make this one call site the only microsecond rule, behind new API. We dropped it and followed upstream (ADR-0004 R9). If a reviewer asks why one second, the answer is "the same as #18278 and the other image date checks".
- The comment is written in #18278's style.

**Test** (`BaseDynamicImageProviderTests`), modelled on upstream's `MergeImages_StoredTimeTruncatedToMicroseconds_NoChange`:

- unchanged: the same time; 1 ms either way; exactly 1 s either way; a stored time truncated to microseconds;
- changed: 1 s plus one tick either way; 10 minutes either way;
- an image with no path is unchanged.
- Five of the eleven cases fail on the old code, so this test genuinely goes from red to green.

## What was deliberately left out

- **`20250420230000_RefreshInternalDateModified.cs:103`** also compares `writeTime != itemModificationTime` exactly. It is a migration routine that runs once. On a microsecond store it refreshes a few extra rows one time, and that is harmless. It is left alone (ADR-0004 R10).
- **Seven comparisons against `DateTime.MinValue` or `default`**, such as `Folder.cs:239` and `MetadataService.cs:95`, are unaffected. They were listed in the step 02 explainer.
- **No new helper and no new public API.**

## How it was tested

- Release build: 0 warnings. Debug build: 0 errors, and the same 211 warnings as upstream's own Debug build, none in a file this step touches. `git diff --check` is clean.
- `Jellyfin.Server.Implementations.Tests`: 1121 passed, 12 skipped. `Jellyfin.Server.Tests`: 39 passed. `Jellyfin.Server.Integration.Tests`: 129 passed, 3 skipped.
- Each of the three commits was built and tested on its own, in Release and Debug.
- Mutation checks, each reverted:
  - a 2 January base fails the sort test;
  - `>= 1` instead of `> 1` fails the "exactly one second" cases;
  - the old exact comparison fails the truncation cases.
- An independent code review failed the first version (the helper, and the sort test not pinning 1 January). The rework was reviewed again before merging.
- To reproduce: `dotnet test tests/Jellyfin.Server.Implementations.Tests --filter "FullyQualifiedName~PremiereDateOrder"` and the `BaseDynamicImageProviderTests` class in the test project that holds it.

## Questions to be ready for

- **"Why touch the sort key at all if SQLite is fine?"** Because PostgreSQL turns the old base into `-infinity`. The new form is equivalent everywhere, and the test proves the order is unchanged.
- **"Why 2000?"** Any ordinary date works. 2000 keeps the offsets small for real production years, and the result is identical for every year.
- **"Is one second too loose?"** A real change to a generated image file is far more than a second apart. Upstream made the same judgement in #18278.
