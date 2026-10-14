using System;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Migrations.Routines;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

/// <summary>
/// Covers the import of the legacy activity log database, in particular the entries that reference a
/// user by the integer id of an old schema instead of by Guid.
/// </summary>
public sealed class MigrateActivityLogDbTests : IDisposable
{
    private static readonly Guid _firstUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _resolvedUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _lastUserId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly string _root;
    private readonly string _dataPath;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;

    public MigrateActivityLogDbTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jf-activitylog-" + Guid.NewGuid().ToString("N"));
        _dataPath = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataPath);

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_root, "jellyfin.db")};Pooling=False")
            .Options;

        using var context = CreateDbContext();
        context.Database.EnsureCreated();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Perform_LegacyUserIdFoundInUserDatabase_ImportsEveryEntryWithResolvedUser(bool guidsAsText)
    {
        CreateActivityLogDb(guidsAsText);
        CreateUserDb(userId: _resolvedUserId, guidAsText: guidsAsText);

        Perform();

        using var context = CreateDbContext();
        var logs = context.ActivityLogs.OrderBy(l => l.Id).ToList();
        Assert.Equal(["first", "second", "third"], logs.Select(l => l.Name));
        Assert.Equal([_firstUserId, _resolvedUserId, _lastUserId], logs.Select(l => l.UserId));
    }

    [Fact]
    public void Perform_LegacyUserIdMissingFromUserDatabase_ImportsEveryEntryWithEmptyUser()
    {
        CreateActivityLogDb(guidsAsText: false);
        CreateUserDb(userId: null);

        Perform();

        using var context = CreateDbContext();
        var logs = context.ActivityLogs.OrderBy(l => l.Id).ToList();
        Assert.Equal(["first", "second", "third"], logs.Select(l => l.Name));
        Assert.Equal([_firstUserId, Guid.Empty, _lastUserId], logs.Select(l => l.UserId));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private JellyfinDbContext CreateDbContext() => new(
        _dbOptions,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(new Mock<IApplicationPaths>().Object, NullLogger<SqliteDatabaseProvider>.Instance),
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private SqliteConnection OpenLegacyDb(string fileName)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_dataPath, fileName)};Pooling=False");
        connection.Open();
        return connection;
    }

    private void CreateActivityLogDb(bool guidsAsText)
    {
        using var connection = OpenLegacyDb("activitylog.db");
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE ActivityLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Overview TEXT,
                ShortOverview TEXT,
                Type TEXT NOT NULL,
                ItemId TEXT,
                UserId GUID,
                DateCreated DATETIME NOT NULL,
                LogSeverity TEXT NOT NULL)
            """;
        create.ExecuteNonQuery();

        InsertEntry(connection, "first", LegacyGuid(_firstUserId, guidsAsText));
        InsertEntry(connection, "second", 1L);
        InsertEntry(connection, "third", LegacyGuid(_lastUserId, guidsAsText));
    }

    private static object LegacyGuid(Guid guid, bool asText)
        => asText ? guid.ToString() : guid.ToByteArray();

    private static void InsertEntry(SqliteConnection connection, string name, object userId)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO ActivityLog (Name, Type, UserId, DateCreated, LogSeverity)
            VALUES (@Name, 'UserLoggedIn', @UserId, '2025-01-01 10:00:00', 'Information')
            """;
        insert.Parameters.AddWithValue("@Name", name);
        insert.Parameters.AddWithValue("@UserId", userId);
        insert.ExecuteNonQuery();
    }

    private void CreateUserDb(Guid? userId, bool guidAsText = false)
    {
        using var connection = OpenLegacyDb("users.db");
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE LocalUsersv2 (Id INTEGER PRIMARY KEY AUTOINCREMENT, guid GUID NOT NULL, data BLOB NOT NULL)";
        create.ExecuteNonQuery();

        if (userId is null)
        {
            return;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO LocalUsersv2 (Id, guid, data) VALUES (1, @Guid, x'00')";
        insert.Parameters.AddWithValue("@Guid", LegacyGuid(userId.Value, guidAsText));
        insert.ExecuteNonQuery();
    }

    private void Perform()
    {
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.SetupGet(p => p.DataPath).Returns(_dataPath);

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);

        new MigrateActivityLogDb(
            NullLogger<MigrateActivityLogDb>.Instance,
            appPaths.Object,
            factory.Object)
            .Perform();
    }
}
