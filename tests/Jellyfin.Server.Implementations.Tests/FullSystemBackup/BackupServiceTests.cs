using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.FullSystemBackup;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.SystemBackupService;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using BaseItemKind = Jellyfin.Data.Enums.BaseItemKind;

namespace Jellyfin.Server.Implementations.Tests.FullSystemBackup;

/// <summary>
/// Tests for <see cref="BackupService"/>, in particular that a single row of corrupt
/// <see cref="KeyframeData"/> (e.g. malformed <c>KeyframeTicks</c> JSON) does not abort
/// an otherwise healthy backup. See https://github.com/jellyfin/jellyfin/issues/17216.
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly string _testRoot;
    private readonly string _backupPath;
    private readonly string _configurationDirectoryPath;

    public BackupServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var ctx = CreateDbContext())
        {
            ctx.Database.EnsureCreated();
        }

        // Use the test assembly's own output directory instead of Path.GetTempPath(). On GitHub-hosted
        // windows-latest runners, the system temp directory lives on the constrained C: drive, which can have
        // less than the 5GiB BackupService requires free, causing spurious failures. AppContext.BaseDirectory
        // is under the repo checkout (the much larger D: drive on Windows runners) on all platforms.
        _testRoot = Path.Combine(AppContext.BaseDirectory, "jellyfin-backup-service-tests-" + Guid.NewGuid().ToString("N"));
        _backupPath = Path.Combine(_testRoot, "Backup");
        _configurationDirectoryPath = Path.Combine(_testRoot, "Config");
        Directory.CreateDirectory(_backupPath);
        Directory.CreateDirectory(_configurationDirectoryPath);
    }

    public void Dispose()
    {
        _connection.Dispose();

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    public async Task CreateBackupAsync_WithCorruptKeyframeDataRow_SkipsRowAndCompletesBackup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var validItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var corruptItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await using (var ctx = CreateDbContext())
        {
            // A healthy item + keyframe row, written the normal way.
            ctx.BaseItems.Add(CreateMovieEntity(validItemId, "Good Movie"));
            ctx.BaseItems.Add(CreateMovieEntity(corruptItemId, "Corrupt Movie"));
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

            ctx.KeyframeData.Add(new KeyframeData
            {
                ItemId = validItemId,
                TotalDuration = 60_000,
                KeyframeTicks = [0, 1000, 2000]
            });
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

            // Simulate a corrupted database row: truncated JSON array for KeyframeTicks,
            // written directly via SQL to bypass EF's normal (well-formed) write path.
            await ctx.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO KeyframeData (ItemId, TotalDuration, KeyframeTicks) VALUES ({corruptItemId.ToString()}, {5000L}, {"[1,2,3"})",
                cancellationToken).ConfigureAwait(true);
        }

        var backupService = CreateBackupService();

        var manifest = await backupService.CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);

        Assert.True(File.Exists(manifest.Path));

        using var archive = await ZipFile.OpenReadAsync(manifest.Path, cancellationToken).ConfigureAwait(true);
        var keyframeEntry = archive.GetEntry("Database/KeyframeData.json");
        Assert.NotNull(keyframeEntry);

        await using var entryStream = await keyframeEntry!.OpenAsync(cancellationToken).ConfigureAwait(true);
        using var document = await JsonDocument.ParseAsync(entryStream, cancellationToken: cancellationToken).ConfigureAwait(true);

        var rows = document.RootElement.EnumerateArray().ToList();

        // The corrupt row must be skipped, but the valid row must still make it into the backup.
        var singleRow = Assert.Single(rows);
        Assert.Equal(validItemId, singleRow.GetProperty("ItemId").GetGuid());
    }

    [Fact]
    public async Task RestoreBackupAsync_WithHomeSections_RestoresThem()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHomeSectionsAsync(cancellationToken).ConfigureAwait(true);
        var expected = await DisplayPreferencesRowsAsync(cancellationToken).ConfigureAwait(true);
        var backupService = CreateBackupService();
        var manifest = await backupService.CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);

        // The sections change after the backup, so only the restore can bring them back.
        await using (var ctx = CreateDbContext())
        {
            var sections = await ctx.Set<HomeSection>().OrderBy(e => e.Order).ToListAsync(cancellationToken).ConfigureAwait(true);
            sections[0].Type = HomeSectionType.NextUp;
            ctx.Set<HomeSection>().Remove(sections[1]);
            await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        }

        await backupService.RestoreBackupAsync(manifest.Path).ConfigureAwait(true);

        Assert.Equal(expected, await DisplayPreferencesRowsAsync(cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task RestoreBackupAsync_WithDamagedKey_LeavesDatabaseUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedHomeSectionsAsync(cancellationToken).ConfigureAwait(true);
        var backupService = CreateBackupService();
        var manifest = await backupService.CreateBackupAsync(new BackupOptionsDto()).ConfigureAwait(true);
        await RewriteEntryAsync(
            manifest.Path,
            "Database/DisplayPreferences.json",
            json =>
            {
                var rows = JsonNode.Parse(json)!.AsArray();
                rows[0]!["Id"] = "not-a-number";
                return rows.ToJsonString();
            },
            cancellationToken).ConfigureAwait(true);
        var before = await DisplayPreferencesRowsAsync(cancellationToken).ConfigureAwait(true);

        await Assert.ThrowsAnyAsync<JsonException>(() => backupService.RestoreBackupAsync(manifest.Path)).ConfigureAwait(true);

        Assert.Equal(before, await DisplayPreferencesRowsAsync(cancellationToken).ConfigureAwait(true));
    }

    private static async Task RewriteEntryAsync(string archivePath, string entryName, Func<string, string> transform, CancellationToken cancellationToken)
    {
        using var archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Update, cancellationToken).ConfigureAwait(false);
        var entry = archive.GetEntry(entryName)!;
        string content;
        var readStream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (readStream.ConfigureAwait(false))
        using (var reader = new StreamReader(readStream))
        {
            content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        entry.Delete();
        var replacement = archive.CreateEntry(entryName);
        var writeStream = await replacement.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (writeStream.ConfigureAwait(false))
        using (var writer = new StreamWriter(writeStream))
        {
            await writer.WriteAsync(transform(content)).ConfigureAwait(false);
        }
    }

    private async Task SeedHomeSectionsAsync(CancellationToken cancellationToken)
    {
        await using var ctx = CreateDbContext();
        var user = new User("alice", "provider", "reset");
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The removed row leaves a gap, so the sections only point at their preferences if the ids are restored.
        var removed = new DisplayPreferences(user.Id, Guid.Empty, "removed");
        var preferences = new DisplayPreferences(user.Id, Guid.Empty, "web");
        preferences.HomeSections.Add(new HomeSection { Order = 0, Type = HomeSectionType.LatestMedia });
        preferences.HomeSections.Add(new HomeSection { Order = 1, Type = HomeSectionType.Resume });
        ctx.DisplayPreferences.AddRange(removed, preferences);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        ctx.DisplayPreferences.Remove(removed);
        await ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<string>> DisplayPreferencesRowsAsync(CancellationToken cancellationToken)
    {
        await using var ctx = CreateDbContext();
        var rows = new List<string>();
        foreach (var row in await ctx.DisplayPreferences.AsNoTracking().OrderBy(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add($"DisplayPreferences {row.Id} {row.UserId} {row.Client}");
        }

        foreach (var row in await ctx.Set<HomeSection>().AsNoTracking().OrderBy(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add($"HomeSection {row.Id} {row.DisplayPreferencesId} {row.Order} {row.Type}");
        }

        return rows;
    }

    private BackupService CreateBackupService()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);

        var applicationHost = new Mock<IServerApplicationHost>();
        applicationHost.Setup(a => a.ApplicationVersion).Returns(new Version(10, 11, 0));

        var applicationPaths = new Mock<IServerApplicationPaths>();
        applicationPaths.Setup(a => a.BackupPath).Returns(_backupPath);
        applicationPaths.Setup(a => a.ConfigurationDirectoryPath).Returns(_configurationDirectoryPath);
        applicationPaths.Setup(a => a.DataPath).Returns(Path.Combine(_testRoot, "Data"));
        applicationPaths.Setup(a => a.CachePath).Returns(Path.Combine(_testRoot, "Cache"));
        applicationPaths.Setup(a => a.ProgramDataPath).Returns(Path.Combine(_testRoot, "ProgramData"));
        applicationPaths.Setup(a => a.RootFolderPath).Returns(Path.Combine(_testRoot, "Root"));
        applicationPaths.Setup(a => a.InternalMetadataPath).Returns(Path.Combine(_testRoot, "Metadata"));
        applicationPaths.Setup(a => a.DefaultInternalMetadataPath).Returns(Path.Combine(_testRoot, "MetadataDefault"));

        var jellyfinDatabaseProvider = new Mock<IJellyfinDatabaseProvider>();
        jellyfinDatabaseProvider.Setup(p => p.RunScheduledOptimisation(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        jellyfinDatabaseProvider
            .Setup(p => p.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<IEnumerable<string>>()))
            .Returns((JellyfinDbContext context, IEnumerable<string> tableNames) => new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance).PurgeDatabase(context, tableNames));

        var applicationLifetime = new Mock<IHostApplicationLifetime>();

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.IsScanRunning).Returns(false);

        return new BackupService(
            NullLogger<BackupService>.Instance,
            factory.Object,
            applicationHost.Object,
            applicationPaths.Object,
            jellyfinDatabaseProvider.Object,
            applicationLifetime.Object,
            libraryManager.Object);
    }

    private static BaseItemEntity CreateMovieEntity(Guid id, string name)
    {
        return new BaseItemEntity
        {
            Id = id,
            Type = "Movie",
            Name = name,
            PresentationUniqueKey = id.ToString("N"),
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false
        };
    }

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
