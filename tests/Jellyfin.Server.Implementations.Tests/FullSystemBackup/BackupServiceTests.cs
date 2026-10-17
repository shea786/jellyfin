using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
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
    // The database configuration of the server that restores, and the one inside the archive.
    private const string ThisServerDatabaseConfiguration = """
        <?xml version="1.0" encoding="utf-8"?>
        <DatabaseConfigurationOptions>
          <DatabaseType>PLUGIN_PROVIDER</DatabaseType>
          <CustomProviderOptions>
            <PluginName>ExampleDatabase</PluginName>
            <PluginAssembly>Example.Database</PluginAssembly>
            <ConnectionString>Host=database;Database=jellyfin;Username=jellyfin;Password=target</ConnectionString>
            <Options />
          </CustomProviderOptions>
          <LockingBehavior>NoLock</LockingBehavior>
        </DatabaseConfigurationOptions>
        """;

    private const string ArchivedDatabaseConfiguration = """
        <?xml version="1.0" encoding="utf-8"?>
        <DatabaseConfigurationOptions>
          <DatabaseType>Jellyfin-SQLite</DatabaseType>
          <LockingBehavior>Pessimistic</LockingBehavior>
        </DatabaseConfigurationOptions>
        """;

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

    [Theory]
    [InlineData("Config/database.xml")]
    [InlineData("Config/DATABASE.XML")]
    [InlineData("Config/users/../database.xml")]
    public async Task RestoreBackupAsync_WithDatabaseConfigurationInTheArchive_KeepsThatOfThisServer(string entryName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WriteConfigurationFileAsync("system.xml", "<system />", cancellationToken).ConfigureAwait(true);
        await WriteConfigurationFileAsync("database.xml", ThisServerDatabaseConfiguration, cancellationToken).ConfigureAwait(true);
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto { Database = false }).ConfigureAwait(true);
        await AddEntryAsync(manifest.Path, entryName, ArchivedDatabaseConfiguration, cancellationToken).ConfigureAwait(true);
        var databaseConfigurationPath = Path.Combine(_configurationDirectoryPath, "database.xml");
        var bytesBefore = await File.ReadAllBytesAsync(databaseConfigurationPath, cancellationToken).ConfigureAwait(true);
        var filesBefore = ListConfigurationFiles();

        await CreateBackupService().RestoreBackupAsync(manifest.Path).ConfigureAwait(true);

        // A copy under another casing must not appear beside the file (a file system that tells the casings apart would keep both).
        Assert.Equal(filesBefore, ListConfigurationFiles());
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(databaseConfigurationPath, cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task RestoreBackupAsync_WithDatabaseConfigurationInTheArchive_StillRestoresTheOtherConfiguration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var userFile = Path.Combine("users", "0f4e5a50d3c64f6c8d8a3f2c4b1a9e77", "x.xml");

        // A user directory may hold a file of the same name; only the one in the configuration directory itself is kept.
        var userDatabaseFile = Path.Combine("users", "0f4e5a50d3c64f6c8d8a3f2c4b1a9e77", "database.xml");
        string[] files = ["system.xml", "network.xml", "encoding.xml", userFile, userDatabaseFile];
        foreach (var file in files)
        {
            await WriteConfigurationFileAsync(file, "<archived />", cancellationToken).ConfigureAwait(true);
        }

        await WriteConfigurationFileAsync("database.xml", ThisServerDatabaseConfiguration, cancellationToken).ConfigureAwait(true);
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto { Database = false }).ConfigureAwait(true);
        await AddEntryAsync(manifest.Path, "Config/database.xml", ArchivedDatabaseConfiguration, cancellationToken).ConfigureAwait(true);
        foreach (var file in files)
        {
            await WriteConfigurationFileAsync(file, "<changed after the backup />", cancellationToken).ConfigureAwait(true);
        }

        await CreateBackupService().RestoreBackupAsync(manifest.Path).ConfigureAwait(true);

        foreach (var file in files)
        {
            Assert.Equal(
                "<archived />",
                await File.ReadAllTextAsync(Path.Combine(_configurationDirectoryPath, file), cancellationToken).ConfigureAwait(true));
        }
    }

    [Fact]
    public async Task CreateBackupAsync_LeavesTheDatabaseConfigurationOut()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WriteConfigurationFileAsync("system.xml", "<system />", cancellationToken).ConfigureAwait(true);
        await WriteConfigurationFileAsync("database.xml", ThisServerDatabaseConfiguration, cancellationToken).ConfigureAwait(true);

        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto { Database = false }).ConfigureAwait(true);

        using var archive = await ZipFile.OpenReadAsync(manifest.Path, cancellationToken).ConfigureAwait(true);
        Assert.Null(archive.GetEntry("Config/database.xml"));
        var entriesWithThePassword = new List<string>();
        foreach (var entry in archive.Entries)
        {
            var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(true);
            await using (stream.ConfigureAwait(true))
            using (var reader = new StreamReader(stream))
            {
                var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(true);
                if (content.Contains("Password=target", StringComparison.Ordinal))
                {
                    entriesWithThePassword.Add(entry.FullName);
                }
            }
        }

        Assert.Empty(entriesWithThePassword);
        Assert.NotNull(archive.GetEntry("Config/system.xml"));
    }

    [Fact]
    public async Task RestoreBackupAsync_WithAnArchiveFromBeforeThisChange_DoesNotApplyItsDatabaseConfiguration()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WriteConfigurationFileAsync("system.xml", "<archived />", cancellationToken).ConfigureAwait(true);
        await WriteConfigurationFileAsync("database.xml", ThisServerDatabaseConfiguration, cancellationToken).ConfigureAwait(true);
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto { Database = false }).ConfigureAwait(true);

        // What a server made before this change wrote: the same engine version and the database configuration among the files.
        const string ManifestOfAnOlderServer = """{"ServerVersion":"10.11.0","BackupEngineVersion":"0.2.0","DateCreated":"2026-01-01T00:00:00+00:00","DatabaseTables":[],"Options":{"Metadata":false,"Trickplay":false,"Subtitles":false,"Database":false}}""";
        await AddEntryAsync(manifest.Path, "manifest.json", ManifestOfAnOlderServer, cancellationToken).ConfigureAwait(true);
        await AddEntryAsync(manifest.Path, "Config/database.xml", ArchivedDatabaseConfiguration, cancellationToken).ConfigureAwait(true);
        await WriteConfigurationFileAsync("system.xml", "<changed after the backup />", cancellationToken).ConfigureAwait(true);
        var databaseConfigurationPath = Path.Combine(_configurationDirectoryPath, "database.xml");
        var bytesBefore = await File.ReadAllBytesAsync(databaseConfigurationPath, cancellationToken).ConfigureAwait(true);

        await CreateBackupService().RestoreBackupAsync(manifest.Path).ConfigureAwait(true);

        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(databaseConfigurationPath, cancellationToken).ConfigureAwait(true));
        Assert.Equal(
            "<archived />",
            await File.ReadAllTextAsync(Path.Combine(_configurationDirectoryPath, "system.xml"), cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task RestoreBackupAsync_WhenThisServerHasNoDatabaseConfiguration_DoesNotCreateOne()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WriteConfigurationFileAsync("system.xml", "<system />", cancellationToken).ConfigureAwait(true);
        var manifest = await CreateBackupService().CreateBackupAsync(new BackupOptionsDto { Database = false }).ConfigureAwait(true);
        await AddEntryAsync(manifest.Path, "Config/database.xml", ArchivedDatabaseConfiguration, cancellationToken).ConfigureAwait(true);
        var databaseConfigurationPath = Path.Combine(_configurationDirectoryPath, "database.xml");
        Assert.False(File.Exists(databaseConfigurationPath));

        await CreateBackupService().RestoreBackupAsync(manifest.Path).ConfigureAwait(true);

        Assert.False(File.Exists(databaseConfigurationPath));
    }

    private static async Task AddEntryAsync(string archivePath, string entryName, string content, CancellationToken cancellationToken)
    {
        using var archive = await ZipFile.OpenAsync(archivePath, ZipArchiveMode.Update, cancellationToken).ConfigureAwait(false);

        // An archive made by a server that already leaves the file out has no such entry; one made by an older server has it.
        foreach (var existing in archive.Entries.Where(e => string.Equals(e.FullName, entryName, StringComparison.Ordinal)).ToList())
        {
            existing.Delete();
        }

        var entry = archive.CreateEntry(entryName);
        var stream = await entry.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteConfigurationFileAsync(string relativePath, string content, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_configurationDirectoryPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private List<string> ListConfigurationFiles()
    {
        return Directory.EnumerateFiles(_configurationDirectoryPath, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(_configurationDirectoryPath, file))
            .Order(StringComparer.Ordinal)
            .ToList();
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
        jellyfinDatabaseProvider.Setup(p => p.PurgeDatabase(It.IsAny<JellyfinDbContext>(), It.IsAny<System.Collections.Generic.IEnumerable<string>>())).Returns(Task.CompletedTask);

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
