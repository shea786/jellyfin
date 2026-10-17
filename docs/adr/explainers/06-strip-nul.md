# Explainer: step 06, NUL characters

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text. The whole feature goes upstream as one PR (`pr-plan.md` S1), so this step is seven commits in that series.

- **Branch:** `multidb/06-strip-nul`, seven commits on top of step 05 (`ccbb7d666e`).
  1. `d5e45f3a20` Pin that text column lengths are not enforced on SQLite (tests only, +62)
  2. `8fe80530b4` Add RemoveNullCharacters and use it in GetCleanValue (+117/-3)
  3. `6b7b7d20b3` Strip NUL where values are de-duplicated or looked up (+734/-26)
  4. `27435e7872` Strip NUL characters from strings when saving (+1934/-4)
  5. `411cdbf786` Strip NUL characters from search input (+203/-1)
  6. `7a7fd72877` Strip NUL characters from name filters (+262/-14)
  7. `a741bc6035` Pin that an exact-match filter with a NUL character matches nothing (test only, +72)
- **Size:** 31 files, +3383/-47. Production code is 13 files, +596/-47; the rest is tests.
- **Status:** gated and merged into `feature/multi-db`. Not pushed.
- **History:** this step failed review four times before it passed. Know why; reviewers upstream will probe the same places. The design notes are ADR-0009 N1-N7.

## The problem

SQLite stores U+0000 in text. PostgreSQL rejects it, stored or as a query parameter: `invalid byte sequence for encoding "UTF8": 0x00`. NUL arrives from NFO files (the XML reader runs with `CheckCharacters=false`), ffprobe output and client strings. Before this step only the audio file prober removed it.

The decision is to **remove** NUL (not truncate at it, which would lose the text after it): `Mid\0dle` becomes `Middle`.

## The three rules, and how they were found

The obvious design is one hook in `SaveChanges` that strips every string. Each review round found a way that breaks something on SQLite, which is why the final design has three rules.

1. **Clean where a value is compared, before the comparison** (round 1). `ItemPersistenceService` de-duplicates genres and looks them up by the raw value. `Rock\0` next to an existing `Rock` found no match, was inserted, and the hook then turned it into a second `Rock`: the unique `(Type, Value)` index failed and the item save was lost. So every path that de-duplicates or looks up a string before inserting under a unique key cleans it first: item values, people names and roles, provider ids, user-data keys, device ids, display preferences. User names were already safe (the name rule rejects NUL).
2. **The hook never touches a key or unique-index column** (round 2). The legacy import routines also de-duplicate on raw values, and a routine that modified one column of an `ItemValue` had its unchanged unique `Value` cleaned into a duplicate, failing the upgrade. Chasing paths had failed twice, so the invariant changed: the hook skips any property in a key or unique index, for added and modified rows. A missed entry point now stores the NUL on SQLite as before and fails loudly on PostgreSQL; it never creates a duplicate. The legacy routines are untouched and behave as before.
3. **What is in memory and what is stored must not differ** (rounds 3 and 4). The hook cleans the entity EF tracks. For items that entity is a copy mapped from `BaseItem`, and `LibraryManager` caches the `BaseItem`. A series whose NFO id held a NUL kept the raw key in memory while its rows were cleaned; seasons and episodes are found by exact key, so the series showed none of them. So `ItemPersistenceService` cleans the `BaseItem` itself before mapping: an explicit list of every string the mapper copies, the joined lists (genres, studios, tags, production locations, artists, album artists) with duplicates removed after cleaning, provider ids (changed entry by entry, never cleared, because other threads read the cached item), and image paths. A guard test fills every string property of every concrete item type by reflection and fails if the mapper copies a string the list does not clean. Where the tracked entity *is* the cached object (users are copied but found by id, devices, user data), the hook keeps memory and storage in step.

## Existing libraries: the upgrade routine

Rule 3 creates a window for libraries that already hold NUL: a series saved again has a clean key while its children's rows still have the old one. Measured: with automatic series grouping on (the default, `LibraryOptions.cs:34`) and off, saving only the series hid the children. The fix is a one-time routine, `20261012120000_RemoveNulFromItemKeys`:

- It removes NUL from the stored presentation keys, external ids, series name, provider values and user-data keys, reading the rows and finding NUL in .NET (SQLite does not match NUL reliably in a query), and writes only rows that change, in one transaction.
- A provider value that held only NUL is deleted, as saving now drops it.
- When a cleaned user-data key collides with another row of the same item and user, one row stays: the newer last-played date, then the played row, then the higher play count, then the row already under the clean key. The loser is dropped whole, including its favourite flag and rating. Fields are not merged: adding play counts would count plays twice (the save path writes to every row), and taking a favourite from the loser could bring back one the user removed. Upstream also keeps one whole row.
- Logging: one Information line with the number of merges; per merge a Debug line with item id, user id and which side was kept. Keys are not logged, because audio keys contain artist, album and title tied to a user.
- It is pre-marked on fresh installs and runs after all EF migrations. Measured on a 2.2 GB SQLite database (200,000 items, 600,000 provider rows, 2,000,000 user-data rows): 3.7 s with a handful of NULs, 16.6 s with 20,000 merges at about 58 MB managed peak, 2.6 s on a re-run.
- Its date must sort after upstream's newest routine when the upstream series is built (watch list).

