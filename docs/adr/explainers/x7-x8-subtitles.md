# Explainer: X7 and X8, the HLS subtitle fixes

These are fork-internal study notes, not upstream PR text. They describe the local merges and the retained proof as of 2026-10-15.

X7 and X8 came out of the subtitle triage after the upstream sync ([sync explainer](sync-upstream-20261015.md); pr-plan B22). Both bugs were already present at `61f9a9df23`, so neither is a regression from the sync. Both touch `Jellyfin.Api/Controllers/SubtitleController.cs` and its tests only. X9 was a separate question; it is now closed without a fix (see the end).

## Commits and merges

| | Upstream-shaped, on `aec42b345e` | Fork candidate | Fork merge |
|---|---|---|---|
| X7 | `837cf7803252994dadf5cf8c9fa6b33c02794b46` (`fix/hls-extinf-invariant-culture`) | `68ea0d1249e37953323e2d27c234c4e2417d0795`, on `a5f1e5b2c9` | `0f118c6b7cdb38ac35412441349170a24d7be080`, parents `37f86395df` and `68ea0d1249` |
| X8 | `34dc00973a0ac9b485eed55552ddea79aadff97a` (`fix/vtt-timestamp-map-header-only`) | `96b3b718a9b621a5e8f8516f1cb6e974263d6068`, on `0f118c6b7c` | `5d8b85e832b562c8b9db3cfb6f4db6c5860e3d7e`, parents `0f118c6b7c` and `96b3b718a9` |

- **X7.** The fork commit is the same patch as the upstream-shaped one: both have stable patch-id `48443ff0efa59ed0fc1e1c4945317300c5cf3a27`, and the range-diff shows `=`.
- **X8.** The fork commit drops one hunk, the `using System.Text;` that the test file already gained from X7. The production change and the test method are unchanged; the range-diff shows only that removed hunk.
- **X8 history.** The final upstream-shaped commit `34dc00973a` is a message-only amend of `cd12b4dcdc`: both have tree `f1be4647ed`. The earlier versions are kept as backup branches, `fix/vtt-timestamp-map-header-only-v1` (`0e3275af52`) and `-v2` (`cd12b4dcdc`).
- **Merge trees.** The X8 merge tree `cd096ce8f9` is identical to the candidate's tree. For X7, the merge differs from the candidate only in `docs/adr` files.

Both fork merges, X7 `0f118c6b7c` and X8 `5d8b85e832`, are on the fork's origin `feature/multi-db`, the head of PR #1, as confirmed read-only with `git ls-remote`. The coordinator pushed them; the orchestrator and the docs writer pushed nothing. The upstream-shaped branches, `fix/hls-extinf-invariant-culture` and `fix/vtt-timestamp-map-header-only`, are local and unpushed. Neither fix has been offered upstream.

## X7: `#EXTINF` durations under a comma-decimal culture

`GetSubtitlePlaylist` built each segment line with `StringBuilder.Append(double)` on `TimeSpan.TotalSeconds`. `Append(double)` formats with the current culture, so under fr-FR a fractional duration came out as `#EXTINF:4,5,`. That is not a valid EXTINF duration. It shows on the last segment of any item whose runtime is not a multiple of the segment length.

The fix formats the value with `CultureInfo.InvariantCulture`. The new test sets `CultureInfo.CurrentCulture` to fr-FR, restores it in `finally`, builds the playlist for a 45,000,000-tick runtime with segment length 10, and asserts the single `#EXTINF` line.

- **Red.** Expected `#EXTINF:4.5,`, actual `#EXTINF:4,5,`. This is recorded both for the upstream shape (`/tmp/x7x8/run.083212.3263076/x7-red.log`) and for the fork (`/tmp/x7x8/fork-x7.Ep7y8RGB/red-audit.json`, one test, one failure).
- **Green.** The test passes with the fix.

## X8: the WebVTT timestamp map goes after the whole header line

With `AddVttTimeMap`, `GetSubtitle` added the `X-TIMESTAMP-MAP` line with `text.Replace("WEBVTT", "WEBVTT\n" + map)`. That replaced every occurrence. A cue reading "Say WEBVTT aloud" was split by a second map line, and header text after the signature, such as `WEBVTT - English`, was split as well. The original `Replace` already did this; the fix addresses both.

