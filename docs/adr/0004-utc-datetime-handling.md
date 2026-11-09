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

## Amendments (multi-target re-check, 2026-10-12)

- **T1. Still shared.** The shared converter (R1: no truncation) and the tolerant comparison stay shared, in `pr-plan.md` PRs 02 and 03.
- **T2. Column types are per provider:**
  - PostgreSQL: `timestamptz`.
  - MySQL/MariaDB: `datetime(6)`, never `timestamp`. `timestamp` rejects `0001-01-01` and converts time zones.
  - SQL Server: `datetime2(7)`, never `datetime`. `datetime` rejects `0001-01-01`.
  - `datetime` on MySQL and `datetime2` on SQL Server carry no zone and are never converted, so these engines need no session time-zone setting.
- **T3. Sentinels are per provider.** `DateTime.MaxValue` ends in `.9999999`.
  - **MySQL** rounds it up into year 10000 and fails (error 1441).
  - **MariaDB** truncates it, so it reads back as something other than `MaxValue`.
  - **Decision:** the MySQL/MariaDB provider adds a converter. It writes `MaxValue` as `9999-12-31 23:59:59.999999` and reads any value at or above that as `MaxValue`.
  - **SQL Server's** `datetime2(7)` round-trips `MaxValue` exactly.
  - **PostgreSQL** keeps the infinity mapping.
  - The rule "no date arithmetic on sentinels" still holds. Test 2 runs on every engine.

## Amendments (PR 02 re-sweep, 2026-10-12)

- **R4. Result of the R3 re-sweep.**
  - `BaseDynamicImageProvider.cs:299` is still the only exact equality between a stored timestamp and a file-system time. PR 03 fixes it.
  - Seven more comparisons against the `DateTime.MinValue`/`default` sentinel were found. They are harmless on every provider, because `Utc`-kind `MinValue`/`MaxValue` round-trip exactly (test a2). A sentinel written with `Unspecified` or `Local` kind is shifted by the zone offset on write, exactly as it was before PR 02, so this is not a regression:
    - `Folder.cs:239`
    - `BaseItemMapper.cs:307,310`
    - `MetadataService.cs:95`
    - `ChannelManager.cs:1187`
    - `PeopleValidator.cs:76`
    - `ArtistsValidator.cs:87`
  - Line numbers are on `multidb/02-shared-utc-converter`. Some of the R1 references have shifted since R1 was written.

## Amendments (PR 02 review, 2026-10-12)

- **R5. Only `Properties<DateTime>()` is registered.** This supersedes the Decision text that names both `Properties<DateTime>()` and `Properties<DateTime?>()`.
  - A `Properties<DateTime>()` convention already applies to `DateTime?` properties.
  - Registering `DateTime?` separately is harmful. A provider that overrides only `Properties<DateTime>()`, such as the MySQL `MaxValue` converter (T3), would then change the non-nullable properties and leave the nullable ones on the shared converter.
  - Provider overrides register `Properties<DateTime>()` only.
- **R6. What test a1 proves depends on the machine's time zone.**
  - On a machine whose local zone is UTC, which includes GitHub runners, `Local` and `Unspecified` values already equal their UTC value. There a1 only proves that values are read back with `Kind == Utc`.
  - The `ToUniversalTime()` step is only exercised under a non-UTC zone. It was verified with `TZ=America/New_York` and `Europe/London`.
  - Adding a CI job per zone is not justified for one converter.

## Amendments (step 03 review, 2026-10-12)

- **R7. `EqualsToMicrosecond` treats timestamps at most 10 ticks apart as equal, inclusive.**
  - It compares ticks and ignores `Kind`, like `DateTime.Equals`.
  - 10 ticks is the same tolerance that test a1 uses.
  - A database that truncates to the microsecond differs from the written value by 0 to 9 ticks. One that rounds differs by at most 5. A real file change is many orders of magnitude larger.
