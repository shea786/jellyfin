# ADR-0009: Column length limits and text sanitisation

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

- **Declared lengths.** 29 entity files carry `[MaxLength]`, for example:
  - `ActivityLog.cs:45-94`: `Name`, `Overview` and `ShortOverview` are 512; `Type` and `ItemId` are 256;
  - `ImageInfo.cs:42`: `Path` is 512;
  - `Device.cs:57-79`: `AppName` 64, `AppVersion` 32, `DeviceName` 64, `DeviceId` 256;
  - `User.cs:72-184`: 255.
- **Enforcement differs.** SQLite DDL is plain `TEXT` and ignores lengths. Npgsql emits `varchar(n)` and rejects longer values with `22001` (verified).
- **Unbounded producers:**
  - `PluginInstallationFailedLogger.cs:47` writes `Exception.Message` into `Overview`;
  - filesystem paths can reach 4096 bytes on Linux;
  - client-supplied device names and versions.
- **NUL characters.** SQLite stores U+0000. PG `text` rejects it with `22021 invalid byte sequence for encoding "UTF8": 0x00` (verified). Only `AudioFileProber.cs:637` strips it (`TruncateAtNull`, `StringExtensions.cs:148-150`). NFO, ffprobe and client strings are unguarded. A search term containing NUL would also fail as a parameter.
- **Btree entry size.** PG rejects index entries over 2704 bytes. Verified: a 3000-character incompressible value in an indexed `text COLLATE "C"` column failed with *"index row size 3016 exceeds btree version 4 maximum 2704"*. SQLite has no such limit. Indexed text columns include `BaseItems.Path`, `BaseItems.Name` (`BaseItemConfiguration.cs:36,40`), `Peoples.Name` and `ItemValues(Type, Value)`.

## Options considered

- **Length:**
  - (a) Truncate to `[MaxLength]` for every provider in `SaveChanges`. This changes SQLite and corrupts identifier-like values (`Path`, `DeviceId`).
  - (b) A PG convention that maps every string to unbounded `text`. Performance is the same as `varchar(n)`, and the semantics match SQLite.
  - (c) Both: `text`, plus truncation of display-only columns.
- **NUL:**
  - (a) A shared `SaveChanges` hook that removes U+0000 from string properties of added or modified entries.
  - (b) A shared value converter on every string. It would also cover parameters, but it risks breaking EF's string-method and LIKE translations, and costs something on every value.
  - (c) A converter on PG only, which makes stored data diverge.
  - (d) Truncating at the first NUL instead of removing it, which loses trailing content silently.

## Decision

- **Length (b).** The PG provider's `ConfigureConventions` calls `Properties<string>().HaveColumnType("text")`, alongside `UseCollation("C")` from ADR-0006. Verified: `[MaxLength(5)]` became `text COLLATE "C"`, and a 6-character write succeeded.
  - `[MaxLength]` stays as model metadata, and no provider enforces it in v1.
  - There is **no shared truncation** and no SQLite change.
  - Parity means "lengths are not enforced", which is what SQLite does today.
- **NUL (a, remove).** `JellyfinDbContext.SaveChanges`/`SaveChangesAsync` call a `SanitizeStrings()` step next to `HandleConcurrencyToken()` (`JellyfinDbContext.cs:265,291`). It strips U+0000 from every string property of `Added`/`Modified` entries, for all providers.
  - `LikePatterns` (ADR-0005) and `GetCleanValue` also strip NUL from query input.
  - Rule: user-sourced strings never go through `ExecuteUpdate` or raw SQL, because those bypass the hook.
- **Long indexed values:** accepted as a known gap for v1 (`KnownGaps` `ADR-0009/G1`). Options for later: a hash index or an `md5(...)` expression index for equality-only lookups, chosen after checking which `Path` predicates need btree order.

## Consequences

- SQLite: no schema change and no migration. The one deliberate change is that NUL characters are no longer stored. Existing rows keep theirs until they are next saved.
- PG never raises `22001`. Over-long display text (for example plugin exception messages) is stored whole, as it is on SQLite.
- MySQL (InnoDB 3072-byte keys, `TEXT` not indexable without a prefix) and SQL Server (`nvarchar(max)` not indexable) **will need lengths on indexed columns**. Their ADRs will add per-column lengths and a shared truncation policy for display columns, superseding the "no enforcement" part of this ADR.
- PG still fails on a `Path` longer than about 2.7 KB in an indexed column. It is rare (deep non-ASCII paths), and the gap ledger tracks it.

## Tests required

**(a) SQLite, in the portability PR:**

1. `SaveChanges_StripsNulFromStrings`: save a `BaseItemEntity` (`Name`, `Overview`) and an `ActivityLog` whose strings contain `\0`. They read back with the NULs removed and the trailing text kept.
2. `Search_TermWithNul_DoesNotThrow`: `SearchTerm`, `NameContains` and the `ActivityLog` filters containing `\0` return results and do not throw.
3. `OverLength_DisplayText_IsStoredWhole`: `ActivityLog.Overview` with 5000 characters and `Device.AppVersion` with 100 characters read back unchanged. This pins "not enforced".
4. `UserProfileImage_LongPath_RoundTrips`: `ImageInfo.Path` with 1000 characters.

**(b) Postgres:**

- 1-4 unchanged;
- `[PostgresFact]`: no column in the model has type `character varying`;
- `BaseItem_PathOver2704Bytes` asserts that the save succeeds and is marked `KnownGap.On(Postgres, KnownGaps.Adr0009_LongIndexedText)`.
