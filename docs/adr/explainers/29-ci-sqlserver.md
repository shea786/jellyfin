# Explainer: step 29, SQL Server in CI, and the operator docs

These are fork-internal study notes, not upstream PR text. They describe the branch and the retained proof as of 2026-10-22.

- **Branch:** `multidb/29-ci-sqlserver`, 2 code commits on `57a1e599ce` plus this documentation commit, fast-forward merged into `feature/multi-db`.

  | Commit | Subject |
  |---|---|
  | `4ba383eb82` | Run the database tests on SQL Server in CI, without requiring them |
  | `9fee9d073f` | Add a how-to page and Docker Compose example for Jellyfin on SQL Server |
  | *(this commit)* | Record the SQL Server CI workflow and operator docs in the ADRs and the pr-plan |

- **Relevant decisions:** ADR-0001 (M3, M5, M6), ADR-0006 (G1, G2), ADR-0008 (S7), ADR-0010 (CI3), ADR-0011 (tiers and CI images; amendments F9, F10, F11), ADR-0012 (decision 7), pr-plan row 29 and B42, Step X11, Step X13, Step X14.
- **Review:** Independent review `step29-review-round1`: `APPROVE` with 0 Must-Fix and 0 Should-Fix.
- **Evidence:** `/tmp/29/` (`/tmp/29/COMPLETE`).

## Why it exists

- **CI.** Step 28 registered the `Jellyfin-SqlServer` provider and added its baseline migration, but SQL Server had no CI workflow. Under ADR-0011 and ADR-0010, an Experimental engine's test job runs on every pull request to catch engine-specific query translation failures early, without being a required check that blocks merges.
- **Operator docs.** There was no documentation or Docker Compose reference for running Jellyfin on SQL Server.
- **Template-driven parity.** The new how-to guide and Compose files are rendered by `docs/database/render` alongside PostgreSQL, MySQL, and MariaDB, keeping all four engines consistent in structure, commands, and backup/restore procedures.

## What each commit does

### The SQL Server CI workflow (`4ba383eb82`)

`.github/workflows/ci-tests-db-sqlserver.yml` calls the shared reusable database test workflow `./.github/workflows/ci-tests-db.yml` with two matrix entries:

| Entry | Image | Role |
|---|---|---|
| `SqlServer` | `mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718` | oldest supported (version 16 / 2022), the harness image |
| `SqlServer newest` | `mcr.microsoft.com/mssql/server:2025-CU9-GDR1-ubuntu-24.04@sha256:742481d1b1186cccc81220a1eaacef244ca8d0f7d19c8e5c70df8ae541ffbab2` | advisory (`advisory: true`): version 17 / 2025, mainstream support to 2031 |

- **Digest pins.** Both container images are pinned by exact digest.
- **Timeout.** Both legs set `timeoutMinutes: 40`, accounting for SQL Server's slower container startup compared to other engines (ADR-0010 CI3).
- **Triggers.** Runs on every `pull_request`, nightly at 04:17 UTC (`schedule`), and on demand (`workflow_dispatch`). Pull request runs have no path filters because changes to shared query or repository logic anywhere in the codebase can introduce constructs SQL Server translates differently. Nightly and manual triggers fire once the workflow is on the default branch.
- **Not required.** The provider is Experimental, so neither job is a required check, neither blocks merges, and their names match no required check pattern.

### Operator docs and Compose example (`9fee9d073f`)

Updates `docs/database/README.md`, `docs/database/render`, and templates `docs/database/templates/{how-to.md,compose.yaml,env.example}`, rendering:
- `docs/database/sqlserver.md`
- `docs/database/compose/sqlserver/compose.yaml`
- `docs/database/compose/sqlserver/env.example`

`render --check` verifies that all rendered files match their templates.

**Facts covered in `docs/database/sqlserver.md`:**
- **Experimental warning:** The provider logs a warning at every start: *"The SQL Server database provider is experimental. Back up the data before using it with a library you cannot rebuild."* Use is recommended only with rebuildable libraries, accompanied by database backups.
- **Supported versions and refusals:** The minimum supported version is SQL Server 2022 (`16.0`). At connection open, the provider inspects `SERVERPROPERTY('EngineEdition')` and refuses:
  - Any server reporting version < 16.0.
  - Azure SQL Database (edition 5), Azure Synapse (6 and 11), and Azure SQL Managed Instance (8), per ADR-0011 F9. Version strings alone would mislead (Azure SQL Database reports 12 regardless of engine, and Managed Instance versions vary by update stream).
  - Container default is `MSSQL_PID=Developer` (free for dev/test only); production deployments require `MSSQL_PID=Express` (capped at 10 GB on 2022 or 50 GB on 2025) or a licensed edition.
