# Explainer: step 19, bounds for the strings in keys and indexes

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/19-indexed-string-bounds`, five commits on the `feature/multi-db` tip `adbafb61f0`.
  - `32e44b5f8c` Bound the key strings and mark the unbounded indexed text
  - `ba39763589` Test that indexed strings are bounded or marked unbounded
  - `efa9191dfa` Shorten over-long user data keys and device ids with a hash
  - `5691637ffd` Cut, drop or refuse key strings that are longer than their column
  - `7d0170471a` Reject a changed key string that is longer than its column on save
- **Status:** three review rounds. Round 1 failed (4 blockers), round 2 failed (a restore gap and a false skip reason), round 3 PASS WITH NITS, then a small fold of the nits (no further round). Merged into `feature/multi-db` after step 22. Not pushed. Backups: `multidb/19-backup-a`, `-d`, `-e`, `-f`, `-g`, `-r2`, `-r3` (`-r3` is the round 3 tip `4794df4abf`).
- **Relevant decisions:** ADR-0009 (L1 to L2e, with the step 19 amendments), ADR-0011 R3.
- **No schema change.** Both providers already store these columns without a length, so the model snapshots are refreshed and no migration file is added. The step adds a startup routine, `20261013090000_NormalizeOverlongKeys`, that rewrites stored data.

## Why it exists

SQL Server and MySQL cap the size of an index key, and neither can index an unlimited text column as it is. SQLite and PostgreSQL accept any length, so the code has never had to care. To run on every engine, each string that sits in a key or an index needs a known upper size, and the data already stored has to fit it. The hard part is not the annotation, it is that real Jellyfin data does exceed such bounds: the user data key of a song is built from title, album and artists, and the web client's device id is built from its user agent.

## What changes, commit by commit

- **Bounds and the unbounded mark (a).** Ten key-class columns get a maximum length: `UserData.CustomDataKey`, `PeopleBaseItemMap.Role`, `BaseItemProviders.ProviderId`, `ItemValues.Value`, `CustomItemDisplayPreferences.Key`, `ApiKeys.AccessToken`, `Devices.AccessToken`, `DeviceOptions.DeviceId`, `BaseItems.Type` and `BaseItems.MediaType`. User names (255) and the display preferences client (32) already had one; they are now named next to the others. The remaining indexed strings (paths, titles) hold free text that cannot be shortened without losing data. They carry an internal `IsUnboundedIndexedText()` annotation so that a provider can decide how to index them (a prefix on MySQL, a computed column on SQL Server). The annotation changes no schema.
- **The model check (b).** A test over both providers' models: every string in a key or index has either a length or the unbounded mark, never both. For the bounded ones it also sums each key against the entry limits (SQL Server 900 bytes clustered and 1700 otherwise at two bytes per character, MySQL 3072 at four, PostgreSQL 2704 counted at four to be safe). Small hand-built models show that an unclassified string, a doubly marked one, an index over the limit and a key of unknown size are each reported. A new indexed string cannot be added without this test forcing the decision.
- **Hashing the lookup keys (c).** `KeyNormalizer` turns a value over the bound into prefix, a dash, and the lowercase SHA-256 hex of the whole value, so the result fits, is deterministic, is idempotent, never splits a surrogate pair, and keeps two values that share a prefix apart. It is applied wherever a user data key or device id reaches the database: user data reads and writes, the device manager, the authorization header, new sessions, quick connect, and the entries of the `EnabledDevices` policy (kept once each afterwards, which also removes exact duplicates that were already stored). The session manager normalises on entry to its public methods, so a caller that passes the id the client sent still finds the session. The startup routine rewrites stored keys, device ids, device option ids and `EnabledDevices` entries, and when two user data rows end up with the same key it keeps the one with the later last played date. A web client with a long device id can now log in again and keep one device row.
- **Cut, drop or refuse (d).** Values that are free text rather than lookup keys cannot be hashed, because the original is wanted back:
  - A person role is cut to 400 and trimmed at the end, an item value to 512; equal results merge into one credit or value.
  - A provider id whose name is over 128 is dropped with a warning; the other ids of the item are kept.
  - A user name over 255 or a display preferences client over 32 is a 400 at the API; a custom preference key over 256 is dropped with a warning.
  - The same rules apply where old data comes in: the library, user data, authentication, display preferences and user imports (an over-long user is skipped with a warning, the others are imported), and the backup restore. The restore cleans what it can and stops before emptying the database when a column that cannot be shortened holds a too-long value, naming table, column and maximum.
- **Backstop on save (e).** `EnforceKeyLengths` in `SaveChanges` throws `InvalidOperationException` naming entity, property and maximum for an added entity or a modified property whose value differs ordinally from the one it was loaded with. Rows already stored with a longer value stay readable and can be saved again, including by code that attaches an entity whole and so marks every property modified. No new exception type, because the assembly is on the compatibility list. Properties are looked up once per entity type.

## What the gates found and what was done

- **Round 1.** The first design truncated everything, which is wrong for lookup keys (two keys cut to the same prefix would share user data). The rules were redesigned into the L2a to L2e split above, recorded as an ADR amendment.
- **Round 2.** The restore left four columns unchecked, and a skip reason in the tests said a column was `varchar` on PostgreSQL when it is `text`. The restore rules are now derived from the model, with a test that fails when a bounded column has no rule, and the skip reasons were corrected.
- **Round 3 and the fold.** The last startup-abort path was the old users database import (a 300-character name would fail the whole startup); it now skips the user. Duplicate `EnabledDevices` entries are removed inside `KeyNormalizer.NormalizeDeviceIds`, so the three call sites cannot drift. The session authentication path strips NUL before normalising, like the others. The reviewer's suggestion to remove a second NUL strip in `UserDataManager` was checked and refused by the worker: the keys there are raw from `GetUserDataKeys()`, and the hash must be taken over the NUL-free key to match what the other paths store.

## Behaviour changes to be aware of

- On SQLite, a key or device id over its bound is now stored in shortened form where it used to be stored as is, and a role, item value, provider id, user name, client name or custom key over its bound is cut, dropped or refused. `UserDataSaved` events carry the normalised key.
- A filter on an item value longer than 512 characters no longer matches, because the stored value was cut and filter inputs are not. This is a documented limit (ADR-0009 L2b); the fix would be one internal helper at about ten query sites if it ever matters.
- The routine runs once per installation and does nothing when nothing is over a bound.

## Evidence

- Each commit builds alone in Release with 0 warnings and passes the Implementations and Server projects on SQLite. Debug warnings equal the base list apart from one line number moving in `SessionManager`.
- Tip, SQLite: Implementations 1773 passed, 85 skipped; Server 128/7; Integration 137/11. PostgreSQL container mode: Implementations 1804/51; Server 123/12. Baseline at `adbafb61f0`: SQLite 1610/85, 114/7, 132/11; PostgreSQL 1641/51, 109/12.
- Red/green for each rule is in `/tmp/step19/red-green.txt`. Gate verdicts are `/tmp/gate19-verdict.md` and `/tmp/gate19b-verdict.md`.

## Limits and follow-ups

- The unbounded indexed text columns are handled per engine in steps 24 and 27, not here.
- A direct SQL write that bypasses the application can still store a longer value on SQLite; the backstop only sees changes made through the context.
- The routine ID `20261013090000` must sort after the newest upstream routine when the upstream series is built (plan S9).
