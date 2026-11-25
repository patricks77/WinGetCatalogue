using Microsoft.Data.Sqlite;

namespace WinGetCatalogue;

// Stores verified icon bytes once per hash. Multiple package versions can share the same image.
internal sealed class PackageDetailsCache
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _lock = new(1);
    private bool _initialized;

    internal PackageDetailsCache(string? databasePath = null)
    {
        _databasePath = databasePath ?? Path.Combine(GetCacheFolder(), "fiches-et-icones-v2.db");
    }

    private static string GetCacheFolder()
    {
        try
        {
            // An explicit package-local path keeps the SQLite database and its WAL together.
            return Path.Combine(Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path, "WinGetCatalogue");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGetCatalogue");
        }
    }

    internal async Task<(PackageDetails Details, byte[]? Icon)?> ReadAsync(
        CatalogEntry entry, CancellationToken cancellationToken, bool includeIcon = true)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT d.publisher, d.description, d.icon_url, d.icon_hash, d.localized_name, i.bytes
                FROM details AS d LEFT JOIN icons AS i ON $includeIcon AND i.hash = d.icon_hash
                WHERE d.id = $id AND d.manifest_hash = $hash AND d.locale_revision = 1
                """;
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$hash", entry.ManifestHash);
            command.Parameters.AddWithValue("$includeIcon", includeIcon);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            var icon = !reader.IsDBNull(2) && !reader.IsDBNull(3)
                && Uri.TryCreate(reader.GetString(2), UriKind.Absolute, out var uri)
                ? new WinGetIcon(uri, reader.GetString(3)) : null;
            byte[]? bytes = icon is not null && !reader.IsDBNull(5) ? (byte[])reader.GetValue(5) : null;
            return (new PackageDetails(reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), icon, reader.IsDBNull(4) ? null : reader.GetString(4)), bytes);
        }
        finally { _lock.Release(); }
    }

    internal async Task<Dictionary<string, string>> ReadCachedManifestsAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, manifest_hash FROM details WHERE locale_revision = 1";
            using var reader = command.ExecuteReader();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read()) result.Add(reader.GetString(0), reader.GetString(1));
            return result;
        }
        finally { _lock.Release(); }
    }

    internal async Task<byte[]?> ReadIconAsync(string hash, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT bytes FROM icons WHERE hash = $hash";
            command.Parameters.AddWithValue("$hash", hash);
            return command.ExecuteScalar() as byte[];
        }
        finally { _lock.Release(); }
    }

    internal async Task WriteAsync(CatalogEntry entry, PackageDetails details, byte[]? icon, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (details.Icon is { } descriptor && icon is not null)
            {
                command.CommandText = """
                    INSERT INTO icons(hash, bytes) VALUES ($hash, $bytes)
                    ON CONFLICT(hash) DO UPDATE SET bytes = excluded.bytes
                    """;
                command.Parameters.AddWithValue("$hash", descriptor.Sha256);
                command.Parameters.AddWithValue("$bytes", icon);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
            }

            command.CommandText = """
                INSERT INTO details(id,manifest_hash,publisher,description,icon_url,icon_hash,localized_name,locale_revision)
                VALUES($id,$manifest,$publisher,$description,$url,$hash,$name,1)
                ON CONFLICT(id) DO UPDATE SET manifest_hash=excluded.manifest_hash,publisher=excluded.publisher,
                    description=excluded.description,icon_url=excluded.icon_url,icon_hash=excluded.icon_hash,
                    localized_name=excluded.localized_name,locale_revision=1
                """;
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$manifest", entry.ManifestHash);
            command.Parameters.AddWithValue("$publisher", (object?)details.Publisher ?? DBNull.Value);
            command.Parameters.AddWithValue("$description", (object?)details.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("$url", (object?)details.Icon?.Source.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$hash", (object?)details.Icon?.Sha256 ?? DBNull.Value);
            command.Parameters.AddWithValue("$name", (object?)details.Name ?? DBNull.Value);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    internal async Task CompactAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA freelist_count";
            // Reclaim the old blob pages after migration, without vacuuming on normal startups.
            if ((long)command.ExecuteScalar()! < 4096) return;
            command.CommandText = "VACUUM";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            command.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    private static void CreateDetails(SqliteCommand command)
    {
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS details (
                id TEXT PRIMARY KEY COLLATE NOCASE, manifest_hash TEXT NOT NULL, publisher TEXT,
                description TEXT, icon_url TEXT, icon_hash TEXT COLLATE NOCASE, localized_name TEXT,
                locale_revision INTEGER NOT NULL
            )
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        try
        {
            connection.Open();
            if (!_initialized)
            {
                Initialize(connection);
                _initialized = true;
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Initialize(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL";
        command.ExecuteNonQuery();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS icons (hash TEXT PRIMARY KEY COLLATE NOCASE, bytes BLOB NOT NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(details)";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));

        if (columns.Contains("icon_bytes"))
        {
            // SQLite rolls back the whole migration if writing the new schema fails.
            command.CommandText = """
                INSERT OR IGNORE INTO icons(hash,bytes)
                SELECT icon_hash,icon_bytes FROM details WHERE length(icon_hash) = 64 AND icon_bytes IS NOT NULL;
                ALTER TABLE details RENAME TO legacy_details;
                """;
            command.ExecuteNonQuery();
            CreateDetails(command);
            string localizedName = columns.Contains("localized_name") ? "localized_name" : "NULL";
            string localeRevision = columns.Contains("locale_revision") ? "locale_revision" : "0";
            command.CommandText = $"""
                INSERT INTO details(id,manifest_hash,publisher,description,icon_url,icon_hash,localized_name,locale_revision)
                SELECT id,manifest_hash,publisher,description,icon_url,icon_hash,{localizedName},{localeRevision} FROM legacy_details;
                DROP TABLE legacy_details;
                """;
            command.ExecuteNonQuery();
        }
        else
        {
            CreateDetails(command);
        }
        transaction.Commit();
    }
}
