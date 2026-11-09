# Explainer: X13, refusing Pessimistic locking on providers that cannot run it

These are fork-internal study notes, not upstream PR text. They describe the branch and the retained evidence as of 2026-10-17.

## Branch and state

- **Branch:** `multidb/x13-pessimistic-refusal`, cut from the `feature/multi-db` tip `27e938f529`, in four commits:
  - *Add SupportsPessimisticLocking to the database provider* (`ef5a37b730`): the interface member with its default, the three overrides, and the first tests.
  - *Refuse Pessimistic locking on a provider that does not support it* (`efb31a6d42`): the startup check and its tests.
  - *Drop the Pessimistic rows from the server-engine locking tests* (`9d0dc0625f`): the SQL Server session test and the MySQL isolation test.
  - A docs commit with this explainer, the ADR-0001 L1 to L3 and ADR-0008 S8 and S9 amendments, pr-plan B38 with the X13 row, the step 27 text that said "planned: X13", and the `LockingBehavior` row of the how-to template.
- **One rewrite.** After the first review the history was rewritten once. The only change is the doc comment on `SupportsPessimisticLocking` in the first commit, which no longer says that the server refuses to start, because that is true only from the second commit (which now adds the clause). The trees of the second and third commits are byte-identical to the originals (`d5c86d8632` and `8b85571b88`; proof in `/tmp/x13/rewrite-proof.txt`). The branch before the rewrite is kept as `multidb/x13-pessimistic-refusal-backup-a` (`dd1479d2b2`). The full suites of "The tip proof" ran on `8b85571b88`, whose tree is the tree of the new third commit.
- **State.** Fork-only. The orchestrator reviews the branch and merges it into `feature/multi-db`.
- **Relevant entries:** ADR-0001 L1 to L3 (and M2, E1, W4); ADR-0008 S8, S9 and S7; pr-plan B37 and B38.
- **Evidence:** `/tmp/x13/`.
  - `anchors.txt`: the code anchors of the brief, checked at the base.
  - `red-first/` and `red-first-c2/`: the red-first runs for the first and the second code commit.
  - `gates/g2/`: the per-commit gates of the three original code commits. `gates/g3/`: the gate of the rewritten first commit. `gates/g1/` is an aborted run, kept for the record (see "Gates").
  - `dev/tip/`: the full suites at the tip, `full/trx-summary.tsv`, `tipcheck.json`, `tipcheck-reasons.txt` (made by `/tmp/x13/scripts/tipcheck-reasons.py`), the Docker lists and the identity files.
  - `rewrite-proof.txt`: the tree and message equality of the rewrite, and the diff of the first commit.
  - `red/final/`: the six red controls.
  - `docs-render.log`: the render and its `--check`.

## The gap

`LockingBehavior=Pessimistic` in `database.xml` selects `PessimisticLockBehavior`, whatever the provider.
- The behaviour takes one static `ReaderWriterLockSlim` (`PessimisticLockBehavior.cs:43`) when a save or a transaction starts, and releases it when that ends.
- The asynchronous paths (`OnSaveChangesAsync`, and the async overrides of `TransactionLockingInterceptor`) hold it across an `await`.
- A `ReaderWriterLockSlim` belongs to a thread. The code after an `await` can run on another thread, so the release can throw `SynchronizationLockException` ("The write lock is being released without being held"). The lock then stays held by the first thread, and the next writer waits for it without end.

On SQLite that is the shape upstream already warns about. On PostgreSQL, MySQL, MariaDB and SQL Server there is no reason to want the lock at all: ADR-0001 says `Pessimistic` and `Optimistic` exist to work around SQLite's single writer. Before X13 the fork honoured the setting there and `PessimisticLockBehavior` logged a warning when it was initialised (`PessimisticLockBehavior.cs:57`), which an operator who copied an old `database.xml` may never read.

The hang was found by step 27, when an asynchronous SQL Server test under `Pessimistic` stopped a run for 15 minutes (pr-plan B37, "The pessimistic-lock hang"). B37 left the production exposure as "planned: X13".

