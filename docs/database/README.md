# Running Jellyfin on a database server

These pages are for operators of Jellyfin. Upstream Jellyfin stores its data in SQLite only. This branch can also store it in PostgreSQL, selected in `database.xml`.

| Engine | Page | Tier | Oldest version |
| --- | --- | --- | --- |
| SQLite | none needed, it stays the default | Supported | bundled |
| PostgreSQL | [postgresql.md](postgresql.md) | Supported | 16 |

Supported means the engine's CI job is a required check.

Each page covers the database and its user, `database.xml`, the first start, backups and restores, upgrades, and a Docker Compose example.

## Docker Compose

`compose/<engine>/compose.yaml` runs Jellyfin and the engine side by side, with health checks, volumes and the
connection string from `.env`. Upstream Jellyfin images have no provider for these engines, so the examples build
the Jellyfin image from this checkout with `compose/Dockerfile`. Copy `env.example` to `.env` next to the Compose
file, set the values, and run `docker compose up -d --build`.

## Editing these pages

The engine pages and the Compose examples are rendered from the templates in `templates/` by
`docs/database/render`, so that they cannot drift apart. Edit a template, or the table of engines at the top of
`render`, then run:

```sh
docs/database/render              # Linux and macOS
dotnet run docs/database/render   # every platform
```

`docs/database/render --check` fails when a rendered file is not what the template gives.
