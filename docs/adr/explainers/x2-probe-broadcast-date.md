# Explainer: X2, read the premiere date from the broadcast date tag

These are study notes for understanding and defending this fix in your own words. Do not paste them as PR text.

- **Branch:** `fix/probe-broadcast-date`, one commit off `upstream/master` (`61f9a9df23`). It is not part of the multi-database step chain.
  - `4f64caba7a` Read the premiere date from the broadcast date tag
- **Size:** 2 files, +38/-1. Production code is one token.
- **Status:** gated and merged into `feature/multi-db`. Not pushed. Whether it ships inside the big PR or on its own, like X1, is decided at submission time (`pr-plan.md` S10).

## What the code does

`ProbeResultNormalizer.FetchWtvInfo` reads the Windows Media tags that ffprobe reports for WTV recordings (and other ASF files). Two of them carry dates:

- `WM/OriginalReleaseTime`, read into a variable called `year`, and parsed with `int.TryParse` into `ProductionYear`;
- `WM/MediaOriginalBroadcastDateTime`, read into `premiereDateString`, meant for `PremiereDate`.

The second condition looked up the broadcast tag, then parsed `year`:

```csharp
if (tags.TryGetValue("WM/MediaOriginalBroadcastDateTime", out var premiereDateString)
    && DateTime.TryParse(year, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedDate))
```

The fix changes `year` to `premiereDateString`. Nothing else changes: the culture and the `AdjustToUniversal` style stay as upstream wrote them.

## What users see

- **Before.** The broadcast date was never used.
  - Usually the year tag is a bare year such as `2010`. That does not parse as a date, so `PremiereDate` stayed empty.
  - If a file had a full timestamp in the year tag, the premiere date came from the wrong tag.
- **After.** Files that carry the broadcast date tag get a premiere date from it. In `FFProbeVideoInfo.cs`:
  - `:445-450` applies the probed premiere date when the item has none, or on a refresh that replaces metadata;
  - `:487-490` then fills `ProductionYear` from that date if the item still has no production year (for example, when the year tag is missing or is not a plain integer).
- The commit body says this. The reviewer asked for it, because "fixes a typo" undersells a visible change.

## History of the bug

Be accurate here, because it is easy to blame the wrong commit.

- The wrong variable is already in the Emby code (`2b7a80cfb5`, 2015).
- `11a5551218` (2021, the `ProbeResultNormalizer` refactor) folded the tag lookup into the `if` and carried `year` over.
- `653df7d8e5` (2021) changed `DateTimeStyles.None` to `AdjustToUniversal`.
- `f8ffccae7f` (2026-07) changed only the culture argument.
- None of the later commits introduced the bug. Our earlier note said it was "last touched" in `f8ffccae7f`, which is true but is not where it came from.

## Tests

In `tests/Jellyfin.MediaEncoding.Tests/Probing/ProbeResultNormalizerTests.cs`. No new fixture file: the tests load the existing `video_ts.json`, replace `Format.Tags` in code, and call `GetMediaInfo`.

- **Broadcast date and a bare year.** Broadcast `2010-05-04T20:00:00Z`, year `2009`. Expects `PremiereDate` 2010-05-04T20:00:00 with `Kind == Utc`, and `ProductionYear` 2009.
- **Broadcast date and a full timestamp in the year tag.** Year tag `2009-01-02T03:04:05Z`. Expects `ProductionYear` null (a timestamp fails `int.TryParse`) and `PremiereDate` from the broadcast tag.
- Both fail on the old code, for the intended reasons: the first because `2009` does not parse, the second because the date comes back as 2009-01-02. Both pass after the fix, under Asia/Tokyo, America/New_York and UTC.
- The `Kind` assertion is not decoration. The reviewer dropped `AdjustToUniversal` as a mutation; under UTC only the `Kind` assertion caught it.
- The test names say "Wtv" because the method is `FetchWtvInfo` and the tags are WTV tags, even though the JSON carrier is a TS probe. That is deliberate.

## Known limitation: a broadcast value with no zone designator

State this plainly if asked, because a reviewer may compare it with step 04.

- With `AdjustToUniversal` and no `Assume*` flag, .NET does not convert a value that has no zone designator. `2010-05-04T20:00:00` comes back as 20:00 with `Kind == Unspecified`. This was checked under Asia/Tokyo, America/New_York and UTC. A value ending in `Z` comes back `Utc`, and one with `+02:00` is converted to `Utc`.
- An `Unspecified` value is treated as server-local time when it is written: the database converter calls `ToUniversalTime()`. So on a server east of UTC an unzoned broadcast date is stored shifted, the same class of shift as step 04's comic dates.
- Before X2 this value never reached `PremiereDate`, so the path is newly reachable.
- **Why X2 does not build the date as `Utc` the way step 04 did.** X2 keeps upstream's parse style and fixes only the wrong variable. Whether an unzoned broadcast value is a calendar date or a local time depends on what writes the tag, and that has not been checked. The comment above the line says the value is "typically Z i.e. UTC", which is the case the tests cover.
- It is on the ADR-0004 U2 follow-up list with the other date producers.

## How it was tested

- Release build: 0 warnings. Debug build: 0 errors, and the same 211 warnings as upstream's own Debug build (sorted, paths stripped, 0 diff lines). `git diff --check` is clean.
- Release tests: `Jellyfin.MediaEncoding.Tests` 102 passed, 1 skipped (macOS only). `Jellyfin.Server.Implementations.Tests` 1076 passed, 12 skipped; `Jellyfin.Server.Tests` 39; `Jellyfin.Server.Integration.Tests` 129 passed, 3 skipped (on the first version of the commit; the amend changed only a test assertion and the message).
- Mutation: with `year` restored, both tests fail in all three zones.
- Independent code review: PASS WITH NITS. Applied: the `ProductionYear` assertion and the visible-effect paragraph in the message. Declined: a test for a missing or unparsable broadcast tag (pre-existing behaviour, kept out of a one-token fix), renaming the tests, and removing the `Assert.NotNull` in the helper (needed for nullable flow analysis).
- To reproduce: `dotnet test tests/Jellyfin.MediaEncoding.Tests --filter "FullyQualifiedName~Wtv"`.

## Questions to be ready for

- **"Is this related to multiple databases?"** No. It was found while auditing date producers for the UTC work. The user ruled that it is fixed as part of this work.
- **"Why not also fix the unzoned case?"** See the limitation above: it needs a check of what writes the tag, and it would change upstream's parse style in a one-token fix.
- **"Could this throw?"** No. It is still `TryParse`; an unparsable value leaves `PremiereDate` unset, as before.
