# Explainer: X3, Latest TV failing when no matching episode has a creation date

These are study notes for understanding and defending this fix in your own words. Do not paste them as PR text.

- **Branch:** `fix/latest-episodes-null-date`, one commit off the fork's upstream base `61f9a9df23`, the same base X2 uses. It is not part of the multi-database step chain.
  - `16c7d206be` Fix Latest TV failing when no matching episode has a creation date
- **Size:** 2 files, +83/-2. Production code is three lines.
- **Fork merge:** side branch `multidb/x3-on-fork`, merged as `2500e2accb`.
  - `824a6d2389` merges X3. In the merge, the test moves from upstream's `SqliteDbTestFixture` to the fork's `JellyfinDbTestFixture`, so it runs on the selected database.
  - `3e7a73a1d0` adds a fork-only test with more undated series than the request limit.
- **Status:** gated and merged into `feature/multi-db`. Not pushed. Whether it ships inside the big PR or on its own is decided at submission time, as for X1 and X2.
- **Branch history:** X3 was first cut from `upstream/master` `e53a6d3e6a`, six commits newer than the fork's base. A trial merge into the fork then pulled in unrelated upstream changes and a `Directory.Packages.props` conflict. It was rebased onto `61f9a9df23` with an identical diff. The pre-rebase commit is kept on `fix/latest-episodes-null-date-backup-a`.

## What the code does

`BaseItemRepository.GetLatestTvShowItems` (`Item/BaseItemRepository.Querying.cs`) builds the home screen "Latest" row for a TV library:

1. It picks the series with the newest episodes, using `max(DateCreated)` per series.
2. It loads those episodes, newest first.
3. For each series it takes the newest episode's `DateCreated`, subtracts 24 hours, and counts the episodes inside that window. A series with several recent episodes is shown as the series or season; one with a single recent episode is shown as that episode.

The old code did `episodes[0].DateCreated ?? DateTime.MinValue` and then `.AddHours(-24)`. Subtracting from `DateTime.MinValue` throws `ArgumentOutOfRangeException`, which failed the whole request.

The fix keeps the date as nullable. When the newest episode has no date, the cutoff is `null`. `ep.DateCreated >= null` is false for every episode, so the group has no recent episodes and is returned as its newest listed episode. Dated groups are untouched.

## When it happens

The throw needs **every** episode that matches the request to be undated:

- `BaseItemMapper` stores a `DateCreated` of `DateTime.MinValue` as `NULL`.
- Ascending or descending, `NULL`s sort last on SQLite, so `episodes[0]` is undated only when its whole group is undated.
- As soon as one selected series has a date, step 1's cutoff drops the undated episodes before step 3 runs.

The reviewer found no path in current code that stores an undated episode. Virtual episodes, file-stat failures, NFO and metadata merges are all guarded or filtered. The real source is old data: 10.10 could store NULL or year-1 values, `MigrateLibraryDb.cs:1025-1028` keeps those as `NULL`, and the `FixDates` routine sets year-1 values to `NULL`. No upstream issue reports the crash. So the commit is hardening plus a fix for legacy-imported libraries, and its message does not claim users have reported it.

## Tests

`tests/Jellyfin.Server.Implementations.Tests/Item/BaseItemRepositoryLatestTvShowTests.cs`, through the public `GetLatestItemList` with `CollectionType.tvshows`:

- **Every episode undated.** It threw before the fix. Now the request returns one of the episodes.
- **An undated series beside a dated one.** It passes before and after. It records that dated groups behave as before and that undated series drop out once any series has a date.
- **Fork only: more undated series than the limit.** Twelve undated series and one dated one. It guards engines that put `NULL`s first on `DESC`. On PostgreSQL the query uses `ORDER BY max("DateCreated") DESC NULLS LAST`, because the step 14 NULL-order generator covers aggregate expressions too. When that is forced back to a plain `DESC`, only this test fails.

## Review notes

- A stored non-null date within 24 hours of `DateTime.MinValue` would still throw. Normal saves cannot produce one, so it is left alone.
- A smaller diff, `episodes[0].DateCreated?.AddHours(...)`, was considered. The fix already does that and keeps `mostRecentDate` for the later sort.
- **Upstream overlap:**
  - #18381 rewrites the same cutoff line. If it lands without this fix, it reintroduces the throw.
  - #18423 adds `BaseItemRepositoryLatestItemListTests.cs`, where a reviewer may want these tests to live.
