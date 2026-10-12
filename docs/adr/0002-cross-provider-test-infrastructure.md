# ADR-0002: Cross-provider test infrastructure

- **Status:** Accepted
- **Date:** 2026-10-12

## Context

- Tests use xUnit **v3**: `xunit.v3` 3.2.2, `xunit.runner.visualstudio` 3.1.5, VSTest via `dotnet test` (`Directory.Packages.props:86-87`). Test projects are `OutputType=Exe`.
- 33 test classes derive from `tests/Jellyfin.Server.Implementations.Tests/Item/SqliteDbTestFixture.cs`. That fixture builds a new in-memory SQLite database **per test instance**, runs `EnsureCreated` (`:34-43`) and creates `SqliteDatabaseProvider` and `NoLockBehavior` directly (`:54-58`).
- About 11 more classes create `new SqliteConnection(...)` themselves, for example `BackupServiceTests.cs:50`, `Users/*`, and the `tests/Jellyfin.Server.Tests/Migrations/*` routine tests.
- No test calls `Migrate()`. `JellyfinApplicationFactory` boots the full server on SQLite on disk.
- Upstream order (see `pr-plan.md`): portability fixes land **before** this infrastructure. Each of those PRs ships SQLite tests that pin the behaviour. This ADR has to let those tests run on Postgres later without being rewritten.

## Options considered (running one test class on several providers)

- **A. Abstract test class plus one small derived class per provider.** Both providers appear in Test Explorer. The cost is per-class churn, it is easy to forget to enrol a class, and each new engine adds a class to every file.
- **B. Ambient provider chosen by an environment variable.** One test class, run once per provider. There is no per-class churn, and every fixture-based test is enrolled automatically.
- **C. Custom `TestFramework` or theory data that multiplies cases.** Fragile across xUnit versions, and the boilerplate moves into every test signature.
- **D. Collection fixtures.** These share state; they do not multiply cases, so they don't solve the problem.

## Decision

**Option B.** Tests run on SQLite by default. `JELLYFIN_TEST_DB_PROVIDER=Postgres` runs the same tests on Postgres. An unknown value throws.

**Shared project `tests/Jellyfin.Database.Testing`.** This is a class library, not a test project. It references `Jellyfin.Database.Implementations`, both provider projects, `Testcontainers.PostgreSql`, `xunit.v3.extensibility.core`, `xunit.v3.assert` and `Moq`. It contains:

- `TestDbProvider` enum: `Sqlite`, `Postgres`.
- `TestDatabaseEnvironment` (static), which reads:
  - `JELLYFIN_TEST_DB_PROVIDER`
  - `JELLYFIN_TEST_POSTGRES_IMAGE`, default **`postgres:16-trixie`**. That is the supported floor (ADR-0003), on Debian/glibc so collation matches typical installs. Alpine/musl collation differs.
  - `JELLYFIN_TEST_POSTGRES_CONNECTIONSTRING`, an optional external server for developers without Docker. It needs a role with `CREATEDB`.
- `ITestDatabaseBackend`, with `SqliteTestBackend` and `PostgresTestBackend`. Each is a lazy singleton per test process. Method: `TestDatabase Create(TestSchemaSource source, IInterceptor[] interceptors)`.
- `TestDatabase : IDisposable`, which exposes `Options`, `ConnectionString` and `CreateProvider()`. Dispose drops or closes the database.
- `TestSchemaSource`: `Model` (`EnsureCreated`) or `Migrations` (`Migrate()`).
- `DatabaseTestFixture` (abstract). It replaces `SqliteDbTestFixture` and keeps the same protected API: `ctor(params IInterceptor[])`, `ApplicationPaths`, `CreateDbContext()` and `CreateDbContextFactory()`. It adds `DbProvider` (`TestDbProvider`) and `DatabaseProvider` (`IJellyfinDatabaseProvider`).
- `TestDbContextFactory` (replaces the Moq factory), `KnownGap`, `KnownGaps`, `KnownProviderGapAttribute`, and `PostgresFactAttribute`/`PostgresTheoryAttribute`. These are `FactAttribute`/`TheoryAttribute` subclasses that set `SkipUnless`/`SkipType` to "Postgres selected" and carry `Trait("Provider","Postgres")`, for tests that only make sense on Postgres.

