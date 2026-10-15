# Explainer: the upstream sync of 2026-10-15

These are study notes for understanding and defending this merge in your own words. Do not paste them as PR text.

- **Merge:** `81f29427e6` "chore: Merge pinned upstream master into multi-database candidate". Its parents are `d38fccdf44` (the fork after ci-fix-2) and upstream `aec42b345e`. It was built in its own worktree and fast-forwarded onto `feature/multi-db`. This worker made no push; the coordinator later reported fork CI green at this SHA.
- **Why it was needed:** S5 says to merge `upstream/master` into `feature/multi-db` whenever upstream moves. The fork's merge base with upstream had stayed at `61f9a9df23`. Upstream had been fetched at `e53a6d3e6a` on 2026-10-13, but never merged. That was a lapse, not a deliberate deferral (pr-plan B18).
- **Size of the upstream change:** seven commits and 15 paths since `61f9a9df23`. None touches `src/Jellyfin.Database`, `Jellyfin.Server.Implementations/Item`, `Jellyfin.Server/Migrations`, `FullSystemBackup` or CI. There is no new SQLite migration or routine, so nothing had to be generated or re-dated (S5, S9).

## What came in

- `619eb231b4` (#17879, with `d8805fd635`): the Microsoft pins to 10.0.12, Test.Sdk to 18.11.0, System.Text.Json to 10.0.13. A superset of our step 23.
- `054706333d` (backport of #18243): no crash when probing a stream that reports neither a usable aspect ratio nor a frame size (`ProbeResultNormalizer`), with two probe fixtures.
- `f624cb4cc5` (backport of #18309): no crash when a subtitle's detected charset has no .NET encoding (`SubtitleEncoder`).
- `34768404f7` (backport of #18343): a year off by one counts as a tie in TMDb result ranking.
- `e53a6d3e6a` (#17299): the fMP4 HLS subtitle timestamp map offset (`SubtitleController`, `DynamicHlsHelper`).
- `aec42b345e`: a Norwegian Bokmål translation.

## The conflict

Only `Directory.Packages.props` conflicted. The fork had added lines next to the pins upstream bumped. The resolution:

- **Upstream pins taken as they are.** The Microsoft packages at 10.0.12, `Microsoft.NET.Test.Sdk` 18.11.0, `System.Text.Json` 10.0.13.
- **Fork-only pins kept as they are.** `Microting.EntityFrameworkCore.MySql` 10.0.12, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Testcontainers.MariaDb`/`MySql`/`PostgreSql` 4.16.0, `xunit.v3.assert` and `xunit.v3.extensibility.core` 3.2.2.
- **Tools.** The fork keeps `dotnet-ef` at 10.0.12, from step 22b. Upstream is still at 10.0.11 and did not touch `.config/dotnet-tools.json`, so that file merged without a conflict.

`CONTRIBUTORS.md`: upstream added three names (#18243, #18309, #17299). The one from #18309 lands in the same hunk as ours (`shea786`), and the merge keeps both.

## The probe fixes

`ProbeResultNormalizer.cs` and its tests are changed on both sides:

- our X2 (`4f64caba7a`, the premiere date from the broadcast date tag);
- upstream's #18243 (the missing frame size).

The changes are in different places, so git merged them without a conflict, and both fixes and both sets of tests are kept. They are why the proof ran `Jellyfin.MediaEncoding.Tests` as well as the database suites.

## Why the merge is "pure"

The merge contains upstream's changes and our conflict resolutions, nothing else. Four files differ from both parents:

- `Directory.Packages.props`, the resolved conflict;
- `CONTRIBUTORS.md`, our name and upstream's in the same hunk;
- `ProbeResultNormalizer.cs` and its tests, both probe fixes.

No fork change was slipped into the merge, so the merge can be reviewed as "did we resolve the conflict correctly", and nothing more. A static review approved it on that basis.

## The proof

Run `/tmp/upstream-sync-20261015/run.Gqpr1O8j`, at `81f29427e6`. All 17 gates passed (rc 0), plus the complete marker.

- **Builds and format.** Release build; `dotnet format`; Debug build. Debug warnings: 211 unique, identical to the `d38fccdf44` baseline, with no new code, message or line shift. The Debug log's sha256 is recorded and serves as step 25's warning base.
- **Database suites, passed/skipped, 0 failed:**
  - SQLite: Implementations 2314/103, Server.Tests 152/8, Integration 137/11.
  - PostgreSQL: Implementations 2346/68, Server.Tests 146/14, Integration 144/6. The PostgreSQL integration suite was added to this proof because the upstream `Mvc.Testing` pin changed.
  - MySQL and MariaDB: Implementations 2251/147 and Server.Tests 104/55 each. No MySQL-family integration suite ran yet; that needed step 25.
- **Other suites.** `Jellyfin.MediaEncoding.Tests` 105/1, for the overlapping probe paths. The migration pairing and workflow tests also passed.
- **No containers touched.** No container of ours was left behind, and unrelated ones were not touched.

Fork CI on the synced tip was relayed as all green: format, Ubuntu, macOS, Windows, the PostgreSQL database job, ABI (no difference) and CodeQL. That run's log was not kept locally. The CI test jobs run every test project; the kept Windows log of the `d38fccdf44` run lists `Jellyfin.Api.Tests` among them. That project holds upstream's new subtitle controller and HLS helper tests, and the local proof did not run it.

## The subtitle triage

A code review of the incoming subtitle changes raised four points. They were triaged separately from the merge, with reproducing inputs:

- **X7, EXTINF uses the current culture.** `SubtitleController` appends `TimeSpan.TotalSeconds` (a `double`) to a `StringBuilder`, which formats with the current culture. Under fr-FR, a runtime of 45,000,000 ticks with segment length 10 gives `#EXTINF:4,5`. The same code is at `61f9a9df23`, so this is not a regression from the sync.
- **X8, the WEBVTT replace is global.** The timestamp map is injected with `text.Replace("WEBVTT", ...)`, so a cue containing "Say WEBVTT aloud" gets the map injected as well. The same pattern is at `61f9a9df23`.
- **X9, raw bytes for an unsupported charset.** #18309 added a guard: when the detector names an encoding .NET does not have, the stream is returned as is. Before the guard, the `StreamReader` refused such a file. For a parser-supported external SRT, those raw bytes are then decoded as UTF-8, and there is no later FFmpeg fallback. This was only read from the code; no test with the real detector has shown it yet. It is not claimed as a loss of support that worked before.
- **P3, a duplicate test.** The new test for an unsupported encoding writes ISO-8859-7, which an existing test already covers. Recorded only.

None of these blocked the sync. X7, X8 and X9 are separate fixes, after step 25, handled like X3: an upstream-shaped branch from `upstream/master`, failing test first, independent review, `--no-ff` merge. X9 is fixed only if a runtime test with a real ISO-8859-16 SRT, or whatever the detector actually reports, shows garbled output. Before each fix, check upstream: open #17012 and #16393 touch `SubtitleEncoder`. No upstream follow-up for any of the three was found on 2026-10-15.

The sync review also noted three points it did not verify, kept for later triage and not blocking:

- the fMP4 timestamp map in `DynamicHlsHelper.cs:724`;
- the adjacent-year TV score in `TmdbUtils.cs:254`;
- the `Range` upper bound of `vttTimestampMapMpegts` (`SubtitleController.cs:230`), which allows values beyond a 33-bit MPEG-TS timestamp.

## The gate rule

The sync blocks only on our own work: the conflict resolution, plus every suite green on the sync tip, upstream's tests included. A bug that upstream brought in, or that was already there, does not block the merge and is not fixed inside it. Each confirmed one becomes its own test-first X fix after the merge, so the merge stays pure and each fix can be offered upstream on its own. The S9 watch list records whether upstream already has a fix or a follow-up.

## Things worth knowing when defending it

- **Step 23 is now redundant.** #17879 is merged, and when `multidb/upstream` is built, step 23 is dropped (S9). The replay of the later steps must not touch those pins.
- **The fork's `dotnet-ef` tool is 10.0.12.** Step 22b bumped it (`d4d55bcada`). Upstream's Renovate PR #17878 would take upstream's tool to 10.0.13; it was open at the scan.
- **Rebases after the sync.** Step 25 was rebased onto `81f29427e6` with all nine commits unchanged. X5 and X6 were rebased too: `fix/activitylog-import-break` sits on `aec42b345e`, and its fork merge candidate `multidb/x5-merge-candidate` sits on the step 25 merge. Neither is merged yet.
