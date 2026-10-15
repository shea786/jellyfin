# Explainer: step 22c, the migration contents check

These are fork-internal study notes, not upstream PR text. They describe the branches, the merge and the retained proof as of 2026-10-15.

- **Upstream-shaped branch:** `multidb/22c-migration-contents-check`, 3 commits on the 22b tip `d37f0d08fb`, final tip `70082dda51`:
  - `d95815c0cc` Share one paired migration id generator between the providers
  - `ac04957b95` Compare the operations of migrations that share an id across providers
  - `70082dda51` Describe the migration contents check in the database guide
- **Fork:** the merge `d59093e95c` (parents `693da13de8` and `70082dda51`) and one commit on top, `5597fca7cc` "Compare the MySQL and MariaDB migrations once in the contents check".
- **State:**
  - The orchestrator fast-forwarded `feature/multi-db` to `5597fca7cc`.
  - The 22c code tip `5597fca7cc` is on origin `feature/multi-db`, pushed by the coordinator. The orchestrator checked this read-only with `git ls-remote` on 2026-10-15; it is relayed, not checked here.
  - The docs commit `ed644cf583`, and the follow-up that corrects this line, go up with the next code merge.
  - No CI result at `5597fca7cc` is known, and none is claimed.
- **Backups, kept:**

  | Branch | Backup | SHA |
  |---|---|---|
  | `multidb/22c-migration-contents-check` | `-backup-a` | `788a28775a` |
  | | `-backup-b` | `d1ff0939dd` |
  | `multidb/22c-merge-candidate` | `-backup-a` | `ed39922868` |
  | | `-backup-b` | `7452d0826e` |
  | | `-backup-c` | `1f61eb54d9` |

- **Relevant decisions:** ADR-0001 W2 and W6, pr-plan 22c, B17, B20 and B27.
- **Evidence:** `/tmp/22c/`. The chain summary is `chain3/chains.txt`.

## Why it exists

22b made one command generate every provider's migration with the same id, and its theory checks that every provider has each id. Nothing checked that the paired migrations do the same thing. A hand edit to one provider's migration, for example SQL added only to the SQLite one, went unnoticed (ADR-0001 W2).

Hand edits are common enough to matter. B17 estimated that about 40% of upstream migrations contain hand-written SQLite SQL. Measured in the fork at `5597fca7cc`, 12 of the 57 SQLite migrations call `migrationBuilder.Sql` (`/tmp/22c/allowlist/sqlite-sql-stats.txt`, and a grep of the migration files). Only one SQLite migration is paired today: `20261013120000_AddNormalisedMatchColumns`. Every earlier SQLite migration is covered by the PostgreSQL and MySQL baselines, so it is not paired and not compared.

## What each commit does

### The shared id generator (`d95815c0cc`)

The generator and the SQLite migrations finder move from the PostgreSQL provider into `Jellyfin.Database.Implementations`, in a new `DesignTime` namespace. Both types are internal: `PairedMigrationsIdGenerator` is `internal sealed` and `PairedMigrationsDesignTimeServices` is `internal static`.

- **Why this assembly.**
  - Every provider already references it, and a provider may reference nothing else (ADR-0011).
  - It already exposes its internals to the PostgreSQL provider and the test project, so no new `InternalsVisibleTo` is added. The commit touches no `AssemblyInfo`, and the public API, which the ABI check covers, does not change.
- **Why not a linked source file.** A file linked into each provider would compile the same internal type into every provider assembly. The test project sees the internals of all of them, so the type name would be ambiguous there (CS0433). That is why step 25 used a copy.
- **What stays per provider.** Each provider keeps its own `IDesignTimeServices` and `[assembly: DesignTimeServicesReference]`.
- **Tests.** Three new theories over the provider table build each provider's design-time services through its attribute, as the tools do. They check that the provider gets the shared generator, that the newest SQLite names get their SQLite ids, and that a name without a SQLite migration gets a fresh id that sorts after them.

### The check (`ac04957b95`)

The check builds the `Up` and `Down` operations of every migration of every provider in the provider table. It does this offline, through each provider's offline context, without a database. For every id that more than one provider has, it compares each operation.

**What it compares:**
- the kind, the table and the columns;
- whether a column is nullable, whether an index is unique, and what a foreign key does on delete;
- for `CreateTable`, each column and each key separately, so a difference names the column;
- for SQL, the text. It is identified by the first 16 hex digits of the SHA-256 of the text, with runs of white space made one space, followed by the first 60 characters so that it can be read.

**What it does not compare:**
- annotations;
- column types and maximum lengths;
- defaults and computed-column SQL;
- collations;
- index filters;
- the SQL of check constraints;
- the names of new indexes and constraints;
- the values of data operations;
- the order of the operations.

