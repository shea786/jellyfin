# Explainer: X5 activity-log import and X6 retry identities

These are fork-internal study notes, not upstream PR text. They describe the local merge and retained proof as of 2026-10-15.

**Limitation: these fixes do not repair an already-completed upgrade that lost activity entries.** If the old routine stopped early and the migration was recorded as applied, the missing entries remain only in `activitylog.db.old`. Recovery is deferred and outside this fix. You must not read the successful retry test as a recovery path for those servers.

## Commits and merge

Two independently testable changes fix the same legacy routine, `Jellyfin.Server/Migrations/Routines/20250420070000_MigrateActivityLogDb.cs`:

| Change | Upstream-shaped commit | Fork commit |
|---|---|---|
| X5: keep importing after resolving a legacy user ID | `7ce3511a8415c11774f357c544b19abaea8321bc` | `cecac3f9b571eb45cbf17ff0d1e76e02460b5531` |
| X6: reset the retry identity counter for the correct table | `b4642f0dbff58569533a28da147227f210d8c52e` | `47b844674bd058d8fcb1a659e569a6ba96cd1039` |

The upstream-shaped pair starts at `aec42b345e`; the fork pair starts at the step 25 merge, `abf45dcad2`. Both rebases retain two `=` entries in their range-diffs against the earlier pairs. They are separate shapes because upstream does not have the fork's X4 pooling fix: the upstream-shaped routine leaves those connections unchanged, while the fork retains `Pooling = false` on both legacy connections. X5/X6 do not introduce or remove X4.

The fork pair is locally merged by `a5f1e5b2c91dd9c3b0f9c0e5a364ac396c980108`, whose parents are docs commit `39026f46fb479fb4977b16f3f0b9ce9468f3554b` and fork tip `47b844674bd058d8fcb1a659e569a6ba96cd1039`. The merge tree is `ffdc2ebf34f8e7b698eb13df3a5860f08bd45b8f`; its code matches the candidate (the difference from the candidate is fork documentation). This is a local fork merge, not an upstream merge.

## X5: successful user lookup must not stop the import

The source activity database can contain a Guid user ID or an integer ID from an old Emby schema. For an integer ID, the routine looks up the Guid in `users.db`, in `LocalUsersv2`.

Previously, a successful lookup executed `break`. That left the outer activity-entry loop before it created even the entry whose user had just been resolved. The routine then saved only the earlier entries and archived the legacy database as though the import were complete.

X5 removes that exit. A successful lookup supplies the resolved Guid and the loop continues normally; a missing or unreadable lookup explicitly uses `Guid.Empty`. The fix does not change the fallback meaning of an unresolved user.

The regression fixtures in `tests/Jellyfin.Server.Tests/Migrations/MigrateActivityLogDbTests.cs` bind their values as parameters, including Guid values represented as both TEXT and BLOB. They check:

- three entries, with the integer user ID in the middle: all three names and user IDs survive, for both Guid representations;
- a missing legacy user: all entries survive, with `Guid.Empty` for the unresolved user.

With production X5 reverted, both successful-lookup cases fail: expected `first`, `second`, `third`, but only `first` is imported. With the fix, all three X5 cases pass on both branch shapes.

## X6: retry from identity one

Before importing, the routine removes target rows left by an interrupted attempt. It then resets `sqlite_sequence`, but used the source table's singular name, `ActivityLog`. The EF target table is plural, `ActivityLogs`, so the update matched no sequence row.

X6 changes that one table name. Its separate regression test seeds five target entries, runs an import with two source entries and asserts IDs `[1, 2]`. Without X6 the actual IDs are `[6, 7]`; with X6 the test passes. This verifies an interrupted-import retry, not repair of a migration already marked complete.

The four final regression cases are SQLite-only. The routine directly uses `sqlite_sequence`; passing the server-provider suites does not mean this routine runs against those providers.

## Premerge verification

Retained evidence: `/tmp/x5/rebase-20261015/run.080031.2985950/`.

- `return-codes.tsv`: all four per-commit legs (upstream X5/X6 and fork X5/X6) pass Release build, format and their test gates. Each `release.log` reports zero warnings and zero errors. The upstream-shaped legs run Server.Tests and Implementations on SQLite; each fork leg runs Implementations, Server.Tests and Integration on all four providers.
- The four negative-control legs fail as expected. Their `expected-failure-check.txt` files and TRX results pin the X5 truncated list and X6 `[6, 7]` mismatch, rather than accepting an arbitrary failing command.
- `regression-results.tsv`: the X5 cases and the additional X6 case pass on the corresponding fixed tips.
- Exact-base/final-tip Debug comparisons for each branch shape contain 211 normalized warnings on each side, with empty normalized diffs and comparison exit code zero. These are equality checks, not warning-free Debug builds.
- The coordinator reports a direct independent static **APPROVE**, with no blockers. That verdict is attributed here; the retained build and test evidence is separately inspectable.

## Postmerge verification

Retained evidence: `/tmp/x5/postmerge/run.GmZeHdZp/`.

`COMPLETE` is present and all 14 gates in `results.tsv` have rc 0: Release build, exact format and 12 database suite runs. Release reports zero warnings and zero errors. Independent XML counting of the 12 TRX files gives 11,212 result rows: 10,619 passed, 593 skipped (`NotExecuted`), zero failed.

| Backend | Implementations passed/skipped | Server.Tests passed/skipped | Integration passed/skipped |
|---|---:|---:|---:|
| SQLite 3.53.3 | 2386/111 | 156/9 | 137/11 |
| PostgreSQL 16.15 | 2418/76 | 146/18 | 144/6 |
| MySQL 8.4.11 | 2346/137 | 126/38 | 144/6 |
| MariaDB 11.4.13 | 2346/137 | 126/38 | 144/6 |

All four Implementations `ProviderBackend_IsLive` and Integration `CreateClient_SelectedProvider` sentinels pass. Server.Tests still has no sentinel of its own: its provider provenance is companion-based, not independently sentinel-proven (pr-plan B21).

The retained start/end identities are the exact merge SHA, with empty status files in the detached proof worktree. The main-checkout observations also record that SHA with clean status. Docker reconciliation records empty added, removed and remaining-new-Testcontainers lists.

## CI at the merge

Fork CI ran on PR #1 with head `a5f1e5b2c9`. The orchestrator read the check runs read-only through `gh api` and reports every completed run as success:

- `format-check`;
- `run-tests` on `ubuntu-latest`, `macos-latest` and `windows-latest`;
- `run-tests-db` for PostgreSQL;
- ABI: BASE, HEAD, Difference, Common Ancestor, Generate Report, and the Base and Head artifact jobs;
- CodeQL, with Analyze (csharp).

The workflow-housekeeping jobs (Rename, Rebase, Release project board, main) were skipped. These results are relayed; this documentation worker did not read the check runs, and no CI log is kept locally.

CI scope is narrower than the local matrix. Its only database job is PostgreSQL, so the MySQL 8.4.11 and MariaDB 11.4.13 evidence for this merge is still the local postmerge run above. The three `run-tests` jobs passed on Ubuntu, macOS and Windows; they do not prove each provider on each platform. This documentation worker made no push.
