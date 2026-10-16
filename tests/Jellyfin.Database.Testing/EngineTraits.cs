using System;
using Jellyfin.Database.Implementations;

namespace Jellyfin.Database.Testing;

/// <summary>
/// Traits of a database engine for test registration and assertions.
/// </summary>
public sealed class EngineTraits
{
    /// <summary>
    /// Gets the provider.
    /// </summary>
    public required TestDbProvider Provider { get; init; }

    /// <summary>
    /// Gets the provider key.
    /// </summary>
    public required string ProviderKey { get; init; }

    /// <summary>
    /// Gets the provider type.
    /// </summary>
    public required Type ProviderType { get; init; }

    /// <summary>
    /// Gets a value indicating whether the engine is embedded.
    /// </summary>
    public required bool IsEmbedded { get; init; }

    /// <summary>
    /// Gets a value indicating whether the engine is registered.
    /// </summary>
    public required bool IsRegistered { get; init; }

    /// <summary>
    /// Gets the server marker.
    /// </summary>
    public string? ServerMarker { get; init; }

    /// <summary>
    /// Gets the traits for a provider.
    /// </summary>
    /// <param name="provider">The database provider.</param>
    /// <returns>The engine traits.</returns>
    public static EngineTraits For(TestDbProvider provider) => provider switch
    {
        TestDbProvider.Sqlite => new EngineTraits
        {
            Provider = TestDbProvider.Sqlite,
            ProviderKey = "Jellyfin-SQLite",
            ProviderType = typeof(Jellyfin.Database.Providers.Sqlite.SqliteDatabaseProvider),
            IsEmbedded = true,
            IsRegistered = true,
        },
        TestDbProvider.Postgres => new EngineTraits
        {
            Provider = TestDbProvider.Postgres,
            ProviderKey = "Jellyfin-PostgreSQL",
            ProviderType = typeof(Jellyfin.Database.Providers.Postgres.PostgresDatabaseProvider),
            IsEmbedded = false,
            IsRegistered = true,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}