## The design

- **The member.** `bool SupportsPessimisticLocking => true;` on `IJellyfinDatabaseProvider`, a default interface member. The PostgreSQL, MySQL (which serves MariaDB) and SQL Server providers return `false`. SQLite and any plugin provider that does not say otherwise keep `true`.
- **The check.** `ServiceCollectionExtensions.EnsureLockingBehaviorIsSupported(configuration, provider)` throws an `InvalidOperationException` when the configured behaviour is `Pessimistic` and the provider says no:

  > LockingBehavior in database.xml is Pessimistic, which the database provider '{DatabaseType}' does not support. Set LockingBehavior to NoLock, the default, or Optimistic.

- **Where it runs.** In the factory of the `IJellyfinDatabaseProvider` singleton, right after `providerFactory!(services)`, and not when the services are registered. At registration the provider has not been created yet, and a test that only registers (`AddJellyfinDbContext_PessimisticOnAServerEngine_RegistrationAloneDoesNotThrow`) pins that registration alone does not throw. On a cold start the first resolution is the call of `PrepareDatabaseProvider` in `ApplyStartupMigrationAsync` (`Program.cs:327`), before the database is touched, and the exception escapes `StartApp`. On an in-process restart (after a restore or a restart from the API) the call is at `Program.cs:203`, inside the `try` that is caught at `Program.cs:271-274`; the `finally` block then resolves the provider again and rethrows, so the server still does not start.
- **Why a default member and not a shared fix.** ADR-0001 E1.1 asks for a neutral, unconditional fix first. There is none: SQLite must keep `Pessimistic`. So E1.2 applies, an operation-named default member (ADR-0001 L2). It is permanent ABI, so W4.3 core sign-off applies, as for the other members of the M2 list.
- **Why the SQL Server row calls the helper directly.** The check itself reads the member of the provider, not `EngineTraits` or a provider name. `EngineTraits.IsRegistered` is false for SQL Server until step 28, so the server cannot start on it yet. The SQL Server row of the refusal test therefore calls the helper directly with the provider, and the other rows go through the real `AddJellyfinDbContext` path.

## Tests

`LockingBehaviourRegistrationTests` (new), over every engine of the provider table:
- `SupportsPessimisticLocking_IsTrueOnlyForSqlite`: the member equals `EngineTraits.IsEmbedded`. The variable is typed as the interface, because a default interface member is not visible through the class.
- `AddJellyfinDbContext_PessimisticOnAServerEngine_RefusesToStartAndNamesTheSetting`: an `InvalidOperationException` whose message names `LockingBehavior`, `Pessimistic`, `NoLock`, `Optimistic` and the provider key.
- `AddJellyfinDbContext_PessimisticOnAServerEngine_RegistrationAloneDoesNotThrow`.
- `AddJellyfinDbContext_PessimisticOnSqlite_Starts`.
- `AddJellyfinDbContext_NoLockOrOptimistic_StartsOnEveryEngine`.
- `EnsureLockingBehaviorIsSupported_PluginProviderThatKeepsTheDefault_StartsUnderPessimistic`: a provider that does not override the member keeps working, as the plugin providers do.

The third commit removes the rows that ran a server engine under `Pessimistic`: `MySqlTransactionIsolationTests.BeginTransaction_UnderEachLockingBehaviour_RunsAtReadCommitted` (and its `switch` arm), and the SQL Server `SaveChanges_WithEachLockingBehaviour_RunsAtReadCommittedAndIsReadBack`, which is now asynchronous because only that behaviour had made it synchronous. `ConnectionStringOverrideTests` still uses `Pessimistic` with a plugin-style provider that keeps the default, and its expectations did not change.

## Red first