- **Collation & snapshot isolation:**
  - `Latin1_General_100_BIN2` is applied to every text column and `__EFMigrationsHistory` via `BinaryCollationHistoryRepository`. If the database default collation differs, a warning is logged at startup.
  - `ALTER DATABASE [jellyfin] SET READ_COMMITTED_SNAPSHOT ON` is required for row versioning so `READ COMMITTED` queries do not take shared locks or block behind library scan writes. If `OFF`, Jellyfin logs a warning at startup.
- **Privileges & session settings:**
  - The `jellyfin` user needs `db_owner` role membership for DDL migrations, `UPDATE STATISTICS`, identity reseed (`DBCC CHECKIDENT`), and toggling foreign keys during restores.
  - Every connection open executes `SET TRANSACTION ISOLATION LEVEL READ COMMITTED`, ensuring pooled connections do not inherit stricter isolation levels from prior transactions (ADR-0008 S7).
- **Connection encryption:**
  - `Encrypt=True` (Mandatory) is enforced by default per ADR-0011 F10 when unspecified in the connection string. Explicit operator values (`False`/`Optional`, `True`/`Mandatory`, `Strict`) and `TrustServerCertificate` are preserved. `TrustServerCertificate=True` is recommended for local/container deployments with self-signed certificates.
- **Locking behavior:**
  - `LockingBehavior=Pessimistic` is refused at startup per Step X13; only `NoLock` (default) and `Optimistic` are supported.
- **1700-byte index key limit & ~850-character path limit (ADR-0012 decision 7):**
  - Text columns in indexes are `nvarchar(4000)`. 18 nonclustered indexes in the baseline migration have declared key widths exceeding SQL Server's 1700-byte limit (emitting expected warning Msg 1945 during creation).
  - Runtime error 1946 (`ADR-0009/G2`) occurs if an actual index key exceeds 1700 bytes. With UTF-16 (2 bytes per BMP code unit, 4 bytes per supplementary character like emoji), `IX_BaseItems_Path` accommodates at most **850 characters** (or 425 supplementary characters).
  - Items with paths exceeding ~850 characters cannot be saved. Without Step X11, an entire folder batch rolls back on one over-long item, stopping the scan. With Step X11 (`SaveItemsSkippingFailures`), Jellyfin retries per-item, logs and skips only the over-long item, and continues scanning the folder—unless the folder's own path exceeds the limit.
- **String semantics:**
  - Trailing spaces are ignored in `=` and `UNIQUE` checks even under `Latin1_General_100_BIN2` (`'Rock'` and `'Rock '` collide, ADR-0006 G1).
  - String ordering operates on 16-bit UTF-16 code units rather than Unicode code points (supplementary characters `U+10000+` sort before BMP `U+E000..U+FFFF`, ADR-0006 G2).
- **Filtered indexes and cascade paths:**
  - `IX_BaseItems_PrimaryVersionId` is unfiltered because SQL Server disallows `OR` in filtered indexes (ADR-0001 M3).
  - `IX_ImageInfos_UserId` is filtered `[UserId] IS NOT NULL` (ADR-0001 M6).
  - `FK_BaseItems_BaseItems_ParentId` and `FK_AncestorIds_BaseItems_ParentItemId` use `ON DELETE NO ACTION` to avoid Msg 1785 multiple cascade paths, with Jellyfin performing application-level cascade cleanup (ADR-0001 M5).
- **Backups and restores:**
  - Native `BACKUP DATABASE` / `RESTORE DATABASE` provides true database dumps and the only rollback mechanism for failed schema upgrades.
  - Jellyfin backup archives (Dashboard, Backups) include table rows and home-screen sections (Step X10), but exclude `database.xml` (Step X14). An archive restore runs in one transaction and rolls back entirely if any row violates SQL Server limits (ADR-0012 T7).
- **Upgrades:**
  - DDL is transactional on SQL Server. A failing migration rolls back with its transaction while earlier migrations remain applied. Jellyfin logs the last successful migration. Operators must run `BACKUP DATABASE` before upgrading.
- **Docker Compose:**
  - Runs Jellyfin alongside `mcr.microsoft.com/mssql/server:2022-CU27-GDR1-ubuntu-22.04`.
  - Because the SQL Server container does not automatically create databases or users, the container healthcheck connects as `sa` and runs an idempotent provisioning script that creates the `jellyfin` database (`COLLATE Latin1_General_100_BIN2`, `READ_COMMITTED_SNAPSHOT ON`), login, and `db_owner` user before reporting healthy.
  - `database.xml` is mounted into `/run/jellyfin/database.xml` and copied to the configuration volume on first boot.
  - Connection string is supplied via `JELLYFIN_DATABASE__CONNECTIONSTRING` from `.env`, keeping credentials off disk and out of backup archives.
  - Health checks coordinate startup: Jellyfin waits for SQL Server TCP readiness, and reports healthy when `/health` answers `Healthy`.

## Review

- **Round 1 (`step29-review-round1`):** Independent review evaluated the workflow definition, template rendering, documentation completeness, and verification controls.
- **Verdict:** `APPROVE` with 0 Must-Fix and 0 Should-Fix.