The fixed behaviour:

- **Guard.** The map is added only when the text starts with `WEBVTT` (ordinal `StartsWith`). Input that does not start with `WEBVTT` gets no map; this is accepted behaviour.
- **Placement.** The map goes after the first LF, so it follows the whole first line. If a CR comes before that LF, the map line ends with CRLF; otherwise it ends with LF.
- **No newline.** If the text has no LF at all, an LF and the map are appended after the complete header.
- **BOM.** The stream is read with `new StreamReader(stream)`, which consumes a byte-order mark, so a BOM does not defeat the `StartsWith` check.
- **Not supported: bare CR line endings.** An all-CR file gets the map appended at the end. A CR-terminated header followed by a later LF gets the map after that later line.

The theory `GetSubtitle_AddVttTimeMap_OnlyChangesHeaderLine` asserts whole expected outputs, so exactly one map and unchanged cue text are checked, not just the presence of a map:

- plain `WEBVTT` and `WEBVTT - English` headers, each with LF and with CRLF, each with the cue "Say WEBVTT aloud";
- the two header-only inputs with no newline. These cannot contain a cue, so they are separate cases.

**Red: 5 failed, 1 passed, on both shapes.** The one case that passes before the fix is header-only `WEBVTT`. The old `Replace` already produced the expected output there, so that pass is correct, not a hole in the test. With the fix, 6 of 6 pass.

**Why v1 was replaced.** v1 (`0e3275af52`) added the `StartsWith` guard but inserted the map straight after the six-character signature. That still splits `WEBVTT - English`. Its tests used only a bare `WEBVTT` header (with LF and CRLF), so they passed. The final tests add header text after the signature and compare whole outputs, which is what catches that defect.

## Verification

### Upstream-shaped branches

- **X7** (`/tmp/x7x8/run.083212.3263076/`, head `837cf78032` on `aec42b345e`): red rc 1, green rc 0, Release build 0 warnings and 0 errors, format rc 0. `Jellyfin.Api.Tests` 166 passed, `Jellyfin.MediaEncoding.Tests` 103 passed / 1 skipped.
- **X8** (`/tmp/x7x8/x8-rework.27F14TMJ/`, tested commit `cd12b4dcdc`, the same tree as final `34dc00973a`):
  - red 5 failed / 1 passed, then green 6/6;
  - final build 0 warnings and 0 errors, and format rc 0;
  - `Jellyfin.Api.Tests` 171 passed, `Jellyfin.MediaEncoding.Tests` 103 passed / 1 skipped.

  `green-initial-analyzer.log` keeps an earlier green attempt that stopped on CA1307 at `IndexOf(char)`. The final code uses `IndexOf('\n', StringComparison.Ordinal)`.

### Fork candidates

Both candidates passed all 16 gates (rc 0):

- Release build, with 0 warnings and 0 errors;
- format;
- `Jellyfin.Api.Tests` and `Jellyfin.MediaEncoding.Tests`;
- Implementations, Server.Tests and Integration on SQLite, PostgreSQL, MySQL and MariaDB.

The start and end identities match the candidate SHA, with empty status files. The Docker reconciliation lists are empty.

- **X7** (`/tmp/x7x8/fork-x7.Ep7y8RGB/`): API 166, MediaEncoding 105/1. Debug comparison against `a5f1e5b2c9`: 211 normalized warnings on each side, and an empty diff.
- **X8** (`/tmp/x7x8/fork-x8.aHsRRuVk/`): red 5/1 on `0f118c6b7c` with the tests applied. API 172, MediaEncoding 105/1. Debug comparison against `0f118c6b7c`: 211 on each side, and an empty diff.

  `preparation-attempt1/` keeps an earlier red attempt whose test patch was empty. Its filter matched no test and it exited 0, so it is not counted as a red. `test-patch-adaptation.txt` records the hunk removal the real attempt used instead.

The Debug comparisons show that no warning was added or moved. They do not mean the Debug build is warning-free.

The fork's MediaEncoding suite has two more tests than upstream's (106 against 104), so 105/1 and 103/1 are both full passes.

### Post-merge

