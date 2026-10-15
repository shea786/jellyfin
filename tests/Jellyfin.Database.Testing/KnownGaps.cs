using System;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Database.Testing;

/// <summary>
/// The known differences between providers. An entry is deleted when the difference is closed, which breaks the
/// build wherever a test is still marked with it.
/// </summary>
public static class KnownGaps
{
    public static readonly KnownGapDefinition NulInExactMatchParameter = new(
        TestDbProvider.Postgres,
        "PostgreSQL rejects a NUL character in any string parameter",
        ex => PostgresErrors.HasSqlState(ex, "22021"));

    public static readonly KnownGapDefinition ContainsOverManyValues = new(
        TestDbProvider.Sqlite,
        "SQLITE-CONTAINS-PARAMETER-LIMIT: SQLite gets a parameter for every value of a plain Contains, and takes at most 32,766",
        ex => ex is SqliteException { SqliteErrorCode: 1 } sqlite && sqlite.Message.Contains("too many SQL variables", StringComparison.Ordinal));
}
