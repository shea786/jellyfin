# Explainer: step 27a, MySQL-family hardening

These are fork-internal study notes, not upstream PR text. They cover the five parts of 27a merged into `feature/multi-db` by 2026-10-16, each fast-forwarded by the orchestrator. 27a's planned scope is complete; the remaining follow-ups are at the end, and the next step is SQL Server (ADR-0001 E2-E3).

| Part | Branch | Commits on `feature/multi-db` | Evidence |
|---|---|---|---|
| Isolation | `multidb/27a-mysql-isolation` | `d429d74b10`, `04fde51282`, `399af59770` (on `09cd63aca8`) | `/tmp/27a-iso/` |
| Collation | `multidb/27a-mysql-collation` | `f7edd3c017`, `a0bf39394e` | `/tmp/27a-coll/` |
| Kit and `EngineTraits` | `multidb/27a-kit-mysql` | `c380ab7d19` to `a6cd869ce7`, 8 commits | `/tmp/27a-kit/` |
| FolderDelete deterministic | `multidb/27a-folderdelete-deterministic` | `2f1f16c85f` (cherry-pick of `09c0b6d72e`) | `/tmp/27a-fd/` |
| The database-ban rule | `multidb/27a-db-ban-analyzer` | `1d22afd2b3`, `a142db1590` | `/tmp/27a-ban/` |

**Backups, kept:**
- isolation: `-backup-a` `a4e74823c2`, `-backup-b` `a8f9ba5265`, `-backup-c` `e2d1f94cfc`;
- collation: `-backup-a` `b990322cf1`;
- kit: `-backup-a` `bdc451f0d2`, `-backup-b` `d8830302fb`, `-backup-c` `53e8a39c96`, `-backup-d` `c951a1973f`.

**Decisions.** The code commits record their own: ADR-0008 S5 and S6 (isolation and the 1020 retry), and ADR-0006 C8 (collation). This explainer does not repeat them. See also pr-plan row 27a, B30 and B31.

## 1. Isolation

**Root cause** (step 26 finding 1). MySqlConnector 2.6.2, which Microting 10.0.12 depends on, maps `IsolationLevel.Unspecified` to an explicit REPEATABLE READ and sends it before `start transaction`. Every transaction Jellyfin or EF Core starts without a level therefore ran at REPEATABLE READ. That overrode the READ COMMITTED the session interceptor sets, for the transaction and for the rest of the connection.

**The commits.**
- **`d429d74b10`.** Adds `ReadCommittedConnection` (`ReadCommittedConnection.cs:29`), a subclass of Microting's `MySqlRelationalConnection`. It is registered with `ReplaceService<IMySqlRelationalConnection, ReadCommittedConnection>()` (`MySqlDatabaseProvider.cs:139`). It turns `Unspecified` into `ReadCommitted` in `BeginTransaction` and `BeginTransactionAsync`, and keeps a level that is named.
- **`04fde51282`.** MariaDB error 1020 (`CheckRead`) is now a retryable write conflict, as defence in depth (`MySqlDatabaseProvider.cs:203`).
- **`399af59770`.** Docs, including ADR-0008 S5 and S6.

**Why not a `DbTransactionInterceptor`.**
- The isolation level in the event is read-only.
- The provider's interceptors run before the locking behaviour's.
- `OptimisticLockBehavior` calls `BeginTransaction` itself, so a transaction started in an interceptor would be nested.

A design probe confirmed it (`/tmp/27a-iso/red3/design-interceptor-only-*`). It ran on `09cd63aca8` with an uncommitted interceptor and `TreatWarningsAsErrors=false`. Exactly the two Optimistic rows failed, with "Transactions may not be nested".

**Results.**
- **Before.** `LibraryStructureControllerTests`, 10 runs per server, failed 7 of 10 times on MariaDB 12.3.3 and 5 of 10 on 11.8.9, all 12 failures with 1020.
- **After.** 0 failures in 20 runs on each server.
- **Red test.** It fails 16 of 16 at the base.

**Which commit each run used.** Most isolation runs used commits that are now kept only on backup branches. Their runtime code is identical to the final commits; they differ only in comments, docs and a few unit-test lines.

| Run | Commit |
|---|---|
| Proof, Debug comparison, advisory at the tip | `e2d1f94cfc` (backup-c) |
| After-runs of the rename | `2a3e5eadef`, `a8f9ba5265`, `e2d1f94cfc` |
| Advisory after C2 | `a8f9ba5265` |
| Per-commit Release gates | the final `d429d74b10`, `04fde51282`, `399af59770` |