| Merge | Run | Gates | TRX files | Result rows | Passed | Skipped |
|---|---|---|---:|---:|---:|---:|
| X7 `0f118c6b7c` | `/tmp/x7x8/postmerge/postmerge-run.yfdrTbhq/` | 16/16 rc 0, `COMPLETE` | 14 | 11,484 | 10,890 | 594 |
| X8 `5d8b85e832` | `/tmp/x7x8/postmerge/x8-postmerge-run.q05sdoYe/` | 16/16 rc 0, `COMPLETE` | 14 | 11,490 | 10,896 | 594 |

Neither run has a failed row. I counted the rows from the TRX XML myself.

- **X7 split.** The 12 provider TRX files hold 11,212 rows: 10,619 passed, 593 skipped. The other two are API (166 passed) and MediaEncoding (105/1).
- **X8 split.** API rises to 172 passed. The provider files are unchanged.

| Backend | Implementations passed/skipped | Server.Tests passed/skipped | Integration passed/skipped |
|---|---:|---:|---:|
| SQLite 3.53.3 | 2386/111 | 156/9 | 137/11 |
| PostgreSQL 16.15 | 2418/76 | 146/18 | 144/6 |
| MySQL 8.4.11 | 2346/137 | 126/38 | 144/6 |
| MariaDB 11.4.13 | 2346/137 | 126/38 | 144/6 |

These provider counts are the same in both runs and match the X5/X6 post-merge run. X7 and X8 add API tests only.

Other facts from both runs:

- Release reported 0 warnings and 0 errors, and format passed.
- All four `ProviderBackend_IsLive` and `CreateClient_SelectedProvider` sentinels passed.
- Server.Tests has no sentinel of its own, so its provider provenance is companion-only (pr-plan B21).
- The start and end identities are the exact merge SHAs, with empty status files.
- The Docker added, removed and remaining-new-Testcontainers lists are empty.

### Review and CI

- **Review.** The coordinator reports an independent static APPROVE:
  - for X7, at `837cf78032` and at `68ea0d1249`;
  - for X8, at `cd12b4dcdc` (the same tree as `34dc00973a`) and at `96b3b718a9`.

  The review notes are not in the evidence directories, so these verdicts are attributed, not checked here.
- **CI at `0f118c6b7c` (X7).** The orchestrator read the check runs with `gh`. It reports `format-check`, `run-tests` on Ubuntu, macOS and Windows, `run-tests-db` for PostgreSQL, the ABI jobs and CodeQL as successful. These results are relayed; no CI log is kept locally. CI's only database job is PostgreSQL, so the MySQL and MariaDB evidence is the local matrix.
- **CI at `5d8b85e832` (X8).** Pending when this was written. The orchestrator read the check runs with `gh`: `format-check`, the ABI jobs and the artifact jobs had completed with success, while `run-tests` on Ubuntu, macOS and Windows, `run-tests-db` for PostgreSQL and Analyze (csharp) were still in progress. No CI result at this merge is claimed.

## Format workspace notice

`dotnet format --verify-no-changes --verbosity diagnostic` exited 0 and formatted 0 of 2382 files, and the tree stayed clean (`/tmp/x7x8/fork-x7.Ep7y8RGB/format-diagnostics/`). Its one workspace-load diagnostic is "Found project reference without a matching metadata reference" for `src/Jellyfin.CodeAnalysis/Jellyfin.CodeAnalysis.csproj`.

That project is added as an analyzer `ProjectReference` in Debug only (`Directory.Build.props:23-24`, `OutputItemType="Analyzer"`). It is a notice from loading the workspace, not a format failure. It is not fixed.

## Not covered

- **Bare-CR WebVTT.** This line ending is not supported (see above).
- **Non-`WEBVTT` input.** It gets no map, by design.
- **Upstream.** Nothing has been offered upstream. The B23 scan found no upstream follow-up for either bug on 2026-10-15.
- **X9.** Closed on 2026-10-15 with no fix in this series. A runtime probe with real files and the real UTF.Unknown 2.7.0 on .NET 10.0.12 (`/tmp/x9-probe/`) did not reproduce it as a #18309 regression. Files detected as `iso-8859-16` or `iso-8859-10` were already garbled before #18309, because `StreamReader` falls back to UTF-8 instead of throwing. After it, srt to vtt output is byte-identical and srt to srt returns the raw bytes. The underlying defect, and #18309's misleading comment and unreached test, are upstream-note candidates (pr-plan B22, B26).