`Jellyfin.Server.Implementations.Tests` keeps the item helpers in `JellyfinDbTestFixture : DatabaseTestFixture`. That class is the old `SqliteDbTestFixture` renamed, plus `CreateBaseItemRepository`. Renaming changes one line in each derived class.

**Postgres backend:**

- One Testcontainers container per test process: tmpfs data directory, `fsync=off`, `synchronous_commit=off`, `full_page_writes=off`, `max_connections=300`. Ryuk cleans it up.
- A template database is built once per `TestSchemaSource`.
- Each test instance gets its own database: `CREATE DATABASE jf_<guid> TEMPLATE <tpl> STRATEGY FILE_COPY`, serialised by a `SemaphoreSlim`. Dispose clears the pool and runs `DROP DATABASE … WITH (FORCE)`.
- One database per instance keeps today's SQLite isolation, and derived constructors can keep seeding synchronously.

**Schema source:**

- `Model` stays the default until Postgres migrations exist. After that, a separate PR switches the default to `Migrations` for both providers, so repository tests run against the schema we actually ship. SQLite then migrates one template once and copies it per test with `SqliteConnection.BackupDatabase`.
- Migration tests always use real migrations.

**Without Docker:**

- Postgres is opt-in, so a default run never needs Docker.
- Selecting Postgres means Postgres is **required**. If the container fails, tests fail; they never skip. This replaces the proposed `JELLYFIN_TEST_REQUIRE_POSTGRES`.
- `[PostgresFact]` tests show as skipped with a clear reason when Postgres is not selected. CI gating is in ADR-0010.

**Known-gap ledger (fails when it goes stale):**

- `KnownGaps` is a static class of `const string` IDs, for example `Adr0005_CaseInsensitiveLike = "ADR-0005/G2"`.
- Method level: wrap the test body as `await KnownGap.On(TestDbProvider.Postgres, KnownGaps.X, async () => { … })`. On a different provider the body just runs. On the gap provider:
  - an exception becomes `Assert.Skip("[known gap ADR-0005/G2] …")`;
  - a **pass becomes `Assert.Fail("gap fixed — remove KnownGap")`**.
- Class level, for seeding that fails in the constructor: `[KnownProviderGap(Postgres, KnownGaps.X)]`. The base constructor calls `Assert.Skip` before derived seeding runs. This is skip-only.
- Closing an inventory item **deletes its constant**, which breaks the build wherever the gap is still marked. Nothing can go stale silently.

**Rules for tests in portability PRs (before this infrastructure lands):**

- Derive from `SqliteDbTestFixture` and use only its API.
- No `Microsoft.Data.Sqlite`, no raw SQL, no SQLite-only assertions.
- Seed data that exposes the difference: mixed case, NULLs, over-length strings, `DateTimeKind.Local`/`Unspecified`.
- When the infrastructure PR lands, those tests run on Postgres with only the one-line rename.

**Enrolment:**

- The 11 hand-rolled classes move onto `DatabaseTestFixture`.
- Then `tests/BannedSymbols.Tests.txt` bans `SqliteConnection..ctor` and `UseSqlite` outside `Jellyfin.Database.Testing`.
- Until a class is migrated, a `#pragma warning disable RS0030 // pending enrolment` marks it, so the pragmas themselves are the backlog.

**Migration tests (`Trait("Category","Migrations")`):**

- (a) `HasPendingModelChanges()` is false for **each** provider. This needs no live database, so it runs everywhere.
- (b) Every provider's `IMigrationsAssembly.Migrations` keys after its baseline equal the SQLite keys after that baseline.
- (c) An empty database migrated to the latest version on the selected provider has nothing pending.
- (d) Upgrade tests (migrate to N, seed, migrate to latest) are deferred until Postgres has at least two migrations.

