# Explainer: pre-mark missing setup migrations when history exists

These are study notes for writing and defending this PR in your own words. Do not paste them as PR text.

- **Branch:** `fix/migration-setup-premark`, one commit off `upstream/master`. It is not part of the multi-database stack.
- **Status:** ready for you. Not pushed.

## What the code does

When Jellyfin starts in setup mode (startup wizard not completed) or with `--mode SeedSystem`, `JellyfinMigrationService.CheckFirstTimeRunOrMigration` writes history rows for every code migration marked `RunMigrationOnSetup = false`. These migrations exist to convert old data, so a brand-new server has nothing for them to do. Writing their history rows up front, "pre-marking", means they never run later.

To pick which migrations to pre-mark, the code filtered with:

```csharp
.Where(e => !appliedMigrations.Any(f => f != e.BuildCodeMigrationId()))
```

That expression reads "no applied id differs from this one". It is true only when the history is empty, or holds exactly this one id. The intent was "this id is not in the history yet". The fix is one line:

```csharp
.Where(e => !appliedMigrations.Contains(e.BuildCodeMigrationId()))
```

This is the same form the file already uses elsewhere (`:161`). The reviewer checked the other membership tests in the method (`:161`, `:269`, `:435`), and they are correct.

## When the bug matters (be precise if asked)

- **A plain first start works.** The history is empty, so everything gets pre-marked.
- **A plain restart before finishing the wizard is harmless.** The first start already pre-marked everything and the other stages recorded their rows in the same history table, so nothing is missing. The broken filter selects nothing, and nothing needed selecting. Don't claim this case as the bug.
- **Realistic case: an upgrade before the wizard is completed.**
  - Someone installs version N, does not finish the wizard, then upgrades to N+1, which adds a new `RunMigrationOnSetup = false` migration.
  - The history is non-empty, so the new migration is not pre-marked. It later runs against the fresh database.
  - Usually that is a no-op. A migration that assumes legacy data exists could fail startup.
- **Realistic case: `--mode SeedSystem` on a database whose history lacks some ids**, for example one created by an older version. Same effect.
- **Theoretical case: duplicate insert.** When the history holds only that one id, the old filter selects it and inserts it again, which fails on the history table's primary key. Reaching this needs a crash straight after the first pre-mark insert. Mention it as a consequence of the expression, not as something users hit.

## Behaviour change a maintainer may ask about

With `--mode SeedSystem` against a populated, older database, the bug *happened* to let newly added data migrations run. Now they are pre-marked and skipped. That matches what SeedSystem is meant to do (seed the system regardless of existing state), but be ready to say so explicitly.

Side benefit: the pre-mark inserts are not in a transaction. With the fix, an interrupted pre-mark heals itself on the next start, because the missing ids get selected.

## How it was tested

- Two new tests in `tests/Jellyfin.Server.Tests/Migrations/JellyfinMigrationServiceTests.cs`. Each one runs setup once, removes some history rows, runs setup again, and expects the full history back:
  - `..._ServerWithoutCompletedSetupAndPartialHistory_...` removes one id. This simulates "a migration is missing while still in setup".
  - `..._SeedSystemOnSetUpServerWithSingleSeededMigration_...` leaves only one id and uses SeedSystem. This covers the duplicate-insert path.
- With the old line restored, both tests fail: the first because the missing id is not re-added, the second with `UNIQUE constraint failed: __EFMigrationsHistory.MigrationId`. The other 7 tests in the class still pass. The reviewer confirmed this independently.
- With the fix, `Jellyfin.Server.Tests` passes 41/0. The Release build has 0 warnings and `git diff --check` is clean.
- Reproduce: `dotnet test tests/Jellyfin.Server.Tests --filter "FullyQualifiedName~JellyfinMigrationServiceTests"`.

## Risks

Low. One predicate change, limited to setup and SeedSystem starts. A normal media-server start does not take this branch.

## Before you open the PR

- The commit message is factual and minimal. Reword it freely.
- The PR template has a **Code assistance** section. Jellyfin's LLM policy requires you to describe honestly how assistance was used.
- Consider whether this deserves an issue first. It is small enough to go straight to a PR.
