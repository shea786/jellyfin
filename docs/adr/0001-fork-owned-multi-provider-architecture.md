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
- **Secrets (option 3b).** Configuration key `database:ConnectionString` (env `JELLYFIN_DATABASE__CONNECTIONSTRING`) overrides `CustomProviderOptions.ConnectionString`. `AddJellyfinDbContext` applies the override in memory, **after** the write-back at `:97-102`, so it is never persisted. There is no CLI flag, because process listings would leak it. Npgsql's own `PGPASSWORD` and `PGPASSFILE` also work, and that is the recommended route. Providers must never log a password; Postgres logs the connection string with `Password` removed.
- **Dialect policy (option 2c).** Shared code (repositories, managers, routines) must not branch on the provider. Differences are handled in this order:
  1. Provider-neutral LINQ rewrites, for example the Guid MIN rewrite (ADR-0007).
  2. Model-level handling in the provider's `OnModelCreating` and `ConfigureConventions`: collations, converters, column types (ADR-0004/0005/0009).
  3. For work that genuinely needs engine-specific SQL (identity or sequence reseed after restore, JSON edits in routines, maintenance), an **additive, operation-named member** on `IJellyfinDatabaseProvider` whose **default implementation keeps today's SQLite behaviour**. For example, a reseed member defaults to a no-op.

  We reject a generic dialect/fragment service until a third provider proves it necessary. Enforcement: `BannedSymbols.txt` bans `IsSqlite()`, `IsNpgsql()`, `IsSqlServer()` and `IsMySql()`.
- **Migrations.** Each provider has its own EF migrations assembly and snapshot (`MigrationsAssembly(GetType().Assembly)`).
  - **Policy:** every schema change ships one migration *per provider*, all with the **identical migration ID** (`yyyyMMddHHmmss_Name`). The interleaving with code routines (`JellyfinMigrationService.cs:266-292`) is then the same on every engine.
  - A provider created later starts from a squashed baseline (ADR-0003).
  - Tests enforce this per provider: no pending model changes, and an ID set equal to the SQLite set after the baseline (ADR-0002).
- **Locking.** Postgres defaults to `NoLock`. `Pessimistic` and `Optimistic` exist to work around SQLite's single writer. If they are configured with a server engine, the provider honours them but logs a warning.

## Consequences

- Adding an engine means adding a project, a registration line, a test backend (ADR-0002) and migrations. Shared code stays unchanged unless a new operation-level member is required.
- Authors of schema changes must generate N migrations and align their IDs; the parity test fails otherwise. We will add a helper script later.
- Provider operation members grow one at a time. Each needs a default that keeps SQLite unchanged, so the interface can never break plugin providers.
- `HasFilter` partial indexes are portable to PostgreSQL and SQL Server but not to MySQL. The MySQL ADR will have to drop the filters in its provider conventions.
- Only one Jellyfin server may use a database at a time. EF takes a migration lock per migration, but code routines take none.
- `docs/adr/` is for the fork only and never appears in upstream PRs.
