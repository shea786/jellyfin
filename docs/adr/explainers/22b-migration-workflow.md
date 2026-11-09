# Explainer: step 22b, the workflow for adding a database change

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/22b-migration-workflow`, 5 commits on the `feature/multi-db` tip `0c21be52e5`, final tip `d37f0d08fb`.
  - `d4d55bcada` Update dotnet-ef to match the Entity Framework runtime
  - `658b2837d5` Reuse the SQLite migration id for new PostgreSQL migrations
  - `aadf94ebde` Add a command that generates the migrations of every provider
  - `95edb3f6af` Check every provider's migrations in one theory
  - `d37f0d08fb` Describe how to add a database change
- **Status:** design brief, two implementation rounds, then a gate on the round 2 tip `ca439f976e` (CHANGES REQUIRED, one must-fix), folded in as round 3; a delta review of round 3 (CHANGES REQUIRED, two must-fixes) folded in as round 4; its delta check (APPROVE WITH NITS) folded in as round 5. Merged into `feature/multi-db` as `e481b3a834`, after steps 24 and 24b, with no conflicts. Post-merge verification: Release 0 warnings; Debug warnings equal the base apart from the CA1721 line shift; SQLite 2312/103, 147/8 and 137/11; PostgreSQL 2344/68 and 142/13 in two runs; MySQL and MariaDB 2249/147 and 100/54 each (17 more tests on every engine than before the merge); no containers left behind. Not pushed. Backups: `multidb/22b-backup-a` to `-h` (`-f` is the gated round 2 tip, `-g` the round 3 tip, `-h` the round 4 tip).
- **Relevant decisions:** ADR-0001 W1 to W7, pr-plan 22b/22c/22d and B17.
- **Upstream-bound.** It sits before group C in the upstream series. It touches only SQLite and PostgreSQL. MySQL and SQL Server join its table in their own steps (ADR-0001 W3).

## Why it exists

Before this step, adding a database change took several separate `dotnet ef` runs, one per provider, each with its own options, in a fixed order. The PostgreSQL migration then had to be renamed by hand so that its id matched the SQLite one. The readme named option spellings that the design-time factories never read. A contributor who only knows SQLite would get this wrong. The PostgreSQL migration would land with a fresh timestamp, and the pairing test would fail with a message that did not say what to do.

The goal (ADR-0001 W1) is one command, one id per change across every provider, and test failures that tell the contributor the next step.

## What each commit does

- **dotnet-ef 10.0.12.** The tool was older than the EF runtime and printed a version-mismatch notice on every run. Housekeeping, needed so the command's output is clean.
- **The id generator.** This is a design-time `IMigrationsIdGenerator` in the PostgreSQL provider (`PairedMigrationsIdGenerator`).
  - It finds the SQLite migration file with the same name by file name, case-sensitively, in the SQLite provider's `Migrations/` directory, and reuses its id. With no match, it falls back to a fresh id.
  - **Search by file, not assembly reference.** ADR-0011 does not let a provider project reference another provider. The directory is found by walking upwards from the provider assembly's output, so it works whatever the tool's working directory is.
  - **Public interface.** It implements the public interface, because EF's own generator is internal (EF1001).
  - **Registration.** It is registered through `PostgresDesignTimeServices` and `[assembly: DesignTimeServicesReference]`, because the tool only looks for `IDesignTimeServices` in the startup assembly.
  - **Warnings.** It logs a warning, which the tool prints, when the SQLite directory is missing or the name matches more than one migration. A name with no SQLite migration in a directory that exists is not reported. The generator cannot tell whether that is intended, and the pairing theory catches it.
- **`dev/add-migration <Name>`.** A .NET 10 file-based app, so one script runs on every platform. It builds each project, then runs `dotnet ef` with `--no-build`, because `dotnet ef` hides compiler errors. It generates SQLite first, then every provider in its table, and checks that all got the same id. Nothing connects to a database.
  - **Resume.** A provider that already has `<id>_<Name>.cs` is skipped and reported. So a run that stopped half way, or a contributor who deleted one provider's files to regenerate them, can repeat the same command.
  - **Refusals.** The command refuses before generating anything if only a non-reference provider has the name, if the existing ids differ, or if every provider already has it ("choose a new name").
- **One theory over a provider table.** `MigrationProvider` lists each provider once, with a context that never connects, and the baseline of each provider that mirrors SQLite. Two theories run over the table:
  - each model has no pending changes;
  - each mirroring provider's migrations after its baseline carry exactly the ids of the SQLite migrations it does not cover.

  A row without a baseline fails with a message; it is not skipped. Failure messages name `dev/add-migration` and give a recovery path that works offline: delete that provider's migration files, restore its snapshot with git, and rerun the command.
- **Readme.** `src/Jellyfin.Database/readme.md` becomes "Adding a database change". It covers the one command, what each kind of change needs, what to do when a query behaves differently on one provider, where the tests run, and how to recover from a failing migration test without a server. It replaces the advice to run the tools as root with a note to fix the ownership of the checkout.

## What the gate found and what was done

- **Must-fix: the recovery advice led to a dead end.** The theory's mismatch message suggested two paths:
  - `dotnet ef migrations remove` on PostgreSQL, which needs a live server;
  - deleting only the PostgreSQL migration and rerunning the command, which stopped at SQLite because the name was taken.

  The gate tried both. Round 3 made the command resume, as described above, and rewrote the messages to give the path that now works.
- **Round 4, from the delta review of round 3:**
  - A resume accepted any name SQLite already had, so reusing the name of an old SQLite migration generated only PostgreSQL, under the old id, and reported success. The command now resumes only when the SQLite migration of that name is the newest one, and otherwise refuses with "choose a new name".
  - The mismatch message described a case that can no longer happen, and the readme's recovery step undid half of a resumed run. The command now prints the files it created and the snapshots it updated before it checks the ids, and the message tells the contributor to undo exactly those.
  - Nits: a name that differs only in case is refused up front; "restore the snapshot" became "restore it to the commit before the migration", which also works once the migration is committed; the cheaper recovery path is offered first.
- **Round 5, from the delta check of round 4 (APPROVE WITH NITS):** the "not the newest" refusal now runs before the ids check, so the cheap path offered there can no longer lead into it; the refusal also says what to do when the older migration is the contributor's own, left behind by a rebase; a duplicated comment went. Only the script and the readme changed, so the round 4 test runs at the tip still stand.
- **Smaller items, also folded in:**
  - the generator is registered as scoped, matching the logger factory it depends on, with a test that builds the services with scope validation on;
  - the baseline check compares by reference;
  - file-name matching is case-sensitive on every platform.

## Things to be ready to defend

- **Why a `dev/` directory?** Upstream has none. The command is a contributor tool, not part of the product, and a file-based app avoids a shell and a PowerShell copy. Say so in the PR text.
- **Why a design-time service instead of a naming convention?** EF picks the migration id; the only supported hook is `IMigrationsIdGenerator`. Renaming files after generation would also need the designer attribute and the snapshot to be edited.
- **The warning depends on EF forwarding Warning-level design-time logs.** It does today. If it stops, the pairing theory still catches the mismatch, so the safety does not rest on the warning.
- **The diff is larger than first planned** (about +900/-50 against about 400). The growth is tests (generator, design-time services, theory) and the readme, not production code.
- **What comes next:** 22c compares the operations of paired migrations across engines, with an allowlist for one-sided `Sql`. 22d bans raw SQL and provider sniffing outside provider projects, and runs the conformance kit on SQLite and PostgreSQL.