- **First code commit.** On the base `27e938f529` the new test file does not compile: `CS1061: 'IJellyfinDatabaseProvider' does not contain a definition for 'SupportsPessimisticLocking'` (`/tmp/x13/red-first/c1-build-on-base.errors`, rc 1). Two earlier attempts are kept and are not evidence: one failed on an environmental `MimeTypes` error of a `--no-incremental` build, and one on a typo in the test, where the variable must be typed as the interface.
- **Second code commit.** At the first commit's tree with the tests of the second added:
  - Stage 1: the test project does not compile, `CS0117: 'ServiceCollectionExtensions' does not contain a definition for 'EnsureLockingBehaviorIsSupported'` (`stage1-build.errors`, rc 1).
  - Stage 2: with an empty-bodied helper that nothing calls, the project builds and the 4 refusal rows fail by name (MariaDB, MySQL, PostgreSQL and SQL Server) while the other 20 pass (`stage2-rows.txt`, rc 1).
  - The stub was then removed: `revert-diff.txt` is empty, and no test host was left (`leftover-hosts.txt` is empty).

## Gates

- **g1 was aborted** (filter missed `ProviderBackendTests`; the audit failed closed). The note is `/tmp/x13/gates/g1/ABORTED-NOTE.txt`. It was stopped by hand, because the runner records a failed gate and carries on. No number of g1 is quoted anywhere.
- **g2** (`/tmp/x13/gates/g2/`, `COMPLETE`, every step under the 12G cap in `cap.txt`) ran each of the three original code commits (`d0f35fe962`, `d5c86d8632`, `8b85571b88`) with:
  - Debug of the base and of each commit: 0 errors and the same 211 warnings, an empty normalised diff;
  - Release: 0 warnings and 0 errors;
  - `dotnet format --verify-no-changes`: 0;
  - the render `--check`: "Every rendered file is up to date";
  - the targeted tests on all five engines, in three legs each (Implementations, Server.Tests, Integration), every leg audited and every sentinel (`ProviderBackend_IsLive`, `CreateClient_SelectedProvider_RunsTheServerOnThatProvider`) present. No row failed in any leg of any commit (`results.tsv` has no non-zero rc).
- **g3** (`/tmp/x13/gates/g3/`, `COMPLETE`, 12G cap in `cap.txt`) re-gated only the rewritten first commit (`ef5a37b730`) from the base, with the same filter as g2. The second and third commits were not re-run: their trees are byte-identical to the ones g2 gated.
  - Debug: 0 errors and the same 211 warnings as the base, with an empty normalised diff; Release: 0 warnings and 0 errors; `dotnet format --verify-no-changes`: 0; the render `--check`: 0.
  - The targeted tests on all five engines in three legs each: every leg rc 0 (`results.tsv`), every sentinel present, 1320 rows audited, 1073 passed, 247 not executed, none failed (`trx-row-audit.tsv`).
  - Rows at the first commit (passed / skipped, `trx-summary.tsv`): SQLite 160 / 54, 33 / 1, 3 / 6; PostgreSQL 160 / 54, 33 / 1, 8 / 1; MySQL and MariaDB 176 / 43, 34 / 0, 8 / 1; SQL Server 225 / 15, 13 / 20, 2 / 7 (Implementations, Server.Tests, Integration).
- **Rows at the third commit** (g2, passed / skipped, none failed):

  | Engine | Implementations | Server.Tests | Integration |
  |---|---|---|---|
  | SQLite | 179 / 54 | 33 / 1 | 3 / 6 |
  | PostgreSQL | 179 / 54 | 33 / 1 | 8 / 1 |
  | MySQL | 194 / 43 | 34 / 0 | 8 / 1 |
  | MariaDB | 194 / 43 | 34 / 0 | 8 / 1 |
  | SQL Server | 242 / 15 | 13 / 20 | 2 / 7 |

## The tip proof

On `8b85571b88` (the third code commit before the rewrite, whose tree is the tree of `9d0dc0625f`; the docs commit changes only `docs/`), `/tmp/x13/dev/tip/`. Start and end identity are the same commit (`8b85571b88cb04e7cb7e7eda0c3d111847993b10`), and the status was clean.

