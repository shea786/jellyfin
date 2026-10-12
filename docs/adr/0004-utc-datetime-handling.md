# ADR-0004: UTC date/time storage, precision and sentinel values

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

- Only SQLite normalises `DateTime`. `SqliteDatabaseProvider.OnModelCreating` (`SqliteDatabaseProvider.cs:142-145`) calls `SetDefaultDateTimeKind` (`Providers.Sqlite/ModelBuilderExtensions.cs:42-46`), which installs `DateTimeKindValueConverter` (`ValueConverters/DateTimeKindValueConverter.cs:16-19`). On write it calls `v.ToUniversalTime()`, so an `Unspecified` value is treated as **server-local**. On read it calls `SpecifyKind(Utc)`.
- Npgsql maps `DateTime` to `timestamptz` and refuses any `Kind` other than `Utc`. We verified this with EF 10.0.11, Npgsql 10 and PG 17: *"Cannot write DateTime with Kind=Unspecified to PostgreSQL type 'timestamp with time zone'"*.
- Shared code produces `Unspecified` values. `ComicInfoReader.cs:214` and `ComicBookInfoProvider.cs:238` build calendar dates with `DateTimeKind.Unspecified`.
- **Precision.** PG stores microseconds; SQLite stores 100 ns ticks as text. In a verified round trip, ticks `...0001` came back as `...0000` on PG and unchanged on SQLite. `BaseDynamicImageProvider.cs:299` compares a stored `DateModified` with the file mtime using `!=`, so on PG every collage looks changed and is regenerated on every refresh.
- **Sentinels.** By default Npgsql maps `DateTime.MinValue`/`MaxValue` to `-infinity`/`infinity`. Both round-trip exactly (verified), but date arithmetic on them does not. `OrderMapper.cs:69` sorts by `DateTime.MinValue.AddYears(ProductionYear - 1)`, which PG evaluates as `-infinity + interval = -infinity`. Every year-only item then ties. Verified: PG returned 2021 before 1999. `BaseItemMapper.cs:116-120,307-311` uses `MinValue` as the "unset" value.
- The `DateTimeOffset` properties (`Entities/Libraries/Series.cs:29`, `ItemMetadata.cs:100`) are not in the model (they are absent from the SQLite snapshot), so they are out of scope.

## Options considered

1. **Column type.** (a) `timestamptz`, the Npgsql default. (b) `timestamp without time zone`: this needs the legacy Npgsql switch or a column type on every property, and it hides the UTC contract.
2. **Where to normalise.** (a) Copy the converter into each provider. (b) One shared convention applied by `JellyfinDbContext.ConfigureConventions` (`JellyfinDbContext.cs:336-340`). (c) A `SaveChanges` interceptor: it would miss query parameters and `ExecuteUpdate`.
3. **Meaning of `Unspecified`.** (a) Keep `ToUniversalTime()` (local time). (b) Treat it as UTC. (c) Throw.
4. **Precision.** (a) Leave as is. (b) Truncate to whole microseconds on write for every provider. (c) Only make comparisons tolerant.
5. **Sentinels.** (a) Keep Npgsql's infinity mapping and remove arithmetic on sentinels. (b) Disable infinity conversions. That is a process-global AppContext switch, and `MaxValue` would then be truncated so it no longer reads back as `MaxValue`.

## Decision

- PG uses **`timestamptz`**. The PG provider adds `Timezone=UTC` to the connection string, so server-side date functions are deterministic.
- **One shared converter** (2b), `UtcDateTimeConverter` in `Jellyfin.Database.Implementations`. `JellyfinDbContext.ConfigureConventions` registers it with `Properties<DateTime>()` and `Properties<DateTime?>().HaveConversion<…>()`. The `SetDefaultDateTimeKind` call is removed from the SQLite provider.
  - Write: `ToUniversalTime()` (unchanged, option 3a), then **truncate to whole µs** (4b). `MinValue` and `MaxValue` pass through untouched as `Utc`.
  - Read: `SpecifyKind(Utc)` (unchanged).
