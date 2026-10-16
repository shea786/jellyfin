using System;
using System.IO;
using Jellyfin.Database.Providers.Sqlite;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data;

/// <summary>
/// The refused rows here are refused by SQLite itself, on an in-memory database, so the result codes the provider
/// classifies on are the ones SQLite reports. The errors that no row causes are built from their result codes.
/// </summary>
public sealed class SqliteDataErrorClassificationTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SqliteDatabaseProvider _provider = new(
        Mock.Of<IApplicationPaths>(),
        NullLogger<SqliteDatabaseProvider>.Instance);

    public SqliteDataErrorClassificationTests()
    {
        _connection.Open();
        Execute("PRAGMA foreign_keys = ON");
        Execute("CREATE TABLE Parents (Id INTEGER NOT NULL PRIMARY KEY)");
        Execute(
            """
            CREATE TABLE Children (
                Id TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL UNIQUE,
                Label TEXT NOT NULL,
                ParentId INTEGER NOT NULL REFERENCES Parents (Id),
                Size INTEGER NOT NULL CHECK (Size >= 0))
            """);
        Execute(
            """
            CREATE TRIGGER RefuseRejected BEFORE INSERT ON Children WHEN NEW.Label = 'rejected'
            BEGIN SELECT RAISE(ABORT, 'rejected'); END
            """);
        Execute("INSERT INTO Parents (Id) VALUES (1)");
        Execute("INSERT INTO Children (Id, Name, Label, ParentId, Size) VALUES ('a', 'alpha', 'x', 1, 1)");
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    [Theory]
    [InlineData("INSERT INTO Children VALUES ('b', 'alpha', 'x', 1, 1)")]
    [InlineData("INSERT INTO Children VALUES ('a', 'beta', 'x', 1, 1)")]
    [InlineData("INSERT INTO Children VALUES ('b', 'beta', 'x', 2, 1)")]
    [InlineData("INSERT INTO Children VALUES ('b', 'beta', NULL, 1, 1)")]
    [InlineData("INSERT INTO Children VALUES ('b', 'beta', 'x', 1, -1)")]
    [InlineData("INSERT INTO Children VALUES ('b', 'beta', 'rejected', 1, 1)")]
    [InlineData("INSERT INTO Parents VALUES ('not a number')")]
    public void IsDataError_RowRefusedBySqlite_IsDataError(string statement)
    {
        var exception = Assert.Throws<SqliteException>(() => Execute(statement));

        Assert.True(_provider.IsDataError(exception));
    }

    [Fact]
    public void IsDataError_ValueOverTheLengthLimit_IsDataError()
    {
        // SQLITE_LIMIT_LENGTH, lowered for this connection so that a short value is already too big.
        SQLitePCL.raw.sqlite3_limit(_connection.Handle, 0, 100);

        var exception = Assert.Throws<SqliteException>(() => Execute("INSERT INTO Children VALUES ('b', 'beta', hex(zeroblob(100)), 1, 1)"));

        Assert.Equal(18, exception.SqliteErrorCode);
        Assert.True(_provider.IsDataError(exception));
    }

    [Fact]
    public void IsDataError_RefusalWrappedInDbUpdateException_IsClassifiedByTheInnerException()
    {
        var inner = Assert.Throws<SqliteException>(() => Execute("INSERT INTO Children VALUES ('b', 'beta', 'rejected', 1, 1)"));

        Assert.True(_provider.IsDataError(new DbUpdateException("An error occurred while saving the entity changes.", inner)));
    }

    [Theory]
    [InlineData(1)] // SQLITE_ERROR, such as a missing table
    [InlineData(5)] // SQLITE_BUSY
    [InlineData(6)] // SQLITE_LOCKED
    [InlineData(10)] // SQLITE_IOERR
    [InlineData(11)] // SQLITE_CORRUPT
    [InlineData(13)] // SQLITE_FULL
    [InlineData(14)] // SQLITE_CANTOPEN
    public void IsDataError_ErrorThatNoRowCauses_IsNotDataError(int resultCode)
    {
        var exception = new SqliteException("error", resultCode);

        Assert.False(_provider.IsDataError(exception));
        Assert.False(_provider.IsDataError(new DbUpdateException("An error occurred while saving the entity changes.", exception)));
    }

    [Fact]
    public void IsDataError_ExceptionNotFromSqlite_IsNotDataError()
    {
        Assert.False(_provider.IsDataError(new DbUpdateException("outer", new IOException("disk"))));
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
#pragma warning disable CA2100 // The statements are constants of this class.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }
}
