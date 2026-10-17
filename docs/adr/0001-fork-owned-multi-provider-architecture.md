# ADR-0001: Fork-owned multi-provider architecture

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

Jellyfin already has a provider seam, but only SQLite uses it:

- `IJellyfinDatabaseProvider` (`src/Jellyfin.Database/Jellyfin.Database.Implementations/IJellyfinDatabaseProvider.cs:18-95`) defines `Initialise`, `OnModelCreating`, `ConfigureConventions`, the maintenance members, the backup members and `PurgeDatabase`. `JellyfinDbContext` hands model building to the provider (`JellyfinDbContext.cs:326-339`).
- Providers are keyed by `[JellyfinDatabaseProviderKey]`. SQLite uses `"Jellyfin-SQLite"` (`SqliteDatabaseProvider.cs:22`).
- `Jellyfin.Server.Implementations/Extensions/ServiceCollectionExtensions.cs` hardcodes the built-in list (`:24-27`) and reads `database.xml` (`:81`). It falls back to the `migration-provider` config key (`:86`) and writes a SQLite default back to disk (`:97-102`). It supports external `PLUGIN_PROVIDER` assemblies (`:47-67`, `:106-114`), maps `LockingBehavior` (`:126-137`) and registers a pooled factory (`:139-145`). The method runs in two containers: `Program.cs:319` and `Startup.cs:74`.
- Config types: `DatabaseConfigurationOptions` (DatabaseType, CustomProviderOptions, LockingBehavior) and `CustomDatabaseOptions` (PluginName, PluginAssembly, ConnectionString, Options). SQLite ignores `ConnectionString` and builds its own from `Options` (`SqliteDatabaseProvider.cs:62-76`).
- EF migrations and code routines share one history table. They are ordered by ordinal ID (`Jellyfin.Server/Migrations/JellyfinMigrationService.cs:266-292`). Code-routine IDs are `yyyyMMddHHmmss_Name` (`Stages/CodeMigration.cs:18-20`).
- Some shared code already contains engine-specific SQL: `sqlite_sequence` in `Routines/20250420070000_MigrateActivityLogDb.cs:87` and `json_*` in `20260911120000_StripEmbeddedLinkedChildren.cs`. The shared model uses only quoted-identifier SQL (`HasFilter`, `BaseItemConfiguration.cs:39-85`).

Constraint: we **extend** the existing seam. We do not build a parallel one. Every change ships upstream as a small PR that leaves SQLite fully working (see `pr-plan.md`).

## Options considered

1. **Provider registration.** (a) Add one line per first-party provider to `DatabaseProviderTypes()`. (b) Scan assemblies for `JellyfinDatabaseProviderKeyAttribute`. (c) Move first-party providers to the `PLUGIN_PROVIDER` path.
2. **Dialect differences.** (a) Shared code branches on the provider (`is X`, `Database.IsNpgsql()`). (b) A general dialect or SQL-fragment service that shared queries call. (c) Keep shared code dialect-neutral: fix semantics in the model or with neutral LINQ, and give engine-specific operations narrowly named members on the provider.
3. **Secrets.** (a) Connection string in `database.xml` only. (b) Also allow a configuration or environment override. (c) Also add a CLI flag.

## Decision