The cells are the counters of each TRX `ResultSummary` (`full/trx-summary.tsv`; the `Passed!` lines of `full/progress.txt` give the same totals): total / passed / failed / skipped. The `Counters` element of the TRX says `notExecuted="0"`, so "skipped" is the number of rows with the outcome `NotExecuted` (total minus executed), as the console summary counts them.

| Engine | Implementations | Server.Tests | Integration |
|---|---|---|---|
| SQLite | 3058 / 2889 / 0 / 169 | 172 / 161 / 0 / 11 | 148 / 137 / 0 / 11 |
| PostgreSQL | 3036 / 2887 / 0 / 149 | 171 / 153 / 0 / 18 | 150 / 144 / 0 / 6 |
| MySQL | 3033 / 2831 / 0 / 202 | 171 / 131 / 0 / 40 | 150 / 144 / 0 / 6 |
| MariaDB | 3033 / 2832 / 0 / 201 | 171 / 131 / 0 / 40 | 150 / 144 / 0 / 6 |
| SQL Server | 3048 / 2843 / 0 / 205 | 170 / 106 / 0 / 64 | 148 / 8 / 0 / 140 |

Controller.Tests (SQLite): 218 / 218 / 0 / 0. Nothing failed anywhere, and every audit passed. The only blame message was the benign "Sequence file will not be generated"; no `Sequence_*.xml` exists, so nothing hung.

These are rows, not distinct names. In every Implementations suite 37 rows share their name with another row, and in every Server.Tests suite 3 do (Integration: none). `tipcheck.json` keys on the test name, so it counts fewer: Implementations 3021 (SQLite), 2999 (PostgreSQL), 2996 (MySQL and MariaDB) and 3011 (SQL Server); Server.Tests 169, 168, 168 and 167. Those distinct-name counts are not used in the table above.

**Comparison with step 27's s10** (`/tmp/x13/dev/tip/tipcheck-reasons.txt`, made by `/tmp/x13/scripts/tipcheck-reasons.py`). The script compares, for the 15 suites and Controller.Tests, the multiset of (test name, outcome, skip-reason message) of the TRX files of `/tmp/27/dev/s10` and of the tip. Duplicate-named rows are not collapsed. What the output shows:
- Row counts of Implementations from s10 to the tip: SQLite 3034 to 3058, PostgreSQL 3012 to 3036, MySQL and MariaDB 3010 to 3033, SQL Server 3026 to 3048. The other suites have equal row counts.
- Only in X13's run: 24 tests on every engine, all passed, the new `LockingBehaviourRegistrationTests` rows.
- Only in s10: one row on MySQL and on MariaDB (`BeginTransaction_UnderEachLockingBehaviour_RunsAtReadCommitted(lockingBehaviour: Pessimistic)`), and two on SQL Server (`SaveChanges_WithEachLockingBehaviour_RunsAtReadCommittedAndIsReadBack(behaviourName: "PessimisticLockBehavior")` in both session classes). Those are the dropped rows.
- The last line is "TOTAL names whose outcome or skip reason differs: 0". The earlier `tipcheck.json` compared outcomes only and is not the evidence for the skip reasons.

## The red controls

Six scratch mutations of the tree, each run against `LockingBehaviourRegistrationTests` and `ConnectionStringOverrideTests` (39 tests), with the exact set of expected failures and no prefix or wildcard matching (`/tmp/x13/red/final/results.json`, `COMPLETE`). A control is red only if the failed set equals the expected set. Each scratch change was reverted clean, and no test host was left.

| Control | Mutation | Failed rows | Verdict |
|---|---|---|---|
| baseline | none | 0 | green |
| a | no override in the three providers | 8 | caught exactly |
| b | the provider factory does not call the check | 3 | caught exactly |
| c | no override in PostgreSQL | 2 | caught exactly |
| d | the default of the member is `false` | 11 | caught exactly |
| e | the check ignores the configured behaviour | 8 | caught exactly |
| f | the check runs at registration, not in the factory | 3 | caught exactly |

Control (b) fails 3 rows, not 4, because the SQL Server row calls the helper directly and so does not depend on the call in the factory.