## Consequences

- The infrastructure arrives in two PRs (`pr-plan.md`). PR 10 adds the shared project with only the SQLite backend. PR 13 adds the Postgres backend, Testcontainers and `[PostgresFact]` together with the provider.
- Test Explorer shows one provider per run. Developers switch provider with `tests/postgres.runsettings` (EnvironmentVariables) or the environment.
- Developers write tests once, and adding an engine adds no per-class code.
- Running Postgres locally before the stack reaches the infrastructure PR means using a fork-only overlay branch (`pr-plan.md`).
- The infrastructure needs self-tests in `Jellyfin.Server.Implementations.Tests/Database/Infrastructure/`, covering:
  - the `KnownGap` inversion;
  - that `Assert.Skip` from a constructor is reported as skipped under xUnit v3;
  - per-test isolation;
  - that an invalid provider value throws.

## Amendments (orchestrator review, 2026-10-12)

- **A1. xUnit version checked.** Every test project already references `xunit.v3` 3.2.2 (`Directory.Packages.props:87`), so `xunit.v3.extensibility.core` and `xunit.v3.assert` 3.2.2 in the support library do not mix versions. Pin all three to the same version in `Directory.Packages.props`.
- **A2. Fixture rename stays, as its own mechanical commit.**
  - We considered keeping `SqliteDbTestFixture` as the name or as an alias. Both were rejected: once the class runs on Postgres the name is false, and an alias leaves two names for one thing.
  - In PR 10 the rename is a **separate commit that does nothing else**: `git mv` plus a one-token base-class change in each of the 33 derived classes, with no other edits in those files. A reviewer can check it with `git show --stat` and `--word-diff`.
  - Logic changes in PR 10 are in other commits.
- **A3. Integration branch.** The fork-only overlay branch referred to as `multidb/dev` in `pr-plan.md` is `feature/multi-db`.

## Amendments (multi-target re-check, 2026-10-12)

- **H1. A generic backend contract.** Per-test isolation belongs to the backend. The fixture never assumes template databases.
  - `TestDbProvider` gets one value per target: `Sqlite`, `Postgres`, `MySql`, `MariaDb`, `SqlServer`. Each value is added by the PR that adds its backend.
  - `ITestDatabaseBackend` exposes `Provider`, `ServerVersion` and `CreateAsync(TestSchemaSource, IInterceptor[], CancellationToken)`, which returns a `TestDatabase`.
  - Isolation strategy per backend:
    - **SQLite:** an in-memory database per test instance, or a template copy under `Migrations`. Unchanged.
    - **PostgreSQL:** a template clone per test instance. Unchanged.
    - **MySQL, MariaDB, SQL Server: a leased pool.**
      - Each process builds databases once per `TestSchemaSource`, on demand, and leases one exclusively to each test instance.
      - When a lease is returned, the database is reset: the provider's `PurgeDatabase` runs, then the model's seed data is re-inserted from `IEntityType.GetSeedData()` (for example the placeholder `BaseItem`).
      - Tests that change the schema, such as migration tests, ask for `fresh: true`. They get a new database, which is dropped afterwards.
- **H2. The pool is generic code** in `Jellyfin.Database.Testing`.
  - It lands in group C0 (`pr-plan.md` PR 22) and tests itself on SQLite file databases, so it exists before any engine needs it.
  - Self-test: after a lease is returned, every table's row count matches a freshly built database, and the next identity value is 1.
- **H3. `[ProviderFact(params TestDbProvider[])]` and `[ProviderTheory]`** skip unless the selected provider is in their list, and add `Trait("Provider", …)`.
  - `PostgresFact` remains only as a one-line subclass, and only if the harness PR has already shipped it.
