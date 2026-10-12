# Explainer: step 23, Entity Framework Core 10.0.12

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/23-efcore-10.0.12`, one commit on `upstream/master` (`61f9a9df23`), independent of the other steps.
  - `f84294103c` Update Entity Framework Core to 10.0.12
- **Size:** `Directory.Packages.props` only, 10 version pins.
- **Status:** gated and merged into `feature/multi-db`. Not pushed.

## Why it exists

The MySQL/MariaDB provider uses `Microting.EntityFrameworkCore.MySql` 10.0.12, which requires EF Core 10.0.12 or newer (ADR-0011). The commit message does not say that, on purpose: on its own the commit is an ordinary servicing update, and a claim that "providers need 10.0.12" would be false in general (Npgsql 10.0.3 accepts EF 10.0.4 and up).

## What changes

- The EF Core packages, `Microsoft.Data.Sqlite` and `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` move to 10.0.12.
- Four `Microsoft.Extensions` pins move too: `Caching.Abstractions`, `Caching.Memory`, `DependencyInjection`, `Logging`. EF 10.0.12 depends on their 10.0.12 versions; without them restore fails with 93 `NU1605` downgrade errors. The reviewer reverted each one individually and restore failed every time, so all four are needed.
- The other Extensions and ASP.NET pins stay at 10.0.11. The mixed versions are harmless: the server binds `Microsoft.Extensions` from the ASP.NET shared framework, and test projects bind at assembly version 10.0.0.0.
- The `dotnet-ef` tool in `.config/dotnet-tools.json` stays at 10.0.11 (it prints a version warning when run). Upstream bumps the tool separately.

## Evidence

- Upstream's `v10.0.11..v10.0.12` EF diff has no source changes.
- No model change: `has-pending-model-changes` reports none, and a throwaway migration is empty.
- Release build 0 warnings; Debug build the same 211 warnings as upstream; the whole test suite matches the base (one known Networking failure, `invalid.domain.test`, at the base too).

## Upstream overlap

Renovate PR #17879 ("Update Microsoft", open and mergeable) moves all 18 Microsoft pins to 10.0.12, a superset of this commit, and #17878 bumps the `dotnet-ef` tool. If #17879 merges first, this step is dropped. At submission it is reasonable to point to #17879 instead of carrying this commit.