These are engine details. A hand edit to one of them in one provider's migration body, for example to a column type, is caught by neither this check nor the pending-changes check. The pending-changes check compares the model snapshot with the model, not the body of the migration. The class comment says so, after N-A1 (see the review history).

**The allowlist.** Every difference needs exactly one entry in `MigrationContentsAllowlist.json`. An entry is keyed by migration id, direction, provider and the exact operation, and carries a category and a reason. The check fails, and every failure names `dev/add-migration`, in these cases:

| Failure | Meaning |
|---|---|
| Missing | A difference has no entry. The message prints the JSON entry to fill in. |
| Stale | An entry matches no difference. |
| Ambiguous | A difference has more than one entry. |
| Invalid | An entry has an unknown provider or direction, or no reason. Or it uses `LegacySqliteData` on a provider other than the reference provider, SQLite. |
| Unreadable | The file cannot be parsed: an unknown category or property, or a pasted entry that still holds its placeholders. The `JsonException` is wrapped in an `InvalidDataException` that names the command (B1, below). |

**Categories.** These are the categories W2 allows:

| Category | When it applies |
|---|---|
| `EquivalentSql` | SQL on one provider that does what the others' operations do. |
| `EngineSpecific` | A step only one engine needs. |
| `LegacySqliteData` | A fix for data only old SQLite databases can hold. SQLite only. |

A fix for a bug in current code has no category: it has to be ported to every provider.

### The guide (`70082dda51`)

The database readme says:
- what the check compares and what it does not;
- when to port a hand edit and when to record it;
- where the allowlist is, and that the failure prints the entry to fill in;
- that a legacy-data fix is SQLite-only, and that a fix for a bug in current code is never listed.

## The fork merge and the commit after it

**The merge (`d59093e95c`).** Git merged the files without a textual conflict. The merge resolves the semantic conflicts with step 25's MySQL provider, which B17 allows: "any conflict with group C is resolved in the merge commit, never by rewriting a merged step".

- **MySQL moves to the shared generator.** `MySqlDesignTimeServices` now calls `PairedMigrationsDesignTimeServices.AddPairedMigrationsIdGenerator`, like PostgreSQL.
- **The copy and its parity tests go.** These are the MySQL copy of the generator, `MySqlPairedMigrationsIdGeneratorTests`, `ProviderSourceParityTests` and the finder tests in `MySqlDesignTimeServicesTests`. The 22c theories now prove the parity directly on every MySQL and MariaDB row. This settles B20's first gate follow-up.
- **New fork-only category `BaselineCoverage`.** It is accepted only for the `BaselineId` of the row's provider. It is fork-only because upstream's one baseline shares no id with SQLite.
  - The MySQL and PostgreSQL baselines share the id `20200101000000_InitialCreate`. The PostgreSQL baseline covers the SQLite migrations only through `20261002210711_MakeOwnerIdIndexesPartial`, and adds the normalised columns in its own `AddNormalisedMatchColumns`.
  - The MySQL baseline covers them through `20261013120000_AddNormalisedMatchColumns`.
- **The two baselines.** They agree on every column's nullability, every index's uniqueness and every foreign key's on-delete action. They differ in 10 operations, all entered:
  - 7 `BaselineCoverage`: what one baseline has and the other adds later;
  - 3 `EngineSpecific`: MySQL's `AlterDatabase` to utf8mb4, and PostgreSQL's `min(uuid)` aggregate, created in `Up` and dropped in `Down`.
- **The upstream sync.** `81f29427e6` brought no migration and no change under `src/Jellyfin.Database`, so it needs no entry.

**One row per migrations assembly (`5597fca7cc`).** The provider table lists the one MySQL and MariaDB migration set five times: MySQL 8.4 and 9.7, and MariaDB 11.4.7, 11.8 and 12.3. The model depends on the server version, so the pending-changes check needs all five. The contents check had compared those rows with each other, so every MySQL-side entry named all five rows, and a new version row would have made those entries stale.

The check now compares the first row of each migrations assembly, so the MySQL family takes part once, as "MySQL 8.4". Two new tests support this:
- one shows that the operations of every migration are the same in all rows that share an assembly;
- one shows that there is one row per assembly, starting with the reference provider.

## The allowlist as merged

The tables are in `/tmp/22c/allowlist/`. The retained JSON files are byte-identical to the files in git at `70082dda51` and `5597fca7cc`.

| Shape | Entries | `BaselineCoverage` | `EngineSpecific` | `EquivalentSql` |
|---|---:|---:|---:|---:|
| A, `70082dda51` | 7 | 0 | 1 | 6 |
| Fork, `5597fca7cc` | 17 | 7 | 4 | 6 |