Not cleaned by the routine: display-only names such as `SeasonName` and `Album`, which nothing matches on; they are cleaned the next time the item is saved.

## Query input

- Commit 5: `LikePatterns` and `GetCleanValue` drop NUL from search terms. This also fixed a SQLite quirk: SQLite stops reading a `LIKE` pattern at a NUL, so `%Quas\0ar%` behaved as `%Quas` (matching only values ending in "quas"), and a term starting with a NUL matched every row.
- Commit 6: the name filters (`NameStartsWith`, `NameStartsWithOrGreater`, `NameLessThan`, the four People filters, the relevance term) use `RemoveNullCharacters`. Deliberate SQLite change: `bob\0` no longer sorts after `bob` in range filters.
- Commit 7 pins the gap: exact-match filters (path, presentation key, external ids, ratings, languages, provider ids, a user-data key lookup) are not cleaned. SQLite returns nothing for such a value; PostgreSQL raises an error. Recorded as a known PostgreSQL gap; the follow-up is to clean or reject NUL once at the API boundary.

## Commit by commit (what a reviewer sees)

1. Pins that over-length text and a long profile-image path are stored whole on SQLite (the "lengths are not enforced" decision, before PostgreSQL column types change).
2. `RemoveNullCharacters` in `Jellyfin.Extensions`, next to `TruncateAtNull`, and `GetCleanValue` removes NUL instead of turning it into a space. Stored data changes: `CleanName` and item-value clean values; old rows keep theirs until saved again. Also changes how query terms clean.
3. Entry-point cleaning (rule 1), the authorization header (device id, name, client, version, cleaned once where parsed), `DeviceManager` cleaning every id it receives (plugins call it directly), detached user data reattached by raw or cleaned key, and people credits dropped when the name is empty or whitespace after cleaning. On its own this only normalises input; it makes commit 4 safe.
4. The hook (rule 2), the in-memory item cleaning (rule 3), and the routine. One pass over the tracked entries is shared with the existing concurrency-token update, so a save does no extra change detection.
5-7. Query input, as above.

`RemoveNullCharacters` is the only new public API. `Jellyfin.Extensions` is on the ABI-compatibility list, so it is permanent; it sits next to the existing public `TruncateAtNull`. `Jellyfin.Database.Implementations` keeps its own inline removal because it does not reference `Jellyfin.Extensions`.

## Known limitations (state them, do not hide them)

- Exact-match filters with NUL: error on PostgreSQL (commit 7, gap).
- Writes that bypass the change tracker (`ExecuteUpdate`, raw SQL) are not cleaned. The audit found none that carry user or metadata strings except the series-key migration routine, which on PostgreSQL only matters when moving existing SQLite data; that data path must strip NUL itself.
- A legacy `DisplayPreferences.Client` holding a NUL is not matched by the cleaned client name, so that client's display preferences reset once.
- Rows written before the upgrade keep NUL in non-key columns until saved again (the routine covers the matching keys only).

## How it was tested

- Every commit: Release 0 warnings; Debug the same 211 warnings as upstream; tests in Release. Final: `Jellyfin.Server.Implementations.Tests` 1341 passed, 12 skipped; `Jellyfin.Server.Tests` 52; `Jellyfin.Server.Integration.Tests` 129 passed, 3 skipped; `Jellyfin.Extensions.Tests` 173.
- Each commit's own tests fail before it and pass after it (the guard tests in commit 4 for unique columns pass without the hook by design; they guard the rule).
- Mutations caught: the pre-dedupe strip, the `GetCleanValue` change, the `LikePatterns` strip, the unique-index skip, the in-place item cleaning (and a single property removed from its list), the header strip, the raw-or-cleaned reattach, the user-data cleaning in the routine, each tie-break rule, the empty provider drop and the genre de-duplication.
- The reviewers' reproductions (round 2 P1-P7, round 3 R2/R4/R5/R6) pass. R1 inserts a raw key past the service and R3 is a raw exact match that fails before this step too; both are explained.
- Save cost: a 10,000-row save benchmark showed no difference outside noise (about ±10% on a loaded machine).

## Questions to be ready for

- **"Why not just strip in one SaveChanges hook?"** Because three rounds of review showed it creates duplicate keys and makes the cache disagree with the database. The three rules exist for those reasons.
- **"Why remove instead of truncate?"** Truncating silently loses the text after the NUL.
- **"Why a migration?"** Libraries that already hold NUL in matching keys would otherwise hide a series' children after the next save.
- **"Why not merge the duplicate user-data rows?"** Merging double-counts plays and can revive a removed favourite; upstream keeps one whole row too.