- **R8. Consequence (ii) is wrong and is withdrawn. On SQLite, the year-only sort key never depended on the server's time zone.**
  - Verified by capturing the generated SQL under `America/New_York`, `Asia/Tokyo` and `UTC`.
    - Before step 03, every zone produced the same literal: `strftime(..., '0001-01-01 00:00:00', CAST("ProductionYear" - 1 AS TEXT) || ' years')`.
    - After step 03, every zone produced `'2000-01-01 00:00:00'` and `- 2000`.
  - The reason is that EF inlines the `DateTime.MinValue` constant as a literal inside the `strftime` translation, and the value converter is not applied to it.
    - The step 03 worker's explanation was that `ToUniversalTime()` clamps `MinValue`. That is wrong.
    - Under `America/New_York`, `DateTime.MinValue.ToUniversalTime()` is `0001-01-01T04:57:00Z`, the zone's local mean time offset. It never reaches the query.
  - The worker evaluated the old and new expressions in SQLite for every year from -3000 to 20000. All 23,001 keys are identical, including NULL for years from 10000 up.
  - So step 03 changes nothing on SQLite. Its only purpose is portability: Npgsql maps `DateTime.MinValue` to `-infinity`, and `-infinity` plus an interval stays `-infinity`, so every year-only item would tie. That is the Sentinels point in Context. The commit message and the explainer must say so, and must not claim a SQLite fix.