- **H4. Environment variables generalise** to `JELLYFIN_TEST_<ENGINE>_IMAGE` and `JELLYFIN_TEST_<ENGINE>_CONNECTIONSTRING`, where `<ENGINE>` is `POSTGRES`, `MYSQL`, `MARIADB` or `SQLSERVER`.
  - Defaults are the pinned images in ADR-0011.
  - An external server needs the right to create databases.
- **H5. Each provider group adds its own backend.** This supersedes the Consequences bullet that said PR 13 adds "the Postgres backend" as the only one.
  - **Note for the harness work now in progress:** PR 10 must ship the generic contract (H1, H3), even though only SQLite implements it there.
- **H6 (orchestrator ruling). `Create` stays synchronous:** `TestDatabase Create(TestSchemaSource, IInterceptor[])`, not `CreateAsync`.
  - The 33 derived fixtures seed in their constructors. An async factory would force sync-over-async blocking in every fixture constructor, or a conversion of all of them to `IAsyncLifetime`, which is churn.
  - `TestDatabase.Dispose` means "release": SQLite closes the database, Postgres drops it, and a pooled backend returns it to the pool.
  - The `fresh` flag arrives with the pool (PR 22).

## Amendments (harness final gate, 2026-10-12)

- **H7. Every check builds and tests in Debug as well as Release.**
  - `Directory.Build.props` enables `AnalysisMode=AllEnabledByDefault` and the `Jellyfin.CodeAnalysis` analyzers only in Debug. A Release-only check missed CA2100 in `PostgresTestBackend`.
  - A bare `dotnet build`, `dotnet test` or IDE build is Debug, so a Debug failure breaks every contributor.
  - SQL built from identifiers we generate (`CREATE`/`DROP DATABASE`) cannot take parameters. It is quoted and suppressed with a justification at the method, not file-wide.
- **H8. A gap covers only the assertion whose expected value depends on the provider.**
  - A behavioural gap matches any assertion failure, so a whole-body span hides unrelated regressions. The final gate showed this: a filtering expectation that was wrong everywhere failed on SQLite and was silently skipped on Postgres.
  - Assertions that hold on every provider sit outside the span. For example, which values a filter keeps is checked on every provider, compared case-insensitively and in a fixed order. Only the exact spelling and order are inside the span.
  - Gaps that come from a server error still match the specific SQLSTATE or exception type.
- **H9. A test of SQLite internals is SQLite-only, not a gap.**
  - `UpdatePeople_GeneratedSqlUsesPeopleNameIndex` reads SQLite's `EXPLAIN QUERY PLAN` output, which no other engine can produce. It becomes `[ProviderFact(Sqlite)]` and the `LowerIndexLookup` gap is removed.
  - Rule: a gap must be something the provider could later close. If no provider fix could make the test pass, the test is provider-specific.
  - The Postgres `lower()` index is tested by the Postgres provider PR that adds it (PR 14), with its own plan check.
- **H10. Shipped test code does not cite fork ADRs.**
  - `docs/adr/` never goes upstream, so "ADR-0007" in a skip reason cannot be looked up.
  - A gap is a `KnownGaps` field with a short description of the difference, for example "PostgreSQL has no min/max over uuid". It replaces the `ADR-xxxx/Gn` IDs in the Decision.
  - There is no class-level `[KnownProviderGap]`; the unused attribute was removed earlier. Seeding that fails on a provider is covered by the ProcessExit sweep, as accepted in the harness review.
- **H11. Container lifetime.** The Postgres container is stopped explicitly in the existing ProcessExit sweep, after the remaining databases are dropped, and this is best effort. Ryuk stays as the backstop for crashed or killed runs. Relying on Ryuk alone leaks containers where it is disabled (`TESTCONTAINERS_RYUK_DISABLED`).
- **H12. Rejected review findings.**
  - The copyright line in the new provider's `Properties/AssemblyInfo.cs` matches all four sibling projects word for word. It stays, for consistency.
  - The SQLite run keeps 15 skipped rows for Postgres-only tests. They carry a clear reason, and ADR-0010 accepts this.