**Proof** (`/tmp/27a-iso/proof/`, at `e2d1f94cfc`). 14/14 gates rc 0. 11,553 TRX rows: 10,896 passed, 657 skipped, none failed. I recounted them.

**Debug.** The comparison ran at the tip only, `e2d1f94cfc`: 211 against 211, empty diff. `399af59770` itself was never built in Debug.

**Advisory.** 0 failures.

**Review** (relayed). An independent APPROVE. Review also removed a fork id from the C1 body and corrected a CI over-claim in C3.

**CI** (relayed). Green at `399af59770` on every job, including the advisory MariaDB 12.3.3.

## 2. Collation

**What was wrong** (step 26 finding 3, ADR-0006 C8).
- `KeyframeData.KeyframeTicks` had no collation.
- `AlterDatabase` set only the character set.
- Microting's history repository left `__EFMigrationsHistory` at the server's insensitive default.

**The commits.**
- **`f7edd3c017`.**
  - `TextCollationConvention` (`TextCollationConvention.cs:32`), a model-finalizing convention, selects every column whose store type is text and calls `UseCollation` per property.
  - The database and table defaults come from `HasCharSet(…, DelegationModes.ApplyToAll)` and `UseCollation(…, DelegationModes.ApplyToAll)` in `MySqlDatabaseProvider.OnModelCreating` (`:171-172`).
  - `BinaryCollationHistoryRepository` replaces the history repository (`MySqlDatabaseProvider.cs:140`).
  - The MySQL baseline is regenerated against an empty snapshot, because EF Core sets the `AlterDatabase` collation only in `Diff(source, target)`. Afterwards all 102 text columns and the 31 tables are binary.
- **`a0bf39394e`.** Docs, including ADR-0006 C8.

**22c allowlist.** It now has 18 entries (7 `BaselineCoverage`, 5 `EngineSpecific`, 6 `EquivalentSql`). One reason changed, for the MySQL `AlterDatabase` in Up. The MySQL `AlterDatabase` in Down was added.

**No upgrade path.** Earlier fork-build MySQL and MariaDB databases are recreated, not migrated. The rule is ADR-0006 C8 (`0006-string-collation-and-null-ordering.md:124`, `:140`): the MySQL family is unreleased, so the baseline may still be regenerated.

**Results.** At the base, 14 of 22 rows fail on each server; with the fix, 39 of 39 pass.

**Which commit each run used.**
- Proof, advisory, migration checks, red and green ran on the pre-fold pair `552c84018d` / `b990322cf1`.
- Only the per-commit fold gates and the Debug build ran on the merged pair `f7edd3c017` / `a0bf39394e`.
- The two pairs differ only in a comment and the docs (`/tmp/27a-coll/fold/range-diff.txt`).

**Proof** (`/tmp/27a-coll/proof/`, at `b990322cf1`). 14/14 gates rc 0. 11,681 TRX rows: 11,022 passed, 659 skipped, none failed. I recounted them.

**Debug.** 211 against 211, empty diff, at the stack tip only.

**Other checks.** Migration checks 48/48. Advisory: 0 failures.

**Review** (relayed). An independent APPROVE. It added the recreate note, the local-run labels and a C1 comment fix.

**CI** (relayed). Green at `a0bf39394e` on every job.

## 3. The kit on MySQL and MariaDB, and `EngineTraits`

**The chain, on `a0bf39394e`:**

| Commit | What it does |
|---|---|
| `c380ab7d19` | Moves `OfflineContexts` and `CollectingLogger` out of the PostgreSQL test namespace. |
| `fa406640fb` | Introduces `EngineTraits` (`tests/Jellyfin.Database.Testing/EngineTraits.cs`). Its first traits: `Provider`, `ProviderType`, `CoversThroughSqliteMigration`, `IsEmbedded`, `AssertUniqueViolation` (exact exception type) and `Pool`. It replaces the engine switches in `ProviderRequirement`, `JellyfinApplicationFactory`, `BackupServiceTests`, `PoolChecks` and `ModelRows`. |
| `1249673827` | The pool adds the migration history to every pooled database's fingerprint itself. This deliberately changes the SQLite-file pool: a database whose history alone changed is now dropped. |
| `ddd3d24b54` | A flavour sentinel (`ServerMarker`): backend sentinels fail on a server of the other MySQL-family engine. |
| `3790fe154e` | The kit runs on MySQL and MariaDB. Adds the capability trait `SupportsPartialIndexes`. |
| `919a1163bf` | Escapes `[` in LIKE patterns, with 7 bracket cases. |
| `dd8c5d0d75` | States the purge contract. Adds the trait `PurgeResetsIdentityInATransaction` and a kit row, and corrects the restore-counter wording. |
| `a6cd869ce7` | Per-version collation tests. |

