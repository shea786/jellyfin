# Explainer: step 12, connection string from configuration

These are study notes for understanding and defending this step in your own words. Do not paste them as PR text.

- **Branch:** `multidb/12-connection-string-override`, one commit on the `feature/multi-db` tip after step 08 (`6297fbd114`).
  - `21a9c934f3` Allow the database connection string to come from configuration
- **Status:** gated and merged into `feature/multi-db`. Not pushed. Backups of earlier versions: `multidb/12-backup-a` (`d3294d9a75`, before hardening), `-b` (`fa808337e1`, before the container fix), `-c` (`e6e416f127`, before the last two nits).

## Why it exists

A server database such as PostgreSQL needs a connection string with a password. Today the only place for it is `CustomProviderOptions.ConnectionString` in `database.xml`, which sits in the config directory, gets copied into backups and is shown by the settings API. Containers hand secrets in through the environment instead. ADR-0001 option 3b decided on a configuration key, applied in memory only, and no command line flag.

## What changes

- The configuration key `database:ConnectionString` replaces `CustomProviderOptions.ConnectionString`. Like any Jellyfin configuration key it can be set as the environment variable `JELLYFIN_DATABASE__CONNECTIONSTRING`. A blank value counts as not set.
- Both service containers (the one that runs migrations and the main one) read the key from the server's own startup configuration, which holds only `JELLYFIN_*` variables and the command line. The main container used to get the web host's configuration, which also holds unprefixed variables, `DOTNET_*`, `ASPNETCORE_*` and `appsettings.json`. With that, `DOTNET_DATABASE__CONNECTIONSTRING` would have moved the main container to another database while migrations ran against the one in `database.xml`. The first gate caught this.
- `AddJellyfinDbContext` reads the key after the default database configuration has been written. It builds a copy of the configuration with the override (`WithConnectionStringOverride`, internal) and hands only the copy to `provider.Initialise`. The object cached by the configuration manager and `database.xml` keep the stored value, so nothing that saves the database configuration later can write the secret to disk, and the settings API and backups never see it.
- If the file has no `CustomProviderOptions`, the copy gets them with empty plugin name and assembly. Plugin providers receive the override the same way as built-in ones.
- One log line names the key, never the value. It appears twice per start because the server builds two service containers, which is cosmetic.
- The startup log prints every `JELLYFIN_*` environment variable. That would have printed the connection string and its password, so the value is now shown as `[redacted]`. The check mirrors how .NET maps environment variables to configuration keys: prefix compared without regard to case, `__` read as `:`, key compared without regard to case. So `jellyfin_database__connectionstring` and `JELLYFIN_database:ConnectionString` are hidden too, while look-alikes stay visible.
- No new public members. `Jellyfin.Server.Implementations` is not on the ABI list, and the new method is internal anyway.

## What it does on SQLite

Nothing. The SQLite provider builds its own connection string from the data path and `Options`, and never reads `ConnectionString`. A test pins that setting the override with SQLite still starts normally.

## Why a copy, and why the guard test

Changing the cached object in place would have been one line, but any later `SaveConfiguration("database", ...)` (the startup wizard, a restore, a plugin) would then write the secret to `database.xml`. A copy cannot leak that way. The risk of a copy is the opposite one: a setting added to `DatabaseConfigurationOptions` or `CustomDatabaseOptions` later would silently be left out. The guard test fills every public settable property, nested objects and list items included, and checks each one arrives in the copy as an independent object. A type it cannot fill makes it fail with a message to extend it. Adding a dummy property that the copy ignores fails only this test.

## Why no command line flag

Process listings (`ps`, `/proc/*/cmdline`) are readable by other users on most systems, so a flag would expose the password. Npgsql's own `PGPASSWORD` and `PGPASSFILE` keep working where the provider is PostgreSQL.

## Evidence

- Builds: Release 0 warnings; Debug 211 warnings, the same sorted list as the base.
- Tests: Implementations 1422 passed and 12 skipped, Server 69, Integration 129 passed and 3 skipped.
- Red first: the `:` spellings of the variable failed before the redaction change. The `DOTNET_`, `ASPNETCORE_` and unprefixed spellings failed against the old single-source behaviour.
- Mutations: case-sensitive redaction fails 5 cases; dropping the `__` to `:` mapping fails 3; an unhandled new property fails the guard test only. Returning the web host's configuration unchanged, or putting the in-memory source before the host's, each fails the same three container tests. Dropping the null check on `Options` fails its own test.

## Limits

- `Startup` cannot be built in the unit tests, so the line that hands the startup configuration to it is covered by compilation and the integration tests only. The decision logic, `CreateDatabaseConfiguration`, is tested directly.
- `AddJellyfinDbContext` also reads `migration-provider`, which only matters when `database.xml` has no database type and which nothing else defines. Its source differs per container today and is left alone, because changing it would alter behaviour unrelated to this step.

- The override covers only the connection string. Other provider options still come from `database.xml`.
- The value is in process memory and in the environment of the process, as with any environment secret. Hiding it from `/proc/<pid>/environ` is the job of the container runtime.