- **Producers.** `ComicInfoReader` and `ComicBookInfoProvider` now emit `DateTimeKind.Utc` for calendar dates. This ships as a separate PR.
- **Tolerant comparison.** `BaseDynamicImageProvider.cs:299` compares through a shared helper, `DateTimeExtensions.EqualsToMicrosecond`.
- **Sentinels (5a).** Npgsql defaults stay. `OrderMapper.cs:69` becomes `new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddYears(ProductionYear - 2000)`. It yields the same dates, and the probe gave the same order on both engines. Rule: shared queries never do date arithmetic on `MinValue`/`MaxValue`.

## Consequences

- SQLite has **no migration**, because converters are not part of the snapshot. Its visible behaviour is unchanged except for three deliberate changes:
  - (i) New writes lose their sub-µs digits.
  - (ii) On servers west of UTC, the sort key for year-only premieres moves from Jan 1 0X:00 to 00:00. The old `Unspecified` constant was shifted by `ToUniversalTime()`.
  - (iii) Comic dates are no longer shifted by the server offset.
- `Unspecified` still means local time. That is a correctness trap, but not a parity one, because every provider stores the same value.
- The converter also applies to parameters, so `>=` filters can include values up to 1 µs earlier.
- MySQL `datetime(6)` is also µs. SQL Server `datetime2(7)` could keep ticks, but truncating keeps every provider equal.

## Tests required

**(a) SQLite, shipped in the portability PRs (derive from `SqliteDbTestFixture`):**

1. `DateTime_RoundTrip_IsUtcAndMicrosecondTruncated`: write `Utc`, `Local` and `Unspecified` values with `ticks % 10 != 0` to `DateCreated` and `PremiereDate`. Assert `Kind == Utc` and value == `ToUniversalTime(input)` truncated to µs.
2. `DateTime_Sentinels_RoundTripExactly`: `MinValue`/`MaxValue` read back equal to themselves.
3. `OrderBy_PremiereDate_YearOnlyItems`: seed an item with a full date (including Jan 1 of a year), year-only 1999 and 2021 items, and an item with neither. Assert the exact ascending and descending order.
4. `MinDateLastSaved_BoundaryWithSubMicrosecondParameter`: the boundary item is returned.
5. `HasChangedByDate_IgnoresSubMicrosecondDifference` (Emby tests). Also `ComicInfo`/`ComicBookInfo` parsers return `Kind == Utc` with the calendar date preserved (Providers tests).

**(b) Postgres re-run:** tests 1-5 unchanged. Plus these `[PostgresFact]` tests:

- every `DateTime` column is `timestamp with time zone`;
- `SHOW TimeZone` returns `UTC` on a provider connection.

## Amendments (orchestrator review, 2026-10-12)

- **R1. No microsecond truncation on write. This supersedes Decision item 4(b). Comparisons become tolerant instead (4c), and SQLite's stored values do not change.**
  - **Why.** Truncating on every provider would change the values written on existing SQLite installs, which gains SQLite nothing and is a harder change to justify upstream.
  - **Equality sweep.** Only `BaseDynamicImageProvider.cs:299` compares a stored timestamp with a file-system time for equality. The other `DateModified ==` comparisons are against the `DateTime.MinValue` sentinel (`Folder.cs:244`, `BaseItem.cs:1723`, `MetadataService.cs:1386`, `BaseItemMapper.cs:308,311`). The shared helper `DateTimeExtensions.EqualsToMicrosecond` fixes `:299` on every provider.
  - **Range filters** (`TranslateQuery.cs:359,364`) are unaffected on SQLite. Postgres rounds sub-microsecond parameters itself, and test 4 pins the boundary.
  - **Engines that keep finer precision.** MySQL stores `datetime(6)`, which is microseconds. SQL Server stores `datetime2(7)`, which keeps ticks. Neither breaks the tolerant comparison.
- **R2. Test 1 asserts microsecond equality instead.** It asserts `Kind == Utc` and that the value equals the input, compared to the microsecond, through the same helper. It does **not** assert exact tick equality.
- **R3. Re-sweep before the shared-UTC PR is marked ready.** The sweep for stored-vs-file-system time equality is repeated, and the results go in the PR explainer.
