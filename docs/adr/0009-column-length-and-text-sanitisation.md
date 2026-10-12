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

## Amendments (multi-target re-check, 2026-10-12)

This supersedes the Consequences bullet that promised "per-column lengths and a shared truncation policy for display columns".

- **Context from the probe.** 27 of the 64 indexes contain an unbounded string:
  - four unique indexes: `ItemValues(Type, Value)`, `CustomItemDisplayPreferences(UserId, ItemId, Client, Key)`, `ApiKeys.AccessToken`, `DeviceOptions.DeviceId`;
  - three primary keys: `BaseItemProviders.ProviderId`, `PeopleBaseItemMap.Role`, `UserData.CustomDataKey`.

  Engine behaviour on such indexes:
  - MySQL rejects unbounded text in an index (error 1170) and any key over 3072 bytes.
  - MariaDB silently indexes a 768-character prefix and silently turns `UNIQUE` into `HASH`.
  - SQL Server cannot index `nvarchar(max)`. Its key limit is 1700 bytes for nonclustered indexes and 900 bytes for clustered keys (PKs are clustered by default).
  - PostgreSQL's btree limit is 2704 bytes.
- **L1. Every string in a key or index belongs to one of two classes.** A model test enforces this.
  - **Key-class** strings get a shared `[MaxLength]`.
    - Verified on EF 10.0.11: a change that only adds `MaxLength` produces **zero** SQLite migration operations, so there is no table rebuild. `HasPendingModelChanges` stays false. Existing snapshots are refreshed without a migration file.
    - Each bound fits the tightest engine. Worst-case sizes: `nvarchar` is 2 bytes per character, utf8mb4 is 4 bytes per character, and a Guid is 16 bytes.

    | Column | Max chars | Binding limit |
    |---|---|---|
    | `UserData.CustomDataKey` (PK) | 256 | SQL Server clustered key: 32 + 512 ≤ 900 bytes |
    | `PeopleBaseItemMap.Role` (PK) | 400 | SQL Server clustered key: 32 + 800 ≤ 900 bytes |
    | `BaseItemProviders.ProviderId` (PK) | 128 | natural size (provider names) |
    | `ItemValues.Value` (unique, with `int Type`) | 512 | SQL Server 4 + 1024 ≤ 1700; MySQL 4 + 2048 ≤ 3072; PostgreSQL ≤ 2704 |
    | `CustomItemDisplayPreferences.Key` (unique) | 256 | SQL Server 32 + 64 + 512 ≤ 1700 |
    | `ApiKeys.AccessToken` (unique), `Devices.AccessToken` | 128 | natural size (32-hex tokens) |
    | `DeviceOptions.DeviceId` (unique) | 256 | matches `Devices.DeviceId` (`Device.cs:78`) |
    | `BaseItems.Type` | 256 | natural size (CLR type names) |
    | `BaseItems.MediaType` | 64 | natural size (enum names) |

  - **Text-class** strings are marked with a shared annotation, `IsUnboundedIndexedText()`. Each provider decides how to store and index them (L3).
    - The text-class columns: `BaseItems.Path`, `Name`, `SortName`, `CleanName`, `SeriesName`, `PresentationUniqueKey` and `SeriesPresentationUniqueKey`; `Peoples.Name` and `NameLower` (from ADR-0005 E2); `ItemValues.CleanValue`; `BaseItemProviders.ProviderValue`; `MediaStreamInfos.Language`.
- **L2. Over-length values are handled the same way on every engine, including SQLite.**
  - **Metadata-sourced keys (`Role`, `ItemValues.Value`)** are truncated to their bound at ingest by the shared persistence code, *before* de-duplication. Uniqueness is then judged on the stored value everywhere. This is a small change on SQLite.
  - **All other key-class values** are rejected by a shared `EnforceKeyLengths()` step in `SaveChanges`, next to `SanitizeStrings()`. The exception names the entity, the property and the bound.
    - These values are server-generated or client-supplied identifiers, and real values are far below the bounds.
    - This is new on SQLite: for example, a client that sends a `DeviceId` longer than 256 characters now gets an error.
    - The step checks only added or modified values, so existing rows stay readable.
  - **Invariant:** every row that SQLite accepts can be restored on every Supported engine. The restore pre-flight (ADR-0003 S2) reports violations before anything is purged.
  - **The database never truncates silently.**
    - MySQL/MariaDB sessions use a strict `sql_mode` (ADR-0008 R3). Without it, a server in non-strict mode truncates silently.
    - MariaDB's silent prefix and HASH conversions cannot happen: the key-class bounds fit, and the text-class prefixes are explicit. A test checks `SHOW INDEX` for an explicit `Sub_part` and `Index_type` `BTREE`.
- **L3. How each provider stores text-class columns:**
  - **PostgreSQL:** `text`. The G1 gap above 2704 bytes is unchanged.
  - **MySQL/MariaDB:** `longtext`. A provider convention computes explicit prefix lengths so that each index fits in 3072 bytes, so no write ever fails because of length.
  - **SQL Server:** `nvarchar(4000)`. Index creation warns about the 1700-byte limit, and a write whose actual key exceeds it fails. That is KnownGap `ADR-0009/G2`, in the same family as G1. Values over 4000 characters also fail.
  - **Non-indexed strings** stay unbounded on every server: `text`, `longtext` or `nvarchar(max)`. "Lengths are not enforced" still holds for them, so no display-column truncation policy is needed.
- **L4. Hashed key columns rejected for now** (for example `PathHash` for equality lookups on `Path`).
  - Cost: an `AddColumn` plus a full backfill of `BaseItems` on SQLite, and every `Path` predicate (`TranslateQuery.cs:328`, `ItemPersistenceService.cs:575,661`) would have to filter on the hash as well.
  - We revisit if G1 or G2 appears in real libraries.
- **L5. Tests:**
  - `EnforceKeyLengths` rejects a value one character over its bound and accepts one at the bound.
  - Ingest truncates `Role` and `Value`, and two roles that are equal after truncation map to one row.
  - Model conformance (ADR-0011): every index fits each engine's budget.
  - SQLite `HasPendingModelChanges` stays false.