The fork's four `EngineSpecific` entries:
- MySQL's `AlterDatabase` to utf8mb4;
- PostgreSQL's `min(uuid)` aggregate, created in `Up`;
- the same aggregate, dropped in `Down`;
- PostgreSQL's `DROP DEFAULT` on `Peoples.NameLower` in `AddNormalisedMatchColumns` `Up`.

**Why `DROP DEFAULT` is `EngineSpecific`.** The generated `AddColumn` gives the new required column a default of `''`, so that existing rows get a value. The model has no default, so PostgreSQL drops it again. SQLite has no `ALTER COLUMN` and would have to rebuild `Peoples` to drop the default, so it keeps the `''` default.

That is an observation, not a bug. `NameLower` is a mapped property without a store-generated value, so EF Core writes it on every insert, and the default never applies to new rows.

**The six `AddNormalisedMatchColumns` `Down` entries (`EquivalentSql`).** SQLite drops the index and the two columns with three SQL statements, to avoid the table rebuild of the builder's `DropColumn`. PostgreSQL uses `DropIndex` and two `DropColumn` operations. The check records them as six witnesses, three per side, instead of learning to read SQL. An edit to either side makes its entries stale.

Both `Down` bodies end with the same fourth statement, which recreates the old `lower("Name")` index. Its text is the same on both providers, so it is not a difference.

**The reviewer's equivalence check.** The independent reviewer hand-checked that the three SQLite statements act on the same objects, in the same order, as PostgreSQL's `DropIndex` and two `DropColumn` operations, and recomputed all six hashes. This is the reviewer's reasoning, relayed by the coordinator; the review notes are not in the evidence. The two `Down` bodies in git show the same objects in the same order:
1. `IX_Peoples_NameLower`
2. `Peoples.NameLower`
3. `BaseItems.CleanOriginalTitle`

## The commit chain and its gates

**Rule.** The upstream series is reviewed commit by commit, so no commit may be red by design. The chain was rewritten until every commit passes its own gates. The final chain's gates are in `/tmp/22c/chain3/gates/results.tsv`, with `COMPLETE` and rc 0:

| Commit | Release | Format | Targeted tests |
|---|---|---|---|
| `d95815c0cc` | 0/0 | equal to base | 24/24 |
| `ac04957b95` | 0/0 | equal to base | 55/55 |
| `70082dda51` | 0/0 | equal to base | 55/55 |
| `d59093e95c` | 0/0 | rc 0 | 84/84 |
| `5597fca7cc` | 0/0 | rc 0 | 86/86 |

From `ac04957b95` onwards, the targeted tests include the check itself. The fork commits also run it on the MySQL rows.

**Superseded chains** (`/tmp/22c/superseded/`):
- **Round 1.** The fork chain was a merge plus three commits (`ed39922868`, now backup-a). It was collapsed into a merge plus one commit with the same final tree (`9658b5f7cb`; `chain-round1/tree-equality.txt`), then superseded by the review fixes.
- **Round 2.** A `d95815c0cc`..`d1ff0939dd`, fork `3ce7899eef`..`1f61eb54d9`. Superseded by N-A1. Its gate run stopped at `d95815c0cc`'s format rc 2, which led to the format decision below.

**Incident: one interrupted gate run.** An accidental `TaskStop` of the background task, at 2026-10-15T10:40:53+01:00, interrupted the round 1 gate run. It was during `5774b18c61`'s format gate; that commit's Release build had passed 0/0. The partial run is kept as `chain-round1/gates-interrupted-1/` with `INTERRUPTED.txt`, and was rerun. That chain was then superseded anyway (`STOPPED.txt`).

## The format gate and the replay rule

The 22b tip `d37f0d08fb`, and so branch A, fails `dotnet format` with 6 errors that 22c did not add:
- 2 × SA1611 in `tests/Jellyfin.Server.Implementations.Tests/Item/BaseItemRepositorySortNameFilterTests.cs:72`;
- 4 × SA1615 in `tests/Jellyfin.Server.Implementations.Tests/Postgres/PostgresMigrationAtomicityTests.cs:31,46,62,78`.

The fork fixed them in `62282ffd55`, which branch A does not contain.

**Decision** (`/tmp/22c/format-probe/DECISION.txt`):
- **Branch A.** Its format gate is "equal to base": every A commit must give exactly the base's normalised error set. The base set comes from a fresh worktree at `d37f0d08fb` (`base-fresh-noBuild.log`), and the A-tip run gives the identical set.
- **Fork commits.** These must reach rc 0.
- **Replay rule.** When `multidb/upstream` is built, `62282ffd55` is folded as a fixup into the steps that introduced those files, `bcdd6b9259` and `651c8dd720`. Branch A does not take its lines.