- **Layout.** Each provider is one project, `src/Jellyfin.Database/Jellyfin.Database.Providers.<Engine>`. It holds the provider class, `Migrations/` (EF migrations, model snapshot, design-time factory) and provider-only conventions, interceptors and value converters. Postgres is `Jellyfin.Database.Providers.Postgres` in namespace `Jellyfin.Database.Providers.Postgres`. Conventions shared by every provider move to `Jellyfin.Database.Implementations`, for example the UTC converter (ADR-0004).
- **Keys.** Keys use the pattern `Jellyfin-<Engine>`: `Jellyfin-PostgreSQL`, and later `Jellyfin-MySQL` and `Jellyfin-SqlServer`. Lookup is case-insensitive, which is unchanged.
- **Registration (option 1a).** Add `yield return typeof(PostgresDatabaseProvider);` to `DatabaseProviderTypes()`. The list stays explicit and trimming-safe. Third parties keep using `PLUGIN_PROVIDER`.
- **Config surface.** Reuse `database.xml` as-is: `DatabaseType=Jellyfin-PostgreSQL`, `CustomProviderOptions/ConnectionString` holds a native Npgsql connection string, and `Options` holds provider tunables such as `command-timeout`. Built-in providers ignore `PluginName` and `PluginAssembly`, which may be empty. The XML schema does not change.
- **Secrets (option 3b).** Configuration key `database:ConnectionString` (env `JELLYFIN_DATABASE__CONNECTIONSTRING`) overrides `CustomProviderOptions.ConnectionString`. `AddJellyfinDbContext` applies the override in memory, **after** the write-back at `:97-102`, so it is never persisted. There is no CLI flag, because process listings would leak it. Npgsql's own `PGPASSWORD` and `PGPASSFILE` also work, and that is the recommended route. Providers must never log a password; Postgres logs the connection string with `Password` removed. A full system backup archive never carries `database.xml`, and a restore never overwrites the target host's `database.xml` (ADR-0003 R6, Amendment S1 below).
- **Dialect policy (option 2c).** Shared code (repositories, managers, routines) must not branch on the provider. Differences are handled in this order:
  1. Provider-neutral LINQ rewrites, for example the Guid MIN rewrite (ADR-0007).
  2. Model-level handling in the provider's `OnModelCreating` and `ConfigureConventions`: collations, converters, column types (ADR-0004/0005/0009).
  3. For work that genuinely needs engine-specific SQL (identity or sequence reseed after restore, JSON edits in routines, maintenance), an **additive, operation-named member** on `IJellyfinDatabaseProvider` whose **default implementation keeps today's SQLite behaviour**. For example, a reseed member defaults to a no-op.

  We reject a generic dialect/fragment service until a third provider proves it necessary. Enforcement: `BannedSymbols.txt` bans `IsSqlite()`, `IsNpgsql()`, `IsSqlServer()` and `IsMySql()`.
- **Migrations.** Each provider has its own EF migrations assembly and snapshot (`MigrationsAssembly(GetType().Assembly)`).
  - **Policy:** every schema change ships one migration *per provider*, all with the **identical migration ID** (`yyyyMMddHHmmss_Name`). The interleaving with code routines (`JellyfinMigrationService.cs:266-292`) is then the same on every engine.
  - A provider created later starts from a squashed baseline (ADR-0003).
  - Tests enforce this per provider: no pending model changes, and an ID set equal to the SQLite set after the baseline (ADR-0002).
- **Locking.** Postgres defaults to `NoLock`. `Pessimistic` and `Optimistic` exist to work around SQLite's single writer. If they are configured with a server engine, the provider honours them but logs a warning. (Amended by L1 below: `Pessimistic` is now refused on the server providers, and `Optimistic` is logged at Information.)

## Consequences

- Adding an engine means adding a project, a registration line, a test backend (ADR-0002) and migrations. Shared code stays unchanged unless a new operation-level member is required.
- Authors of schema changes must generate N migrations and align their IDs; the parity test fails otherwise. We will add a helper script later.
- Provider operation members grow one at a time. Each needs a default that keeps SQLite unchanged, so the interface can never break plugin providers.
- `HasFilter` partial indexes are portable to PostgreSQL and SQL Server but not to MySQL. The MySQL ADR will have to drop the filters in its provider conventions.
- Only one Jellyfin server may use a database at a time. EF takes a migration lock per migration, but code routines take none.
- `docs/adr/` is for the fork only and never appears in upstream PRs.

## Amendments (multi-target re-check, 2026-10-12)

This re-check covers the full target list in ADR-0011: PostgreSQL, MySQL/MariaDB and SQL Server.

- **M1. Keys.**
  - `Jellyfin-PostgreSQL`.
  - `Jellyfin-MySQL`, a single provider for MySQL and MariaDB. The server type comes from the `Options` entry `server-version`, with auto-detection as the fallback.
  - `Jellyfin-SqlServer`.
  - An Experimental provider (ADR-0011) logs a Warning in `Initialise`. Every server provider defaults to `NoLock`.