**Decisions and fixes along the way.**
- **Partial indexes.** The coordinator ruled that `SupportsPartialIndexes` is a capability trait, not a known gap. The matcher is narrow: on MySQL and MariaDB, 4 non-unique filtered indexes are full indexes.
- **Purge over-reset.** Found in review and fixed. With the break, the old tip passed and the new tip fails (Expected 3, Actual 1) (`/tmp/27a-kit/red-overreset/`).
- **The purge contract.** Identities are reset, except inside a transaction on MySQL and MariaDB.
- **CA1034.** The Debug build at the then-tip `c951a1973f` (`/tmp/27a-kit/run2-superseded/`, now backup-d) failed on a nested public type in `EngineTraits`. The chain was rewritten, and the fix folded into `fa406640fb` (`review-r4/range-diff.txt`). That slip is why the gate-plan rule below exists.

**Kit size.** This counts the `*ConformanceTests` classes; a "Conformance" substring match gives 70, because it also picks up 6 `ModelShapeTests`.
- 64 rows per engine.
- SQLite: 62 passed and 2 skipped by its known gap `SQLITE-CONTAINS-PARAMETER-LIMIT`.
- PostgreSQL, MySQL and MariaDB: 64 of 64.

**Red controls** (`/tmp/27a-kit/red`, `red-fix`, `red-fix2`, `red-overreset`).
- **The first red run returned rc 1.** The writeconflict, datetime and MariaDB folderdelete legs failed their audit because the expected specs were wrong; for example, MariaDB's error 193 message has no "depth".
- **The reruns.** `red-fix` reran those legs with corrected specs, and each was caught. `red-fix2` reran purge-rows, whose files had been overwritten.
- **Overall.** Every row family was caught only across the three runs together, and folderdelete only with repeats (see the open items).

**Flavour controls.** 24 of 24 as expected (`/tmp/27a-kit/flavour/`): each of MySQL and MariaDB selected, on each of a MySQL and a MariaDB server, at the base and at the tip, for each of the three test projects.

**Proof** (`/tmp/27a-kit/proof/`, at `a6cd869ce7`). 14/14 gates rc 0.

| Backend | Implementations passed/skipped | Server.Tests passed/skipped | Integration passed/skipped |
|---|---:|---:|---:|
| SQLite 3.53.3 | 2582/123 | 157/9 | 137/11 |
| PostgreSQL 16.15 | 2616/85 | 147/18 | 144/6 |
| MySQL 8.4.11 | 2564/135 | 127/38 | 144/6 |
| MariaDB 11.4.13 | 2565/134 | 127/38 | 144/6 |

12,063 TRX rows: 11,454 passed, 609 skipped, none failed. I recounted them, and the orchestrator recounted them independently.

**Advisory.** MySQL 9.7.2 and MariaDB 12.3.3 both passed with 0 failures (`/tmp/27a-kit/advisory/`).

**Per-commit gates** (`/tmp/27a-kit/percommit/results.tsv`). 120 of 120: 15 gates on each of the 8 commits.
- **The gates.** Release, format, `render --check`, and the 12 provider legs.
- **Debug was built for every kit commit**, outside the per-commit gate table:
  - six with a recorded commit identity, each 0 errors and 211 warnings, with a warning set equal to the base's:
    - `c380ab7d19` (`/tmp/27a-kit/debug-c1`);
    - `1249673827`, `ddd3d24b54`, `3790fe154e`, `919a1163bf` and `dd8c5d0d75` (`/tmp/27a-kit/dev/debug-each/results.tsv`);
  - the tip `a6cd869ce7` in its own run (`/tmp/27a-kit/debug`), 211 against 211 with `a0bf39394e`, empty diff;
  - for `fa406640fb`, a Debug log exists without a recorded commit identity (`/tmp/27a-kit/dev/d2-debug.log`: rc 0, 0 errors, 211 warnings).

**Review** (relayed through the orchestrator; only range-diffs are kept). An independent APPROVE after two rounds.

**Process.** One `git restore --source=HEAD` was used on the worker's own scratch file. It was self-reported and harmless.

## Gate-plan rule from here on