## Verification and Evidence

All verification artifacts are preserved in `/tmp/29/` (`/tmp/29/COMPLETE`):

### 1. Workflow Lint (`/tmp/29/lint/lint.log`)
- All 5 workflow files were parsed and linted using `rhysd/actionlint:1.7.12`:
  - `.github/workflows/ci-tests-db-sqlserver.yml`
  - `.github/workflows/ci-tests-db.yml`
  - `.github/workflows/ci-tests-db-mysql.yml`
  - `.github/workflows/ci-tests-db-nightly.yml`
  - `.github/workflows/ci-tests.yml`
- Result: 0 errors, `rc=0`.

### 2. Check-Step Controls (`/tmp/29/check-controls/results.tsv`)
Tested the workflow check step logic against positive and negative test matrices:
- **Positive controls (2/2 passed, `rc=0`):**
  - `positive-sqlserver-2022`: SQL Server 2022 (`16.0.4305`) passed all sentinel, version, and required-test checks.
  - `positive-sqlserver-2025`: SQL Server 2025 (`17.0.5015`) passed all sentinel, version, and required-test checks.
- **Negative controls (5/5 correctly caught and failed, `rc=1`):**
  - `negative-version17-against-2022`: version mismatch caught.
  - `negative-version16-against-2025`: version mismatch caught.
  - `negative-missing-integration-trx`: missing integration project TRX caught.
  - `negative-skipped-server-sentinel`: skipped `ProviderBackend_IsLive` sentinel caught.
  - `negative-wrong-image-variable`: incorrect `IMAGE_VARIABLE` environment variable caught.

### 3. Per-Commit Static Gates (`/tmp/29/gates/results.tsv`)
- `render --check`: exited `rc=0` on both commit 1 (`4ba383eb82`) and commit 2 (`9fee9d073f`).
- `render-negative-control`: perturbation in `sqlserver.md` and `compose.yaml` caught with `rc=1`.
- `dotnet format Jellyfin.sln --verify-no-changes`: passed with `rc=0`.
- Release build (`dotnet build Jellyfin.sln -c Release --no-incremental`): 0 warnings, 0 errors.
- Debug build (`dotnet build Jellyfin.sln -c Debug --no-incremental`): 0 errors, 211 warnings (identical to base commit `57a1e599ce`, diff is empty).

### 4. Local CI Leg Execution (`/tmp/29/ci-legs/results.tsv`, `/tmp/29/ci-legs/trx-counts.tsv`)
Both CI matrix legs were executed locally following the workflow steps:

| Leg | Server Version | Step 2 rc | Step 3 rc | Step 4 rc | Result |
|---|---|---|---|---|---|
| `SqlServer` | SQL Server 2022 CU27 GDR1 (`16.0.4305`) | 0 | 0 | 0 | `success` |
| `SqlServer newest` | SQL Server 2025 CU9 GDR1 (`17.0.5015`) | 0 | 0 | 0 | `success` |

Both legs produced identical TRX counts across all 3 test projects:

| Project | Total | Passed | Failed | Skipped |
|---|---:|---:|---:|---:|
| `Jellyfin.Server.Implementations.Tests` | 3,100 | 2,913 | 0 | 187 |
| `Jellyfin.Server.Tests` | 171 | 126 | 0 | 45 |
| `Jellyfin.Server.Integration.Tests` | 150 | 144 | 0 | 6 |
| **Total per leg** | **3,421** | **3,183** | **0** | **238** |

Across both legs: 6,842 TRX test rows, 6,366 passed, 476 skipped, 0 failed.

### 5. Live Docker Compose Verification (`/tmp/29/compose/env/`, `/tmp/29/compose/docxml/`)
Tested both `MODE=env` (connection string from `JELLYFIN_DATABASE__CONNECTIONSTRING`) and `MODE=docxml` (connection string from `database.xml`):
- `docker compose config`: valid configuration with 0 errors.
- Database auto-provisioning: healthcheck successfully created database `jellyfin` with `COLLATE Latin1_General_100_BIN2` and `is_read_committed_snapshot_on = 1`.
- Startup and health: Jellyfin started, logged expected SQL Server initialization, answered `/health` with `Healthy`, and responded to `/System/Info/Public` with HTTP 200.
- Log checks: 0 `[ERR]` or `[FTL]` lines, 0 RCSI or collation warnings.
- Schema verification: 31 model tables, 0 non-BIN2 text columns, 53 migrations applied from `20200101000000_InitialCreate` through `20261016043615_IndexItemPathByHash`.
- Backup and restore round-trip:
  - Database backed up with `BACKUP DATABASE`.
  - Schema perturbed by dropping 2 tables (31 -> 29 tables).
  - Database restored with `RESTORE DATABASE ... WITH REPLACE` (29 -> 31 tables restored).
  - Jellyfin restarted against restored database and responded `Healthy`.
- Teardown: `docker compose down -v` left 0 containers, 0 volumes, and 0 networks.
