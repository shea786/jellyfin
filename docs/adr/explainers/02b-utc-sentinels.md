# Explainer: step 02b, store date sentinels independently of the time zone

These are study notes for understanding and defending this change in your own words. Do not paste them as PR text.

- **Branch:** `multidb/02b-utc-sentinels`, one commit, merged as `8184b5edd2`.
  - `06b702e6d6` Store minimum and maximum dates independently of the time zone
- **Size:** production code is one internal helper of about ten lines in `UtcDateTimeConverter.cs`. The rest is tests and two small test support types.
- **In the upstream series:** it is replayed right after step 02, which created the shared converter.
- **Record:** ADR-0004 R11.

## The bug

`UtcDateTimeConverter` (step 02, which replaced upstream's SQLite-only `DateTimeKindValueConverter` with the same body) stored every `DateTime` as `v.ToUniversalTime()`.

`DateTime.MinValue` and `DateTime.MaxValue` have `Unspecified` kind. `ToUniversalTime()` treats an unspecified value as local time, so what got stored depended on the server's zone:
- `MinValue` moves by the zone's local mean time in year 1, because the time zone database goes back that far. That is about one minute in Europe/London and 4h57m in America/New_York.
- `MaxValue` moves by the zone's current offset: 5h30m earlier in Kolkata, 13h earlier in Auckland.
- East of UTC, `MinValue` would go below zero ticks, so .NET clamps it and nothing visible happens. West of UTC the same clamp protects `MaxValue`. So each zone only exercises one of the two sentinels.

On PostgreSQL, a correctly labelled sentinel maps to `-infinity` or `infinity`. A shifted one is an ordinary timestamp.

## Who noticed it

The step 24 code review ran the date tests under `TZ=Asia/Kolkata` and found the MySQL clamp converter losing `MaxValue`. The same check on the shared converter showed the shift. CI never saw it because CI runs in UTC.

## The fix

`ToStoredUtc(value)`: if `value == DateTime.MinValue || value == DateTime.MaxValue` (equality compares ticks only, whatever the kind), return the same ticks labelled UTC. Otherwise call `ToUniversalTime()` as before.

- It changes nothing for an ordinary value.
- For a `Local` sentinel it keeps the ticks and relabels them. That is "the same ticks", not "the same instant", and the commit message says so.
- It is internal, so the ABI-checked assembly gains no public surface. A provider converter (the MySQL clamp) can reuse it.

## What it does not do

It does not repair rows already stored shifted. A repair would have to guess the offset at write time. The inventory in ADR-0004 R11 shows that readers which treat a sentinel as "unset" read `null`-mapped columns, so they never saw the shifted value. The one exception is `MetadataService.cs:557`, which can give a parent a year-1 premiere date from an already-shifted child. That is rare and only affects old rows.

## Tests

- **`UtcDateTimeConverterTests`:** the helper and converter with literal ticks and kinds. Every expected value is a literal; none is computed with the code under test.
- **`UtcDateTimeConverterTimeZoneTests`:** a regression guard that works on a UTC machine.
  - `TemporaryTimeZone` sets `TZ`, calls `TimeZoneInfo.ClearCachedData()`, and restores both on dispose. It skips on non-Linux hosts, where `TZ` does not drive the local zone.
  - The tests sit in the `TimeZoneIsolation` collection with `DisableParallelization = true`, so no other test runs while the zone is changed.
  - New York exercises `MinValue` and Kolkata exercises `MaxValue`. With the old converter, 8 of 46 tests fail even with the whole process in UTC.
- **`DateTimeRoundTripTests`:** on the selected database, `Local` and `Unspecified` sentinels round-trip exactly, inside the zone guard too. Equality queries (`PremiereDate == DateTime.MinValue`) find the stored row.
- All 46 date tests pass on SQLite and PostgreSQL under UTC, New York, Kolkata, London and Auckland.

## Likely review questions

- **"Why not treat every Unspecified value as UTC?"** That would change every ordinary date written with local semantics, which is a far larger behaviour change. Only the sentinels have a meaning that no time zone can apply to.
- **"Is the in-process TZ change safe?"** It is restored in `Dispose`, and the collection runs alone. `ClearCachedData` is the documented way to make .NET re-read the local zone.
- **"Why not a CI matrix leg?"** Upstream reviewers would get no guard from a fork-only workflow. The in-process test travels with the code.
