# Explainer: step 20b, the sort name prefix filter

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/20b-sort-name-ascii-fold`, one commit on the `feature/multi-db` tip `227c08919e`.
  - `bcdd6b9259` Match the sort name prefix filter on ASCII case only
- **Status:** one review round (FAIL on one blocker, the first version changed results), fixed by amending, delta-checked. Merged into `feature/multi-db`. Not pushed. Backup: `multidb/20b-backup-a` (the reviewed first version `660e34a692`).
- **Relevant decisions:** ADR-0005 R13 and R14.
- **No schema change, no routine, no public API, and no change in results on SQLite or PostgreSQL.**

## Why it exists

Step 20 removed SQL `lower()` from the mixed-case columns that matter, but one use of it was left: the `NameStartsWith` filter on the sort name, which is how the library alphabet picker works. It compared `SortName.ToLower()` with the lowered term, and `ToLower()` becomes the engine's own `LOWER()`. On SQLite and on PostgreSQL with collation "C" that folds ASCII letters only, but on MySQL, MariaDB and SQL Server it folds Unicode, so the same request would return different rows.

## What it changes

- `NameStartsWith` now calls `JellyfinDbFunctions.LikeIgnoreAsciiCase` with a prefix pattern. The pattern is built from the term lowered in .NET, as the old code did, so on SQLite and PostgreSQL the SQL is the same comparison as before (ASCII-lowered column, Unicode-lowered term) and returns the same rows. A provider that translates `LikeIgnoreAsciiCase` with an ASCII-only fold gets the same rows on any engine.
- `LikePatterns.StartsWithIgnoreAsciiCase` builds the pattern. It escapes `\`, `%` and `_` (a prefix is literal), drops NUL and lowers ASCII letters. It is internal, since the assembly is on the compatibility list and already shows its internals to its two consumers. `ContainsIgnoreAsciiCase` keeps its output exactly (the reviewer compared 200k random strings and every single character).
- The range filters `NameStartsWithOrGreater` and `NameLessThan` still use `ToLower()`. A comment says so.

## What the review found

The first version built the pattern from the term with only its ASCII letters lowered. It was the same on ASCII, but an upper-case non-ASCII term (`E` with an accent, `O` with an umlaut, the Kelvin sign) stopped finding a lower-case stored name. Stored sort names are mostly lower-case, because the alphanumeric branch of `GetSortName` lowers them, so this would have broken the picker for accented letters. I had described that version as a single change on SQLite; that was wrong. The fix keeps the .NET lowering of the term. The tests now pin the old results and pass 33 of 33 on the unchanged base commit on both engines, which is the proof that nothing changed.

## Tests and what they are for

- `BaseItemRepositorySortNameFilterTests` (33): ASCII case, literal `%`, `_` and `\`, the non-ASCII cases above, an upper-case negative theory, and pins for both range filters with ASCII terms.
- `LikeIgnoreAsciiCaseBehaviourTests` (24) and `LikePatternsTests`: run `LikeIgnoreAsciiCase` on whichever provider is selected against the characters other engines fold to ASCII (Kelvin sign, dotted capital I, the sharp s and its capital). The expected rows are written out, not computed. These are the tests the MySQL/MariaDB and SQL Server providers must pass.

## Counts (passed/skipped, 0 failures)

Base `227c08919e` to the tip: SQLite Implementations 1898/87 to 1971/87; PostgreSQL Implementations 1930/52 to 2003/52; Server 144/8 and 139/13 and SQLite Integration 137/11 unchanged. The 73 new tests are all in the three files above. Release 0 warnings; Debug warnings equal the base apart from the accepted SessionManager line shift. Logs: `/tmp/step20b/` and `/tmp/step20b/fold1/`; the reviewer's probes and mutations are in `/tmp/gate20b/`.

## Limits and for the next steps

- A non-ASCII term in the two range filters is a known gap on engines whose `LOWER()` folds Unicode. The known-gap ledger needs a provider to key it to, so steps 24 and 27 add the entry (their rows in `pr-plan.md` say so), together with the requirement that their `LikeIgnoreAsciiCase` translation folds ASCII only and passes the two behaviour test files.
- Plans do not change: no index serves either form of the filter.
