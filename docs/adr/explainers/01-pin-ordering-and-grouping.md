# Explainer: tests pinning item grouping, string order and NULL order

These are study notes for writing and defending this PR in your own words. Do not paste them as PR text.

- **Branch:** `multidb/01-pin-ordering-and-grouping`, two commits off `upstream/master`.
  - `f1520e80cb` Add tests pinning item grouping representatives
  - `7567e55900` Add tests pinning sort order of strings and NULLs
- **Size:** 7 test files, +566/-9. No production code changes.
- **Status:** ready for you. Not pushed. Rebase on `upstream/master` before opening.

## What the PR is, in one sentence

It writes down, as tests, which entry Jellyfin shows when several rows collapse into one, and in which order it lists names and missing values, so later changes cannot alter those results without someone noticing.

## The user-visible behaviour each test pins

Frame the PR around these. Other databases are background, not the headline.

**Which entry represents a group** (commit 1)

- **Alternate versions of a movie** (`BaseItemRepositoryGroupingTests`). A movie with a 4K and a 1080p file shows once in the library. The entry shown is a primary version (no `PrimaryVersionId`), and among primaries the one with the lowest Id. Detail pages and play actions target that entry.
  - `GetItemList_VersionGroup_ReturnsPrimaryVersion` now seeds two primaries and one alternate. The alternate has the lowest Id of the three, so a plain `MIN(Id)` would wrongly pick it. Turning the primary-only `Min` at `BaseItemRepository.QueryBuilding.cs:109` into `Max` makes this test fail.
  - `GetItemList_GroupWithoutPrimary_FallsBackToMinId`: when the library holds no primary of the group, the lowest Id is shown.
- **Episodes grouped by series** (`GetItemList_GroupBySeriesPresentationKey_ReturnsLowestIdOfEachSeries`). One entry per series, the episode with the lowest Id.
- **Genres sharing a presentation key** (`BaseItemRepositoryByNameRepresentativeTests`). The genre list shows one entry, the lowest Id.
- **People whose names differ only in case** (`PeopleRepositoryDuplicateNameTests`). "john smith" and "John Smith" list once, as the lowest Id.

**How names sort and filter** (commit 2)

- **Sorted lists** (`BaseItemRepositoryStringOrderTests`, Album, SeriesSortName, SortName). Order is by Unicode code point and case-sensitive: upper case, then `_`, then lower case, then accented letters. Seeded `Rock, _zed, alpha, Élan` come back in exactly that order.
- **Letter picker / name ranges** (`NameStartsWithOrGreater`, `NameLessThan`, for items and for people). These compare the lower-cased name, so they ignore case, but `_` still comes before `a` and `é` after `z`. Example: starting at "a" excludes `_zed`; ending before "é" excludes `élan`.
- **Tags and genres differing only in case or accents** (`BaseItemRepositoryItemValueStringTests`). The filter lists show one spelling, the smallest by code point. The three tests cover the four string-`MIN` call sites listed in ADR-0006 (`ByName.cs:144`, `:170`, and `Querying.cs:637`/`:650` together).

**Where missing values sort** (commit 2)

- `BaseItemRepositoryNullOrderTests`, 10 rows, one per user-selectable sort option: PremiereDate, DateCreated, CommunityRating, CriticRating, ProductionYear, IndexNumber, ParentIndexNumber, Album, SeriesSortName, DatePlayed.
- An item without a value comes first when sorting ascending and last when descending. Example: a movie with no release date is at the top of "Release date, ascending".

## Why the Ids look odd (you will be asked)

The grouping tests use `0fffffff-0000-0000-0000-0000000000ff` (lower) and `10000000-0000-0000-0000-000000000000` (higher).

- In canonical text order, which is what SQLite stores and what .NET `Guid.CompareTo` uses, the first is lower.
- Compared as the little-endian bytes of `Guid.ToByteArray()`, or in SQL Server `uniqueidentifier` order (last 6 bytes first), the second is lower.
- So any storage that orders Guids differently from today makes these tests fail. On SQLite today the tests just pass. Their job is to catch a future change, not a current bug.

The alternate version uses `00000000-0000-0000-0000-000000000002`, not `...0001`. The model seeds a placeholder BaseItem at `...0001` (`BaseItemRepository.PlaceholderId`), and the test database contains it.

## What was deliberately left out (be ready to say why)

- **One grouping branch is not covered.** `ApplyGroupingFilter` has a branch for `GroupBySeriesPresentationUniqueKey` combined with presentation-key grouping (`QueryBuilding.cs:100-104`). It cannot be reached through the public repository API, so no test pins it. Whether it is dead code belongs in a separate PR. (ADR-0007 P4.)
- **No "no client evaluation" test.** EF Core already throws when a filter, ordering or grouping cannot be translated to SQL. A test would re-test EF. (ADR-0007 P3.)
- **No non-ASCII upper-case range bounds.** Today SQLite's `lower()` folds ASCII only, so such a test would pin a result that later work intends to change. (ADR-0007 P5.)
- **No artist test.** The artist representative is chosen in memory with `ThenBy(e => e.Id)`, so it pins no database behaviour. (ADR-0007 P6.)

## Why a maintainer should want this

- These behaviours are easy to change by accident when the queries are touched. Examples: rewriting a `GroupBy`, or adding a collation or a value converter. Nothing currently fails when they change.
- Each test describes something a user sees: which version opens, where unknown dates land, what the letter picker returns.
- The PR does not depend on any multi-database work and stands on its own. Mention other database providers only as one reason these results could change. Do not lead with it.

## How it was tested

- Release build: 0 warnings, 0 errors. `git diff --check` is clean.
- `Jellyfin.Server.Implementations.Tests`: 1102 passed, 0 failed, 12 skipped. That is 26 new tests.
- Mutation checks, each reverted afterwards:
  - Changing the primary-only `Min` at `QueryBuilding.cs:109` to `Max` fails the two-primary version test.
  - An earlier mutation run over the representative and ordering sites failed 9 tests. Re-run it yourself before quoting numbers.
- An independent code review passed it, with nits that were then fixed.
- Reproduce: `dotnet test tests/Jellyfin.Server.Implementations.Tests --filter "FullyQualifiedName~Item."`

## On other databases (background only)

Of the planned providers, Postgres is expected to fail some of these until its provider handles Guid `MIN`, collation and NULL order. In the fork's harness those cases become known gaps. None of that is part of this PR.

## Before you open the PR

- The commit messages are factual and minimal. Reword them freely.
- The PR template has a **Code assistance** section. Jellyfin's LLM policy requires you to describe honestly how assistance was used.
- Tests-only PRs are usually welcome without an issue. If you prefer, mention it in the Meta discussion as the first, independent step.