- **M2. Dialect policy re-checked against four engines: still no dialect or SQL-fragment service.** Every difference the probe found resolves to one of three things: a provider convention, a shared model change (ADR-0005 E, ADR-0009 L1), or an operation-named member.
  - The full list of additive members, each with a default that keeps SQLite's behaviour:
    - `IsRetryableWriteConflict` (ADR-0008);
    - `BeginExplicitIdentityInsertAsync` and `ResetIdentitySequencesAsync` (ADR-0003 S2);
    - `SupportsTransactionalMigrations` (ADR-0003 S1).
  - Shared code may read a capability flag, because a flag names an operation, not an engine.
- **M3. Filtered indexes.** This supersedes the Consequences bullet about MySQL.
  - None of the four filters is on a `UNIQUE` index (`BaseItemConfiguration.cs:38-39, 41-42, 69-70, 84-85`; the snapshot's only unique indexes are on other tables). Dropping a filter therefore changes index size and query plans, never results.
  - **PostgreSQL** keeps all four.
  - **MySQL/MariaDB** have no partial indexes, so a provider convention removes all four filters.
  - **SQL Server** keeps the three single-predicate filters. A provider convention drops the OR-shaped filter at `:85`, which SQL Server rejects (Msg 156).
  - The filters use double-quoted identifiers, so SQL Server needs `QUOTED_IDENTIFIER ON`. SqlClient sets it by default, and a test asserts it.
  - The comment at `:66-68` warns that an unfiltered `PrimaryVersionId` index can mislead the planner. The per-engine benchmark (ADR-0011) covers that risk.
- **M4. Identifier length.**
  - Six generated index names are longer than 63 characters; the longest has 81. EF already truncates generated names to the provider's limit. Verified on EF 10.0.11: Npgsql (63) produced `IX_BaseItems_IsFolder_TopParentIdentifierLongName_IsVirtualIte~`, and a collision got `~1`; SQL Server's limit is 128. So PostgreSQL never truncates a name silently.
  - The problem is that the `~`/`~1` suffix depends on the order in which the model is built. Adding an index can rename an existing one in a later migration.
  - **Decision:** a shared convention `IdentifierLengthConvention(int maxLength)` in `Jellyfin.Database.Implementations`. Each server provider applies it in `ConfigureConventions` with its own limit: PostgreSQL 63, MySQL/MariaDB 64, SQL Server 128.
    - A name over the limit becomes its first `maxLength - 9` characters, then `_`, then 8 hex characters of the SHA-256 of the full name.
    - The result is deterministic and does not depend on order. SQLite is not affected.
  - Shared `HasDatabaseName` was rejected: SQLite cannot rename an index, so the six `BaseItems` indexes would be dropped and rebuilt.
  - **Test (model only, per provider):** every table, column, key, foreign key and index name is within the limit, unique per schema, and contains no `~`.
- **M5. Cascade paths (SQL Server).** SQL Server rejects both of the following (Msg 1785):
  - the two cascading foreign keys from `AncestorIds` to `BaseItems` (`AncestorIdConfiguration.cs:17-18`);
  - the self-referencing cascade on `BaseItems.ParentId` (`BaseItemConfiguration.cs:30`).

  The shared delete path does not rely on those cascades:
  - it collects descendants through `ParentId` and `OwnerId` (`ItemPersistenceService.cs:62-88`);
  - it deletes `AncestorIds` by both columns and every dependent table explicitly (`:134-155`).

  **Decision:** a SQL Server provider convention sets `AncestorIds.ParentItemId` and `BaseItems.ParentId` to `NoAction`. The shared model and SQLite do not change.
  - **Test (every engine):** deleting a folder removes its descendants, the `AncestorIds` rows in both directions, and their dependents.
  - **Residual risk:** a future delete path that relies on these cascades would pass on SQLite and fail on SQL Server. The SQL Server suite catches it.
- **M6. Unique index with multiple NULLs.** `ImageInfos.UserId` is the only unique index on a nullable column. EF's SQL Server provider adds `[UserId] IS NOT NULL` to it automatically (verified on EF 10.0.11), so no action is needed. A model test asserts it.

## Amendments (upstream reconciliation, 2026-10-12)

- **U1. Upstream history and coexistence.** Nothing upstream at `61f9a9df23` duplicates this architecture.
  - #13451 added a PgSql provider. It was removed in the #13589 refactor, and #14556, an MsSql provider, was closed unmerged. Neither has code we can reuse, but both show the maintainers' intent.
  - The plugin-provider path (#14171) stays supported. In-tree providers sit beside it, and the provider key and `database.xml` are unchanged.
- **U2. Adopted as they are.**
  - #18264 `EnsureExistingDatabaseAsync` is provider-neutral.
  - #18196 gives `RefreshStatistics` a no-op default and skips it on an empty library. That matches our rule for additive members.

## Amendments (step 09 re-check, 2026-10-13)

- **P1. The existing SQLite-era routines are not rewritten; step 09 is dropped.** The routines with engine-specific SQL are `MigrateActivityLogDb` (`sqlite_sequence`), `MigrateUserDb`, `MigrateDisplayPreferencesDb`, `MigrateAuthenticationDb`, `MigrateLibraryDb` (all reading the legacy SQLite files) and `StripEmbeddedLinkedChildren` (`json_remove`/`json_valid`). Every one of them has `RunMigrationOnSetup = false` (the attribute default; only five routines set it to true, none of them with raw SQL), so a fresh install on any engine pre-marks them and never runs them. The X1 fix makes that pre-marking reliable. The only other way to reach a non-SQLite database is backup, switch, restore, and the restore carries the archive's routine history (step 08), so these routines are already marked there too. They can only ever run on SQLite, as today. Rewriting them would change upstream migration code for no behaviour on any engine.
  - **Rule for new routines:** a routine added from now on must be provider-neutral (EF queries and .NET-side filtering, as step 06's `RemoveNulFromItemKeys` does), or use an operation-named provider member per the dialect policy above. Reviewers check it; the step 15 test that a fresh PostgreSQL install pre-marks every `RunMigrationOnSetup = false` routine guards the existing ones.

## Amendments (third-engine evaluation, 2026-10-14)

An independent architecture evaluation, made after the MySQL/MariaDB provider (step 24) had been through review, checked whether option 2c survived a third engine.

- **E1. Option 2c stands; no dialect object.** Every MySQL/MariaDB difference landed as one of four things: a provider convention, a provider interceptor, an unconditional shared fix, or the one existing flag `SupportsTransactionalMigrations`. The shared fixes were an integer search score, `OwnerId` and `ParentId` cleared before a delete, and the migration history created before the restore transaction. None needed a dialect service. `IJellyfinDatabaseProvider` already carries five added default members, and each is permanent ABI. The rule is now:
  1. Make the shared code neutral unconditionally if that is possible, even if it costs an extra statement on SQLite and PostgreSQL. The `ParentId` pre-clear is the model: it satisfies MySQL's cascade depth limit and SQL Server's `NoAction` (M5) without a flag.
  2. Only when that is impossible, add an operation-named default member (the policy above). A capability flag is the last resort.
- **E2. The promised ban ships in step 27a.** The `IsSqlite()`/`IsNpgsql()`/`IsSqlServer()`/`IsMySql()` ban in the decision above was never added; `BannedSymbols.txt` has none of them. Step 27a bans them, and `ExecuteSqlRaw*`, `FromSql*` and `SqlQuery*` outside the provider projects. Each existing call site gets a suppression that states its reason.
- **E3. Conformance before features.** Each new engine starts from a fixed hazard checklist and from the whole suite run against a conventions-only provider stub; the brief is written afterwards. The cross-engine conformance kit from step 27a runs on every provider, SQLite and PostgreSQL included. It turns each hazard the suite cannot see (cascade depth, connection charset, host time zone, LIKE metacharacters, large id lists, long composite keys) into a permanent test.
- **E4. Merged SQLite and PostgreSQL steps are not reworked.** The PostgreSQL template clone, the per-provider conventions and retry classifiers, and the SQLite-only paths stay as they are. The kit covers them instead.
- **E5. In-tree versus plugin providers is not decided here.** Providers depend only on `Jellyfin.Database.Implementations`, so either route works without code changes. The choice for the upstream submission stays with the project owner, together with the other upstream questions.

## Amendments (contributor workflow, 2026-10-14)

The project owner asked what a contributor has to do per database once this lands. An evaluation of a year of upstream database commits, with an EF 10.0.12 experiment, settled the following. It supersedes "We will add a helper script later" under Consequences.

- **W1. One offline command, matching ids.** A schema change still ships one migration per provider with the identical id. The non-SQLite providers register a design-time `IMigrationsIdGenerator` that reuses the id of the SQLite migration with the same name.
  - The lookup is by file name in the SQLite provider's `Migrations/` directory, because a provider may only depend on `Jellyfin.Database.Implementations` (ADR-0011).
  - The generator implements the public interface. Subclassing EF's internal `MigrationsIdGenerator` raises EF1001, which `TreatWarningsAsErrors` turns into a build break.
  - It is registered through `[assembly: DesignTimeServicesReference]`. EF looks for `IDesignTimeServices` only in the startup assembly, while that attribute is read in both, so a different startup project cannot silently skip the generator.
  - One command generates the SQLite migration first and then every other provider's. Every design-time factory is offline, so it needs no server and no Docker.
- **W2. What enforces it.** One test theory over a provider table checks pending model changes and id pairing; each failure message names the command. A migration-contents check compares the operations of every paired id by kind, table and column, ignoring annotations. A `Sql` operation present on one engine only needs an allowlist entry with a reason. A fix for legacy SQLite data may stay SQLite-only; a fix for a bug in current code may not.
- **W3. Fork-only engines join the same table.** MySQL/MariaDB (step 25) and SQL Server (step 28) add a design-time factory with a fixed server version and rows in the theory, the command and the contents check. MariaDB gets its own pending-changes check.
- **W4. Escape-hatch order** (refines the dialect policy and E1):
  1. neutral LINQ;
  2. a `JellyfinDbFunctions` function with a provider translation, or a provider convention;
  3. an operation-named default member on `IJellyfinDatabaseProvider`, which is permanent ABI and needs core sign-off;
  4. a `KnownGaps` entry keyed to the provider, signed off by the provider's owner.

  `ProviderFact(Sqlite)` is only for tests of SQLite itself.
- **W5. The ban and the kit protect upstream.** The raw-SQL and provider-sniffing ban (E2) and the conformance kit on SQLite and PostgreSQL (E3) ship in the upstream series (step 22d). `EngineTraits` and the MySQL/MariaDB/SQL Server rows stay in the fork.
- **W6. Ownership.** Upstream contributors generate the SQLite and PostgreSQL migrations with the one command; PostgreSQL already runs on every pull request. The fork maintainer generates the MySQL and SQL Server migrations at each upstream sync with the same command and resolves what the contents check reports (pr-plan S5). This is the model Bitwarden uses.
- **W7. Contributor guide.** `src/Jellyfin.Database/readme.md` becomes an "Adding a database change" page: the command, W4, "review every generated migration", "never hand-port SQL blindly", and the hand edits a generated migration can need (data SQL, expression indexes, renames that EF scaffolds as drop plus add, type changes that need `USING`). The current page also names two different option spellings (`--provider` and `--migration-provider`), which the new page settles.

## Amendments (X13, 2026-10-17)

- **L1. Locking: `Pessimistic` is refused at startup on a provider that does not support it.** This supersedes the "Locking." bullet under the decision above.
  - **Every provider defaults to `NoLock`.** `NoLock` is the first value of `DatabaseLockingBehaviorTypes` (`DatabaseLockingBehaviorTypes.cs:11`), so a `database.xml` that does not set the property gets it. `Pessimistic` and `Optimistic` exist to work around SQLite's single writer.
  - **`Pessimistic` is refused at startup** on PostgreSQL, MySQL/MariaDB and SQL Server. The server stops with an `InvalidOperationException` that names `LockingBehavior` in `database.xml` and the provider, and says to set `NoLock`, the default, or `Optimistic`. The check runs when the provider is created, on its first resolution, and not at registration, so that it reads the capability of the provider that was chosen (`ServiceCollectionExtensions.EnsureLockingBehaviorIsSupported`). SQLite and a plugin provider that keeps the default are not affected.
    - **A cold start.** The first resolution is `ApplyStartupMigrationAsync` to `PrepareDatabaseProvider` (`Program.cs:327`), before `CheckFirstTimeRunOrMigration` (`:330`). No `try` surrounds it in `StartApp`, so the exception leaves the entry point, where the unhandled-exception handler logs it as Critical (`Program.cs:110-111`).
    - **An in-process restart** (after a restore, or a restart from the API) goes through `StartServer` instead. The refusal is thrown at `Program.cs:203`, caught and logged as Critical at `:271-274` (after the 10-minute delay at `:280`, which applies when the setup server is still alive and the configuration was not completed), and the `finally` (`:286-294`) resolves `IJellyfinDatabaseProvider` again, which throws the same refusal out of `StartServer`. That is read from the code; no test covers the in-process path.
  - **`Optimistic` is accepted, and nothing changes for it.** No provider reads `LockingBehavior`. `OptimisticLockBehavior.Initialise` logs "The database locking mode has been set to: Optimistic." at Information (`OptimisticLockBehavior.cs:83`), as `NoLockBehavior` does for its mode (`NoLockBehavior.cs:33`). The only mode whose `Initialise` logs a warning is `Pessimistic` (`PessimisticLockBehavior.cs:57`), and that is the one that is now refused on the server providers.
  - **Why a refusal and not a warning.** `PessimisticLockBehavior` is one static `ReaderWriterLockSlim` (`PessimisticLockBehavior.cs:43`) that a thread takes and an asynchronous save holds across an `await`. A `ReaderWriterLockSlim` belongs to a thread, so the release after the `await` can throw `SynchronizationLockException` and leave the lock held for good, and the next writer then waits without end. The step 27 hang showed it (pr-plan B37). SQLite's single writer is the only case where the lock is needed at all. On an engine with concurrent writers it adds nothing and risks a hang, so a warning that the operator may not read is the wrong answer.
- **L2. `SupportsPessimisticLocking` joins the list of additive members of M2.** It is `bool SupportsPessimisticLocking => true;` on `IJellyfinDatabaseProvider`, overridden to `false` in the PostgreSQL, MySQL (which serves MariaDB) and SQL Server providers.
  - **Justified under E1.** E1.1 asks for a neutral, unconditional fix first, and that is impossible here: SQLite has to keep `Pessimistic`, because its owners may use it, so no shared change can drop the behaviour for everyone. E1.2 then applies: an operation-named default member, whose default keeps the behaviour that exists today. The name describes what the engine can do, not which engine it is, which is the test M2 sets for a capability flag.
  - **Core sign-off (W4.3).** The member is permanent ABI, so it needs the core maintainers' sign-off, like the others on the list. Until that is given, it is a fork-only member (pr-plan B38).
  - **A plugin provider** inherits the default `true`, so the refusal never applies to it. A plugin provider for a server engine keeps the hazard, and its author can override the member.
- **L3. What is not changed.**
  - `database.xml` is not rewritten. Editing it to `NoLock` is the whole upgrade.
  - The shared `NoLock`, `Pessimistic` and `Optimistic` behaviours are unchanged, and so is the SQLite hazard of `Pessimistic` (a thread-affine lock held across an `await`), which stays on the owner's upstream-notes list (pr-plan B37, B38).

## Amendments (X14, 2026-10-17)

- **S1. Full system backup and restore leave `database.xml` alone.**
  - **Archive contents.** `CreateBackupAsync` filters out `DatabaseConfigurationStore.DatabaseConfigurationFileName` (`database.xml`) when walking `ConfigurationDirectoryPath`. Backup archives never carry a `database.xml`.
  - **Target protection.** `RestoreBackupAsync` checks archive paths against `DatabaseConfigurationStore.DatabaseConfigurationFileName` (case-insensitively). Any archive entry matching `database.xml` is skipped and cannot overwrite the target host's database configuration.
  - **Security and cross-engine portability.** This ensures that secrets in `database.xml` (passwords, connection strings, host credentials) are never exfiltrated into backup zip archives, and restoring an archive onto another host preserves that host's database provider, credentials, and settings.