- **R9. Follow upstream's one-second tolerance. This supersedes R7 and the "Tolerant comparison" decision (`DateTimeExtensions.EqualsToMicrosecond`).**
  - Upstream already fixed the same problem. `a2bd48c1d2` is the backport of upstream #18278, for issue #18274, and is in our base `61f9a9df23`. Its comment reads: "Stores such as PostgreSQL keep only microseconds, so an exact comparison would treat every unchanged image as modified."
  - #18278 compares image dates as `Math.Abs((a - b).TotalSeconds) > 1` in `BaseItem.cs:2565` and `ItemImageProvider.cs:468`. `BaseItemExtensions.HasChanged` (`:129`) and `LibraryManager.cs:2543` already use the same one-second rule.
  - Step 03 drops the `EqualsToMicrosecond` helper and its tests. `BaseDynamicImageProvider.cs:300` uses the #18278 shape and comment style, so every stored-versus-file-system image date comparison follows one rule. That is consistent with code the maintainers have already merged, and adds no public API.
  - Test a4 (the helper's unit tests) is dropped. Test a5 covers the one-second boundary: exactly 1 s is unchanged, 1 s plus 1 tick is changed.
  - R2's phrase "through the same helper" no longer applies. Test a1 compares with the assertion's own one-microsecond tolerance, as shipped in step 02.
  - The step 03 gate found this. Before relying on our own design, check what upstream has merged; see the upstream scan amendment.
- **R10. Addition to R4.** `20250420230000_RefreshInternalDateModified.cs:103` compares `writeTime != itemModificationTime` exactly. It is a one-shot migration routine: on a microsecond store it refreshes a few extra rows once, and it is harmless. It is left unchanged and mentioned in the step 03 explainer.
- **R11. Test a3 pins "start of the year".** It seeds dated neighbours at 2009-12-31 23:59:59Z and 2010-01-01 00:00:01Z around the year-only 2010 item. A base date other than 1 January then fails it, which the step 03 gate showed the first version did not catch.

## Amendments (upstream reconciliation, 2026-10-12)

- **U1.** The `DateTime.MinValue` to `null` sentinel convention comes from upstream #14347. Our sentinel rules (R4, Sentinels) build on it. #18278 is handled in R9.
- **U2. Other date-only producers are follow-up work, not part of the series.** The step 04 worker found the same kind of date shift in other producers. These are existing bugs that users can see on SQLite. The shared converter handles them the same way on every provider, so they are not a parity problem, and fixing them in the series would be an unrelated change. They stay out of the series unless one of them behaves differently on a server engine. Line numbers are at step 03, `bb1f10913b`.
  - **Local converted to UTC, which moves the date on servers east of UTC:**
    - `LibraryManager.cs:3360`, `new DateTime(y, m, d).ToUniversalTime()`;
    - `TmdbEpisodeProvider.cs:186` and `TmdbMissingEpisodeProvider.cs:571`, `SpecifyKind(Local).ToUniversalTime()`;
    - `TmdbMovieProvider.cs:79`, `TmdbPersonProvider.cs:127`, `TmdbSeriesProvider.cs:149,166`, `.ToUniversalTime()`.
  - **Kind left `Unspecified`:** `AudioFileProber.cs:403`, `OmdbItemProvider.cs:199-201`, `OpfReader.cs:131-134`.
  - **Kind not checked:**
    - TMDb: Movie `:141` and `:246`, Series `:305`, Season `:79`;
    - MusicBrainz: Album `:136` and `:285`, Artist `:103` and `:149`;
    - `PhotoProvider.cs:112`, `AudioFileProber.cs:390`.
  - **Added by the X2 fix:** `ProbeResultNormalizer.cs:1658` now parses the `WM/MediaOriginalBroadcastDateTime` value with `AdjustToUniversal`. With no `Assume*` flag, .NET does not convert a value that has no zone designator: it comes back unchanged with `Kind == Unspecified` (checked with `/tmp/tzprobe` under Asia/Tokyo, America/New_York and UTC). The shared converter then treats it as server-local time on write, which is the same east-of-UTC shift as step 04. Before X2 the broadcast date never reached `PremiereDate`, so this path is newly reachable. A value with a `Z` or an offset gives `Kind == Utc` and is not affected. X2 deliberately keeps upstream's parse style and fixes only the wrong variable. Whether such a value is a calendar date or a local time depends on the source (WTV recordings), so it is checked with the others.
  - Before anything is fixed, each needs checking against what its source returns. A TMDb date is a calendar date, but EXIF and audio tags can carry a real time.
- **U3. A separate upstream bug, not ours to fix in the series.**
  - `ProbeResultNormalizer.cs:1658` checks for the `WM/MediaOriginalBroadcastDateTime` tag, then parses `year` instead of `premiereDateString`. So the broadcast date is never used: a bare year such as "2010" fails `DateTime.TryParse`.
  - History: the wrong variable is already in the Emby code (`2b7a80cfb5`, 2015-04-04). The refactor `11a5551218` (2021-07-09) folded the tag lookup into the condition and carried `year` over. `653df7d8e5` (2021-09-21) changed `DateTimeStyles.None` to `AdjustToUniversal`, and `f8ffccae7f` (2026-07-04) changed only the culture argument. Neither introduced the bug.
  - No upstream PR or issue mentions the tag. It is a candidate for a small standalone upstream fix, like X1, and the user decides.
  - Superseded by the user's ruling: it is fixed as unit X2 (`pr-plan.md` S10).

## Amendments (step 02b, 2026-10-14)

- **R11. Supersedes the sentinel part of R4: date sentinels no longer depend on the server's time zone.** The step 24 gate found the MySQL clamp converter losing `DateTime.MaxValue` under `TZ=Asia/Kolkata`. The same check on the shared converter showed that R4's "shifted on write, exactly as before PR 02" was a real bug, inherited from upstream's SQLite `DateTimeKindValueConverter`:
  - `MinValue` and `MaxValue` have `Unspecified` kind, so `ToUniversalTime()` treated them as local time.
  - `MinValue` moved by the zone's year-1 local mean time: about one minute in Europe/London, which includes this machine, and 4h57m in America/New_York. `MaxValue` moved by the current offset: 5h30m in Kolkata, 13h in Auckland.
  - On PostgreSQL a shifted `MinValue` was a finite `0001-01-01 04:57:00+00` instead of `-infinity`.
- **Decision (step 02b).** `UtcDateTimeConverter.ToStoredUtc` (internal) keeps the ticks of `MinValue` and `MaxValue` and labels them UTC whatever their kind. Every other value goes through `ToUniversalTime()` as before. On PostgreSQL the sentinels now become `-infinity`/`infinity` and sort first and last.
  - **Scope.** 0 ticks and `9999-12-31 23:59:59.9999999` are never real local times in this code base. They appear only as sentinels or as the result of clamping, where keeping them is the desired result. One older edge stays: east of UTC, a local value within the offset of `MinValue` is clamped to `MinValue` on the way in.
  - **Queries.** Checked on SQLite and PostgreSQL under New York and Kolkata: saves, reads, closure parameters, inline `DateTime.MinValue` literals (EF 10 inlines them through the column mapping; PostgreSQL gets `TIMESTAMPTZ '-infinity'`) and `??` sentinels inside `Min`/`Max` all go through the converter. `ExecuteUpdate` `SetProperty` uses the same parameter path but was not probed directly. Constants inside function arguments with no column mapping are still not converted (R8); no production query has one.
  - **Regression guard.** CI runs in UTC, where the old code passes. The guard tests set `TZ` in-process on Linux, call `TimeZoneInfo.ClearCachedData()` and restore both, in an xUnit collection that runs alone. America/New_York exercises `MinValue`, and Asia/Kolkata exercises `MaxValue`. With the old converter they fail 8 of 46 under `TZ=UTC` on SQLite and PostgreSQL. This guard ships in the upstream series, not only in the fork's CI.
- **Rows already stored shifted are not repaired.** A repair would have to guess the server's offset at the time of each write. Readers that treat a sentinel as "unset" are protected by `BaseItemMapper.cs:308-312,422`, which turns a `MinValue` into `null` before it is stored. The inventory of columns that can hold a sentinel, adding those R4 missed:

  | Column | Source of a sentinel | Reader that treats it as unset |
  |---|---|---|
  | BaseItems `DateCreated`, `DateModified`, `DateLastRefreshed`, `DateLastSaved`, `DateLastMediaAdded` | Mapped to `null`; `FixDates` also nulled year-1 values | Unaffected, the sentinel is never stored |
  | BaseItems `PremiereDate` | Client or legacy input | `MetadataService.cs:557`: a shifted child date passes `> MinValue` and can give the parent a year-1 premiere date. Existing rows only; rare |
  | BaseItems `StartDate`, `EndDate` | XMLTV, already UTC | None |
  | Chapters `ImageDateModified` | `ChapterRepository.cs:102`, `default` when a chapter has no image | None: the tag is computed only when `ImagePath` is set |
  | BaseItemImageInfos `DateModified` | Remote images left at `default` (`ItemImageProvider.cs:230`, `LibraryManager.cs:3918`, `GuideManager.cs:735`) | Only the image cache tag; old rows keep their old tag |
  | ApiKeys `DateLastActivity` | Never set by the `ApiKey` constructor | Never read |
  | Devices `DateCreated`, `DateLastActivity` | `MigrateAuthenticationDb.cs:84-94` on a failed parse | Sorting and the activity refresh only |
  | UserData `LastPlayedDate` | Client-supplied | Compared in memory with `?? MinValue` only |

  Other "unset" checks R4 did not list read `null`-mapped columns or in-memory values: `MetadataService.cs:572,732,1381,1386`, `BaseItem.cs:1723`, `Folder.cs:244`, `SessionManager.cs:267`, `VersionPlaybackSelector.cs:47`, `VersionResumeData.cs:27`.
- **Compiled models.** `ToStoredUtc` is internal. If compiled models are ever enabled, the assembly that holds the generated model needs `InternalsVisibleTo`. `dotnet ef dbcontext optimize` fails today anyway, because of the custom `LikeIgnoreAsciiCase` translation.
