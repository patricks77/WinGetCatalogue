using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;

// Downloads the community index and maintains a local SQLite catalogue.
namespace WinGetCatalogue;

internal sealed class CatalogSource
{
    private static readonly Uri CommunityIndex = new("https://cdn.winget.microsoft.com/cache/source2.msix");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGetCatalogue");
    private static readonly string CachePath = Path.Combine(CacheDirectory, "catalogue-classement-v8.db");
    private static readonly string[] ReadableCaches = [CachePath,
        Path.Combine(CacheDirectory, "catalogue-classement-v7.db"),
        Path.Combine(CacheDirectory, "catalogue-classement-v6.db")];

    internal static async Task<SoftwareCatalog?> ReadCacheAsync(bool applyClassifications = true)
    {
        try
        {
            // Keep the old catalogue available offline while a fresh index is reclassified.
            string? path = ReadableCaches.FirstOrDefault(File.Exists);
            if (path is null) return null;
            return await Task.Run(() =>
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
                }.ToString());
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT published, etag FROM metadata LIMIT 1";
                using var metadata = command.ExecuteReader();
                if (!metadata.Read()) return null;
                var published = DateTimeOffset.FromUnixTimeSeconds(metadata.GetInt64(0));
                // Earlier caches lack monikers and the revised categories. Request the full index once.
                bool currentSchema = path == CachePath;
                string? etag = !currentSchema || metadata.IsDBNull(1) ? null : metadata.GetString(1);
                metadata.Close();
                string monikerColumn = currentSchema ? "moniker" : "NULL";
                command.CommandText = $"SELECT id,name,publisher,aliases,category,subcategory,description,version,hash,{monikerColumn} FROM packages ORDER BY ordinal";
                using var reader = command.ExecuteReader();
                var entries = new List<CatalogEntry>();
                var reviewed = applyClassifications ? SoftwareCatalog.LoadClassifications() : null;
                while (reader.Read())
                {
                    string id = reader.GetString(0);
                    string name = reader.GetString(1);
                    string category = reader.GetString(4);
                    string subcategory = reader.GetString(5);
                    // Enrich only unclassified cached entries; WinGet-classified entries stay authoritative.
                    if (category == "autres" && CatalogCategory.Classify(id, name, "", null) is var updated && updated.Category == "blockchain")
                    {
                        category = updated.Category;
                        subcategory = updated.Subcategory;
                    }
                    if (reviewed is not null)
                        (category, subcategory) = SoftwareCatalog.ApplyClassification(id, reader.GetString(8), category, subcategory, reviewed);
                    entries.Add(new CatalogEntry(id, name, reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetString(3).Split('\u001f', StringSplitOptions.RemoveEmptyEntries),
                        category, subcategory, reader.IsDBNull(6) ? null : reader.GetString(6),
                        reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)));
                }
                return entries.Count > 0 ? new SoftwareCatalog(published, etag, entries.ToArray()) : null;
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return null;
        }
    }

    internal async Task<SoftwareCatalog> RefreshAsync(SoftwareCatalog? previous, bool applyClassifications = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CommunityIndex);
        if (previous?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && previous is not null)
        {
            return previous;
        }

        response.EnsureSuccessStatusCode();
        using var package = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var archive = new ZipArchive(package);
        var database = archive.GetEntry("Public/index.db")
            ?? throw new InvalidDataException("L’archive WinGet ne contient pas son index.");
        string databasePath = Path.Combine(Path.GetTempPath(), $"WinGetCatalogue-{Guid.NewGuid():N}.db");

        try
        {
            database.ExtractToFile(databasePath);
            var catalog = ReadIndex(databasePath, response.Headers.ETag?.ToString());
            await WriteCacheAsync(catalog).ConfigureAwait(false);
            // Cache only source classifications so updated corrections can replace older suggestions.
            if (!applyClassifications) return catalog;
            var classifications = SoftwareCatalog.LoadClassifications();
            var entries = catalog.Entries.Select(entry =>
            {
                var (category, subcategory) = SoftwareCatalog.ApplyClassification(
                    entry.Id, entry.ManifestHash, entry.Category, entry.Subcategory, classifications);
                return entry with { Category = category, Subcategory = subcategory };
            }).ToArray();
            return new SoftwareCatalog(catalog.PublishedAt, catalog.ETag, entries);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    // The source index is authoritative; local overrides enrich rather than replace it.
    internal static SoftwareCatalog ReadIndex(string databasePath, string? etag)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using var connection = new SqliteConnection(connectionString.ToString());
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE name = 'majorVersion'";
        if (command.ExecuteScalar() is not "2")
        {
            throw new InvalidDataException("The WinGet source index format has changed.");
        }

        command.CommandText = "SELECT CAST(value AS INTEGER) FROM metadata WHERE name = 'lastwritetime'";
        if (command.ExecuteScalar() is not long timestamp || timestamp <= 0)
        {
            throw new InvalidDataException("L’index de la source WinGet n’indique pas sa date de publication.");
        }

        var overrides = SoftwareCatalog.LoadOverrides();
        command.CommandText = """
            WITH package_tags AS (
                SELECT map.package, group_concat(tag.tag, char(31)) AS tags
                FROM tags2_map AS map JOIN tags2 AS tag ON tag.rowid = map.tag
                GROUP BY map.package
            ), package_publishers AS (
                SELECT package, group_concat(norm_publisher, char(31)) AS publishers
                FROM norm_publishers2
                GROUP BY package
            )
            SELECT p.id, p.name, coalesce(t.tags, ''), coalesce(n.publishers, ''),
                   p.latest_version, lower(hex(p.hash)), p.moniker
            FROM packages AS p
            LEFT JOIN package_tags AS t ON t.package = p.rowid
            LEFT JOIN package_publishers AS n ON n.package = p.rowid
            """;

        using var reader = command.ExecuteReader();
        var entries = new List<CatalogEntry>();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            string name = reader.GetString(1);
            string[] aliases = reader.GetString(3).Split('\u001f', StringSplitOptions.RemoveEmptyEntries);
            overrides.TryGetValue(id, out var local);
            var classification = CatalogCategory.Classify(id, name, reader.GetString(2), local?.Category);
            entries.Add(new CatalogEntry(id, local?.Name ?? name, local?.Publisher, aliases,
                classification.Category, classification.Subcategory, local?.Description, reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        if (entries.Count == 0 || entries.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
        {
            throw new InvalidDataException("L’index WinGet est vide ou contient des identifiants en double.");
        }

        return new SoftwareCatalog(DateTimeOffset.FromUnixTimeSeconds(timestamp), etag,
            entries.OrderBy(entry => entry.Name, StringComparer.Create(SoftwareCatalog.Culture, ignoreCase: true))
                .ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray());
    }

    private static async Task WriteCacheAsync(SoftwareCatalog catalog)
    {
        string temporaryPath = $"{CachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            await Task.Run(() =>
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = temporaryPath, Pooling = false,
                }.ToString());
                connection.Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "CREATE TABLE metadata(published INTEGER NOT NULL, etag TEXT); CREATE TABLE packages(ordinal INTEGER PRIMARY KEY,id TEXT NOT NULL,name TEXT NOT NULL,publisher TEXT,aliases TEXT NOT NULL,category TEXT NOT NULL,subcategory TEXT NOT NULL,description TEXT,version TEXT NOT NULL,hash TEXT NOT NULL,moniker TEXT); CREATE INDEX packages_category ON packages(category,subcategory); CREATE INDEX packages_id ON packages(id);";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO metadata VALUES ($published,$etag)";
                command.Parameters.AddWithValue("$published", catalog.PublishedAt.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$etag", (object?)catalog.ETag ?? DBNull.Value);
                command.ExecuteNonQuery();
                command.Parameters.Clear();
                command.CommandText = "INSERT INTO packages VALUES ($ordinal,$id,$name,$publisher,$aliases,$category,$subcategory,$description,$version,$hash,$moniker)";
                foreach (string parameter in new[] { "$ordinal", "$id", "$name", "$publisher", "$aliases", "$category", "$subcategory", "$description", "$version", "$hash", "$moniker" })
                    command.Parameters.Add(new SqliteParameter(parameter, DBNull.Value));
                for (int index = 0; index < catalog.Entries.Length; index++)
                {
                    var entry = catalog.Entries[index];
                    object?[] values = [index, entry.Id, entry.Name, entry.Publisher, string.Join('\u001f', entry.PublisherAliases),
                        entry.Category, entry.Subcategory, entry.Description, entry.Version, entry.ManifestHash, entry.Moniker];
                    for (int i = 0; i < values.Length; i++) command.Parameters[i].Value = values[i] ?? DBNull.Value;
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
            }).ConfigureAwait(false);
            File.Move(temporaryPath, CachePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(exception);
            }
        }
    }
}