## Proofs

Every run below has `COMPLETE`, rc 0 on every gate, and Release with 0 warnings and 0 errors. Start and end identities are the exact tip, with empty status files, and the Docker added, removed and remaining-new-Testcontainers lists are empty.

**Fork at `5597fca7cc`** (`/tmp/22c/fork-proof/`): 14/14 gates, format rc 0.

| Backend | Implementations passed/skipped | Server.Tests passed/skipped | Integration passed/skipped |
|---|---:|---:|---:|
| SQLite 3.53.3 | 2408/111 | 156/9 | 137/11 |
| PostgreSQL 16.15 | 2440/76 | 146/18 | 144/6 |
| MySQL 8.4.11 | 2368/137 | 126/38 | 144/6 |
| MariaDB 11.4.13 | 2368/137 | 126/38 | 144/6 |

I counted the 12 TRX files from their XML: 11,300 rows, 10,707 passed, 593 skipped, none failed. The orchestrator's independent recount gave the same.

**Branch A at `70082dda51`** (`/tmp/22c/A-proof/`): format equal to base, with the same 6 errors and an empty diff from the base set (`format.verdict`).

| Backend | Implementations passed/skipped | Server.Tests passed/skipped | Integration passed/skipped |
|---|---:|---:|---:|
| SQLite 3.53.3 | 2065/87 | 144/8 | 137/11 |
| PostgreSQL 16.15 | 2097/52 | 139/13 | 144/6 |

6 TRX files: 4,903 rows, 4,726 passed, 177 skipped, none failed.

All Implementations `ProviderBackend_IsLive` and Integration `CreateClient_SelectedProvider` sentinels pass in both runs. Server.Tests provenance is companion-only (B21).

**Debug warnings.** Each comparison has 211 normalised warnings on both sides and an empty diff:
- fork: `/tmp/22c/fork-debug/`, base `693da13de8` against `5597fca7cc`;
- A: `/tmp/22c/A-debug/`, base `d37f0d08fb` against `70082dda51`.

**Negative controls** (`/tmp/22c/negative/fork` and `/tmp/22c/negative/A`). Each control edits the tree, runs the check, and is undone before the next. Every expected failure failed, every message names `dev/add-migration`, and each run ends green with a clean status.

| Control | What it does | Reported as |
|---|---|---|
| missing | Injects SQL into one migration. | Missing, with the entry to fill in |
| stale, extra entry | Adds an entry that matches nothing. | Stale |
| stale, altered entry | Changes an entry's direction. | Stale, plus the now-uncovered difference as Missing |
| normalised Down entries removed | Removes the six `Down` entries. | Missing: 6 |
| ambiguous | Duplicates one entry. | Ambiguous |
| unknown category | Puts an unknown category in an entry. | Unreadable: the parse error is wrapped and names the command |

Fork run 1 (`fork-run1-needle-bug/`, `NOTE.txt`) is kept and not counted. The check failed as intended, but the script's search string used unescaped quotes, while the printed entry is JSON with `\"` escapes. The script was fixed and rerun.

**Parity** (`/tmp/22c/parity/fork/`, `parity-result.json`). In a scratch worktree, `dev/add-migration ParityProbe` gave the same id, `20261015101737_ParityProbe`, on SQLite, PostgreSQL and MySQL. A name with no SQLite twin got fresh ids: `20261015101746` on PostgreSQL and `20261015101748` on MySQL. Each build reported 0 warnings.

## Review history

Relayed by the coordinator; the review notes are not in the evidence:
- **First review: FAIL.**
  - Blocker B1: a parse-time `JsonException` did not name the command.
  - Should-fix: S1 to S3, C-S1, and the docs.
- **Re-review: APPROVE** on both shapes.
- **N-A1.** A wording fix to the class comment about what is not compared was then folded in. It is comment-only. `/tmp/22c/chain3/NA1-A.diff` and `NA1-fork.diff` equal `git diff d1ff0939dd 70082dda51` and `git diff 1f61eb54d9 5597fca7cc`: one file, 4 lines added and 3 removed, the same lines in both shapes.
- **Optional follow-ups, not done:**
  - N-A2: a missing allowlist file is not wrapped. `File.ReadAllText` is called outside the parse `try`.
  - N-A3: some tests do not assert that the message names the command.

## Limits

- **What the check ignores.** Types, defaults, collations, filters, check SQL, new names, data values and order are not compared (see the list above).
- **SQL is compared as text.** Equivalent SQL with different text needs an entry, and the check does not prove equivalence.
- **CI.** No CI result at `5597fca7cc` is known, and none is claimed.
- **The format gate on branch A** is "equal to base" until the replay folds `62282ffd55` in.
