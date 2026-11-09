# Explainer: step 24b, the date played order on MariaDB

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/24-dateplayed-fix`, 2 commits on the `feature/multi-db` tip `0d5e538f5e`, final tip `13144d7bf5`.
  - `c0b578c14b` Rewrite Max and Min over a union for MariaDB
  - `13144d7bf5` Test the date played order on every engine
- **Status:** design decision after a measured comparison of shared rewrites, then a rebase onto the merged step 24 and a delta gate (APPROVE, no blockers and no should-fix; it confirmed the identical tree, NULL and tie equivalence, MariaDB-only registration and thread safety). Merged into `feature/multi-db` as `52cdc45a5c`. Not pushed. Backups: `multidb/24b-backup-a` (the tip before the rebase, `ff02bffa41`) and `-b` (the first rebased tip, `fb9be81787`, which has the same tree as the final tip).
- **Relevant decisions:** ADR-0011 F2 (shared interceptor instances) and F7 (this step), ADR-0001 option 2c and E1, ADR-0002 H14, pr-plan B16.

## Why it exists

Sorting by date played reads, for each item, the newest play date of the item and of its alternate versions. EF Core translates `Max` over `Concat` of two sequences into a scalar subquery that selects from a `UNION ALL` derived table, and both halves of the union read the outer item's id. MariaDB cannot resolve an outer column inside a derived table, so the query failed with "Unknown column 'b.Id' in 'WHERE'". Step 24 recorded this as the known gap `MARIADB-OUTER-REF-DERIVED`. MySQL 8.4, SQLite and PostgreSQL run the query as it is.

## How the fix was chosen

- **A shared rewrite first (ADR-0001 E1).** The preferred fix is one query that every engine runs. Sixteen shapes were measured (C0 to C8 and their sub-variants). All of them were still a correlated subquery per row, so none improved on the original on SQLite or PostgreSQL.
- **A pre-aggregated join (C9)** removes the per-row subquery, but it aggregates every user's data before filtering. The cases that pick a few items (the children of one folder, a search for 1% of the library) went from 0 to 9 ms to 47 to 276 ms, and EXPLAIN showed no filter pushdown on SQLite or PostgreSQL. It failed the merge bar, so no shared commit was made.
- **Chosen: a rewrite inside the MySQL provider, for MariaDB only.** The shared query stays as it is. SQLite, PostgreSQL and MySQL 8.4 keep the same SQL byte for byte. This is a provider convention, not a dialect object, so it fits option 2c.

## What the code does

- **The identity used.** The `Max` of a union is the greater of the `Max` of each part, treating null as "no value". `Min` is the same with the lesser. Each part is a plain scalar subquery that MariaDB resolves, and that can seek an index with the outer column.
- **The interceptor.** `UnionAggregateInterceptor` is an `IQueryExpressionInterceptor`. Before translation it looks for `Max` or `Min` over exactly two concatenated sequences, with or without a selector, whose result is a nullable number or a nullable `DateTime`. It replaces that call with a conditional over the two aggregates. The second part is tested first, because it is the one that is usually empty (most items have no alternate versions), and then the comparison is skipped.
- **Only MariaDB.** It is registered in `MySqlDatabaseProvider` only when the server flavour is MariaDB. The flavour comes from the `server-version` option or from the version the server reports (ADR-0011 F6). It is one shared instance, like the list interceptor of F2, so EF does not build a service provider per context.
- **Everything else is left alone.** A non-nullable result, `Guid` and string results, an aggregate with no union, `Count`, `Sum` and `Average` over a union, a union with an in-memory list, an overload with a comparer, and the same query on MySQL all come through unchanged. A test checks each one.
- **The gap is gone.** `MARIADB-OUTER-REF-DERIVED` is removed from the ledger, with its ledger test and the error helper that only it used. The null-order test for date played now runs on every engine.

## Tests

- `MySqlUnionAggregateInterceptorTests` (in the first commit, with the code it covers): the rewrite on MariaDB, each case that must stay unchanged, and the date played query on MySQL, which must still contain `UNION ALL`.
- `BaseItemRepositoryDatePlayedOrderTests`, on all four engines: an item takes the newest of its own date and its alternate versions' dates, with own date only, alternate only, both and neither; the descending order is the reverse of the ascending one; with a user only that user's progress counts, and without one every user's counts.

## Evidence

- **Per commit,** on SQLite, PostgreSQL, MySQL and MariaDB, both test projects: every leg passes and the Release build has 0 warnings. The first commit alone is green on MariaDB, because the gap removal moved into it.
- **Final tip, a fresh run:** SQLite 2295 passed and 103 skipped, 147 and 8, 137 and 11 (integration); PostgreSQL 2327 and 68, 142 and 13, the same in two runs; MySQL and MariaDB 2232 and 147, 100 and 54 each. Release 0 warnings; the Debug warnings equal the base apart from the accepted `SessionManager` CA1721 line shift.
- **Benchmark,** median of 11 runs, the date played order on the full library, the children of one folder and a 1% search:
  - MariaDB with the rewrite: 184, 0 and 34 ms at 100k items; 218, 0 and 35 ms at 300k.
  - MySQL, untouched: 168, 0 and 35 ms at 100k, the same as before within noise.
  - The shared SQL for SQLite, PostgreSQL and MySQL was compared before and after and is identical.
- **Post-merge verification** on the merge `52cdc45a5c`: Release 0 warnings; Debug warnings equal the base apart from the CA1721 line shift; SQLite 2295/103, 147/8 and 137/11; PostgreSQL 2327/68 and 142/13 in two runs; MySQL and MariaDB 2232/147 and 100/54 each; no containers left behind.

## Limits and follow-ups

- **Nested unions grow fast.** A union inside a union is rewritten recursively, and each level makes about three copies of the aggregate of the level below (`UnionAggregateInterceptor.cs`, the conditional at the end of the rewrite). The only caller today has one union. A depth guard or a comment is a cheap follow-up, noted for step 27a.
- **Other aggregates over a union** (`Count`, `Sum`, `Average`) and non-nullable results are not rewritten and would still fail on MariaDB if they read the outer row. No such query exists today.
- **NaN.** In memory, `NaN` compares differently from `Max`. It cannot occur in these columns, and the query runs on the server, so it does not matter here.
- If MariaDB learns to resolve outer columns in derived tables, the interceptor can go. The MariaDB-only registration keeps it easy to drop.

## Questions a reviewer might ask

- *Why not change the shared query?* Every shared shape that MariaDB accepts was slower on SQLite and PostgreSQL, or no better. The merge bar was no regression on those engines.
- *Is the result the same?* Yes. The greater of two maxima is the maximum of the union, null meaning no rows. The four-engine tests check the order, including items with no date.
- *Why test the second part first?* It is usually empty, so the conditional ends after one cheap seek.