## Risks and the release note

**Release note (fork users).**
- A fork install that has `LockingBehavior` set to `Pessimistic` in `database.xml` on **PostgreSQL, MySQL or MariaDB** now stops at startup with the message above. SQLite is unaffected. SQL Server is not registered yet (step 28), so no install can run on it.
- **Editing `database.xml` to `NoLock` is the whole upgrade.** `NoLock` is the default, and the right choice on these engines. `Optimistic` is accepted: no provider reads `LockingBehavior`, and `OptimisticLockBehavior` logs it at Information (`OptimisticLockBehavior.cs:83`).
- Nothing rewrites the file. There is no automatic repair, because changing a setting the operator wrote is a decision for the operator.
- **A restart policy will start the server again, and each start refuses again** until the file is fixed (under `restart: always`, `Restart=always` or a similar policy). The reason goes to the console and to stderr, and normally to the log file too. The file sink is asynchronous (`logging.json:18`), so a process that dies at once may not have flushed it; read the console or the service journal first.

**Risks.**
- **Plugin server providers keep the hazard.** A plugin provider for PostgreSQL or another server engine inherits `true` and is never refused. Its author can override the member. The fork cannot tell what a plugin connects to.
- **Validate on save is not done.** `ConfigurationController.UpdateNamedConfiguration` (`:95`) saves `database.xml` without the check, so a bad value is only found at the next start. Only the running provider could be checked there, and a change of `DatabaseType` would still be left to the startup check. Recorded as a follow-up (pr-plan B38).
- **A restore can bring the setting back.** `BackupService.cs:173` copies `Config/` from an archive over the live `database.xml`, so a restore from an old backup may bring `Pessimistic` back on a server engine. It is refused at the next start (an in-process restart refuses too, by the path described under "Where it runs"). X14 will stop that overwrite.
- **The upstream half is untouched.** SQLite with `Pessimistic` still holds a thread-affine lock across an `await`; the option is a `SemaphoreSlim`. That stays on the owner's upstream-notes list (pr-plan B37, B38).
- **`PessimisticLockBehavior` is now untested.** The third commit removed the last rows that ran it, and no `.cs` file under `tests/` names `PessimisticLockBehavior` any more, so the SQLite behaviour has no test. That is on the upstream-notes list too (pr-plan B38).
- **Permanent ABI.** The member is permanent once the upstream series contains it (W4.3). It is in the fork only until the core maintainers sign it off.

## The replay

PostgreSQL ships in the upstream series, so the member and the check are candidates to fold into it. Which upstream step takes them is decided at the replay (B31), not here:
- the member with its default, the PostgreSQL override and the check would go with the PostgreSQL provider; the MySQL and SQL Server overrides with the steps that add those providers;
- `LockingBehaviourRegistrationTests` would move with them, and the `Pessimistic` rows of the MySQL and SQL Server tests would never be written there.

The fork keeps the commits as they are. Core sign-off (ADR-0001 W4.3) applies to the member before it can go upstream.

## Things to be ready to defend

- **Why refuse, and not warn as before?** The lock can deadlock the writers, and a warning in a log is easy to miss. The setting does nothing useful on an engine with concurrent writers, so refusing costs the operator one edit.
- **Why not fix `PessimisticLockBehavior` with a `SemaphoreSlim`?** That is the upstream half. It changes the behaviour on SQLite, the one engine where `Pessimistic` still means something, and upstream decides that.
- **Why not make the check at registration?** The capability belongs to the provider, which is created in the factory, so the check reads the provider that was actually chosen, including a plugin provider. Control (f) shows that a check moved to registration is caught.
- **Why a default member, not a check on the provider key?** A key list in shared code is the provider sniffing that ADR-0001 E2 bans. A capability flag names an operation, which M2 allows.
- **Why is `Optimistic` still accepted?** It does not hold a thread-affine lock, and it is logged at Information, not as a warning. On server engines it does nothing useful (its retry reads SQLite message text), but it does no harm, and refusing it would break more installs for no gain.