Every commit's gate includes a Debug build: 0 errors, and a warning set no larger than the base's, not only at the tip.
- **Why.** The analyzers that fail the build run in Debug only, as the CA1034 slip showed.
  - CI's test and database jobs build Release (`ci-tests.yml:31`, `ci-tests-db.yml:85`), so they do not run those analyzers.
  - In CI the Debug-only analyzers are enforced only by the ABI job's `dotnet build Jellyfin.Server` (`ci-compat.yml:26`), which runs on pull requests.
  - The local per-commit Debug build is therefore the only per-commit check. (This corrects the first version of this note, which said CI's `dotnet test` builds Debug.)
- **Scope.** It applies to the rest of 27a, to SQL Server, and to the final linear replay.
- **Before the rule.** The isolation and collation parts were built in Debug only at their stack tips.

## 4. FolderDelete made deterministic

**The commits.** The worker's commit is `09c0b6d72e`. The orchestrator cherry-picked it onto `2c95aa2622` as `2f1f16c85f`. Outside the two 27a docs files, the tree of `2f1f16c85f` is byte-identical to that of `09c0b6d72e`: the non-docs tree hash and the patch-id are the same. Every after-fix run used `09c0b6d72e`; red-old and the Debug base used `a6cd869ce7`. No run used `2f1f16c85f`.

**Why the row was a coin toss.** The commit body says the server deletes the chain's rows in "the order of their key", which made the row catch a removed pre-clear "about one run in four".
- **Key order.** On MySQL and MariaDB a Guid is stored as big-endian `binary(16)` (ADR-0007, `0007-guid-aggregates-and-unsupported-translations.md:85`), so key order is the byte order of the id. The random Guids put the root anywhere in that order.
- **Probability.** The simulation `/tmp/27a-fd/scratch/sim.py` gives 0.265 per run, and 0.046 for ten passes in a row.
- **Observed.** The old row failed 3 of 20 runs (15%).

**The fix, in the kit only.** No production code changed.
- **The ids.** The chain's ids share one random prefix, and the last byte is the depth, so the ids rise from the root.
- **The guard.** It reads the ids back with `ORDER BY Id` on every engine.

**Results** (`/tmp/27a-fd/`, from a TRX recount):

| Run | Result |
|---|---|
| Pre-clear removed, new row (`red-new`) | 20 of 20 failed, 10 of 10 on each of MySQL 8.4.11 and MariaDB 11.4.13 |
| Pre-clear removed, old row (`red-old`) | 3 of 20 failed: 1 of 10 and 2 of 10 |
| Descending ids with the break (`contrast`) | 20 of 20 passed |
| The guard | fired on all 4 engines |
| Green | 40 of 40, 10 runs on each engine |
| Proof | 11,454 passed, 609 skipped, 0 failed |
| Per-commit gates | 15 of 15 |
| Debug, as its own leg | 211 warnings on each side, empty diff |

## 5. The dedicated rule for the database ban, JF0002

**The commits**, on `2f1f16c85f`:
- **`1d22afd2b3`.** Adds `DatabaseBannedSymbolsAnalyzer` (`src/Jellyfin.CodeAnalysis/DatabaseBannedSymbolsAnalyzer.cs`) with two rules:
  - JF0002 reports a use of a banned member;
  - JF0003 reports a list entry that names no member, only when its containing type resolves.

  Both went straight into `AnalyzerReleases.Shipped.md` under Release 1.1; the repository has no Unshipped file.
- **`a142db1590`.** Renames the list to `DatabaseBannedSymbols.txt`, so that `RS0030` no longer reads it.
  - **Suppressions.** The 7 production suppressions move to JF0002, with their reasons: `BackupService` (2), `JellyfinMigrationService` (3), `MigrateActivityLogDb` and `StripEmbeddedLinkedChildren`.
  - **One exception.** `MigrateLibraryDb` keeps `RS0030`, because its suppression covers a Guid operator.
  - **The hole closes.** The 20 file-wide `RS0030` disables no longer hide the ban.

**The guard.** `DatabaseBanSuppressionTests` checks that:
- JF0002 and JF0003 appear in production `.cs` files only on `#pragma` lines;
- only the root `Directory.Build.props` wires the rule, and nested props import their parent;
- the 4 pinned files with a bare disable contain no banned member;
- workflows and `.rsp` files do not switch analyzers off;
- the ABI job's build of `Jellyfin.Server` has no configuration or property arguments, and no `Configuration` environment variable.

**The canary.** `CheckDatabaseBanIsInEffect` (`Directory.Build.props:33`) is a Debug build-time check. It fails if a project the rule applies to is compiled without the analyzers or the list, with analyzers off, or with the rule in `NoWarn`.
- **In the runs.** The canary log shows it ran in the 22 in-scope projects that were built.
- **By the path rule.** 24 projects are in scope. The other 2 are the `fuzz/*.Fuzz.csproj` projects: they are not in `Jellyfin.sln` and CI does not build them, though the guard's source scan covers their `.cs` files.

**Real metadata.** 23 of the 25 entries resolve; the 2 SQL Server entries are absent.

**Negative controls** (`/tmp/27a-ban/negative/`):

| Control | Result |
|---|---|
| N1, raw SQL in `ItemPersistenceService`, under its file-wide `RS0030` disable | Silent at the base; fails as JF0002 at (70,26) at the tip. This is the headline. |
| N2 scoped disable, N3 test project, N4 provider | Silent, as intended |
| N5, the environment variable | No effect |
| N6 | 23 JF0002, 0 RS0030 |
| N7, N8 | The canary errors |
| N9, a nested props file without the import | Silent at build; only guard g10 catches it |
| N10, N11, `NoWarn` and `RunAnalyzers` from the environment | The canary fires, in dependency projects |
| N12, `ImportDirectoryBuildProps=false` from the environment | Silent. A documented limitation (`src/Jellyfin.Database/readme.md:169-174`): it drops the root props with every analyzer setting, and the tests cannot see a developer's shell. CI is covered by the workflow scan. |

The first negative-control run was invalid, because of a MimeTypes `.pp` rebuild artefact. It is kept in `/tmp/27a-ban/invalid/` with `WHY.txt`.

**Red mutations.** 7 analyzer breaks (m1 to m7) and 15 guard breaks (g1 to g15) each fail as intended.

**Proof** (`/tmp/27a-ban/proof/`, at `a142db1590`). All gates rc 0: Release, format, the 12 database legs, the analyzer tests and the row audit.
- **TRX rows.** 13 TRX files, 12,101 rows: 11,492 passed, 609 skipped. That is the 12 database legs (12,083 rows, 11,474 passed) plus the analyzer tests (18 of 18). I recounted them, and the orchestrator recounted them independently.
- **Identity and Docker.** Both clean.

**Per-commit gates.** Release, Debug (211 warnings, equal to the base) and format on both commits.

**Debug.** The base-against-tip comparison is `/tmp/27a-ban/debug/`, on `2f1f16c85f`. `debug-base/` was built on `a6cd869ce7`; its warning set is byte-identical.

**Evidence naming.** `/tmp/27a-ban/delta-backup-c-to-tip.diff` is misnamed: it is `e675334d7c..db0e73703e`, backup-c to backup-d.

**Review** (relayed). Approved over three rounds. Folded in:
- S1, guard bypasses;
- S2, a narrowed unresolved-id check;
- the coordinator's workflow scan and ABI-head check.

## Relayed CI

All green, every database job included, as read by the orchestrator:
- isolation `399af59770`, including the advisory MariaDB 12.3.3;
- collation `a0bf39394e`;
- kit `a6cd869ce7`;
- FolderDelete `2f1f16c85f`.

## Status and follow-ups

**Done:**
- isolation;
- collation;
- the kit and `EngineTraits`;
- the flavour sentinel;
- the `[` escape;
- the purge contract;
- FolderDelete determinism (section 4; the kit run showed a first failure only on run 2 on MySQL and run 7 on MariaDB, `/tmp/27a-kit/red/folderdelete-summary.tsv`);
- the dedicated ban rule (section 5).

**Follow-ups, still open:**
- the MySQL purge test classes; the slim `MySqlPurgeDatabaseArgumentTests` is kept by agreement;
- complex-type properties in `TextCollationConvention`;
- the views filter in `MySqlMigratedCollationTests`;
- the `OldAnnotation` charset;
- the null-tables check, which is server-only now;
- `[`, which is tested only through `LikeIgnoreAsciiCase`;
- the hand-built `%term%` at `TranslateQuery.cs:270` (pre-existing);
- PostgreSQL's transitive `CASCADE` against the purge contract;
- the nullable `tableNames` parameter;
- the inline server-version choice in `MySqlFamilyServer` and `MySqlTransactionIsolationTests`;
- the engine switches in `StartOverTests` and `AddNormalisedMatchColumnsMigrationTests`;
- analyzer coverage gaps: operators, `typeof` and type-level bans;
- JF0002 and JF0003 placed straight into `AnalyzerReleases.Shipped.md`.

**Notes for the owner:**
- **For the final upstream PR.** Whether jellyfin/jellyfin makes its ABI job a required check, since that job is what enforces the Debug-only analyzers in CI. This goes with the existing question of the PostgreSQL leg (B17).
- **No question on the fork.** The fork's `master` has no branch protection, which is expected for a CI runner (relayed from the coordinator), so no check is required there.
- **Fuzz projects.** They are outside CI.
