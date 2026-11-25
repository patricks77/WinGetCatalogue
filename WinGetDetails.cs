using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using YamlDotNet.RepresentationModel;

// Retrieves and verifies WinGet manifests, localized details, and icons.
namespace WinGetCatalogue;

internal sealed record WinGetIcon(Uri Source, string Sha256);
internal sealed record PackageDetails(string? Publisher, string? Description, WinGetIcon? Icon, string? Name = null);

internal sealed class WinGetDetails
{
    private const uint MsZip = 2;
    private const int InsufficientBuffer = 122;
    private const int MaximumYamlBytes = 8 * 1024 * 1024;
    private const int MaximumIconBytes = 1024 * 1024;
    private static readonly Uri Cdn = new("https://cdn.winget.microsoft.com");
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly string LegacyIconCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinGetCatalogue", "icons");
    private readonly ConcurrentDictionary<(string Id, string Hash), PackageDetails> _cache = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _iconRetryAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly PackageDetailsCache _persistent;
    private readonly SemaphoreSlim _requests = new(4);

    internal WinGetDetails(PackageDetailsCache? persistent = null) => _persistent = persistent ?? new();

    internal async Task<(PackageDetails Details, byte[]? Icon)> GetCachedAsync(CatalogEntry entry, CancellationToken token)
    {
        var saved = await TryReadCacheAsync(entry, token).ConfigureAwait(false);
        if (saved is { } cached && HasCurrentIcon(cached))
        {
            return cached;
        }

        await _requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            saved = await TryReadCacheAsync(entry, token).ConfigureAwait(false);
            if (saved is { } existing && HasCurrentIcon(existing)) return existing;
            var details = saved?.Details ?? await FindAsync(entry, token).ConfigureAwait(false);
            byte[]? bytes = null;
            try
            {
                if (details.Icon is { } icon)
                {
                    bytes = await LoadIconAsync(icon, token).ConfigureAwait(false);
                    _iconRetryAfter.TryRemove(icon.Sha256, out _);
                }
            }
            catch (Exception exception) when (!token.IsCancellationRequested && exception is IOException or InvalidDataException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
            {
                // A missing or oversized icon must not hide an otherwise valid description.
                if (details.Icon is { } icon) _iconRetryAfter[icon.Sha256] = DateTimeOffset.UtcNow.AddMinutes(5);
                DiagnosticLog.Write($"Icon unavailable: {entry.Id}", exception);
            }
            if (saved is null || bytes is not null)
                await TryWriteCacheAsync(entry, details, bytes, token).ConfigureAwait(false);
            return (details, bytes);
        }
        finally { _requests.Release(); }
    }

    // Details can be displayed straight from the local database, before attempting any icon request.
    internal async Task<PackageDetails> GetMetadataAsync(CatalogEntry entry, CancellationToken token)
    {
        var saved = await TryReadCacheAsync(entry, token, includeIcon: false).ConfigureAwait(false);
        if (saved is { } cached) return cached.Details;

        await _requests.WaitAsync(token).ConfigureAwait(false);
        try
        {
            saved = await TryReadCacheAsync(entry, token, includeIcon: false).ConfigureAwait(false);
            if (saved is { } existing) return existing.Details;
            var details = await FindAsync(entry, token).ConfigureAwait(false);
            await TryWriteCacheAsync(entry, details, null, token).ConfigureAwait(false);
            return details;
        }
        finally { _requests.Release(); }
    }

    private bool HasCurrentIcon((PackageDetails Details, byte[]? Icon) cached) =>
        cached.Details.Icon is null || cached.Icon is not null ||
        (_iconRetryAfter.TryGetValue(cached.Details.Icon.Sha256, out var retryAfter) && retryAfter > DateTimeOffset.UtcNow);

    private async Task<(PackageDetails Details, byte[]? Icon)?> TryReadCacheAsync(
        CatalogEntry entry, CancellationToken token, bool includeIcon = true)
    {
        try
        {
            var result = await _persistent.ReadAsync(entry, token, includeIcon).ConfigureAwait(false);
            if (result is { Details.Icon: { } icon, Icon: { } bytes } && !IsVerifiedIcon(bytes, icon.Sha256))
                return (result.Value.Details, null);
            return result;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            // A damaged cache should not stop the grid from fetching the details directly.
            DiagnosticLog.Write($"Details cache read failed: {entry.Id}", exception);
            return null;
        }
    }

    private async Task TryWriteCacheAsync(CatalogEntry entry, PackageDetails details, byte[]? icon, CancellationToken token)
    {
        try { await _persistent.WriteAsync(entry, details, icon, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Details cache write failed: {entry.Id}", exception);
        }
    }

    // Populate the persistent cache gradually without blocking catalogue navigation.
    internal async Task FillInBackgroundAsync(IEnumerable<CatalogEntry> entries, CancellationToken token)
    {
        Dictionary<string, string> cached;
        try
        {
            // A single metadata query avoids opening and decoding thousands of already cached icons.
            cached = await _persistent.ReadCachedManifestsAsync(token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("Unable to prefill the package details cache", exception);
            return;
        }

        await FinishCacheMigrationAsync(token).ConfigureAwait(false);
        int connectionFailures = 0;
        foreach (var entry in entries)
        {
            if (token.IsCancellationRequested) break;
            if (cached.TryGetValue(entry.Id, out string? hash) &&
                string.Equals(hash, entry.ManifestHash, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                await GetCachedAsync(entry, token).ConfigureAwait(false);
                connectionFailures = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception exception) when (exception is HttpRequestException { StatusCode: null } or OperationCanceledException)
            {
                DiagnosticLog.Write($"Background cache request failed: {entry.Id}", exception);
                if (++connectionFailures >= 3) break;
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException or
                SqliteException or InvalidDataException or YamlDotNet.Core.YamlException)
            {
                DiagnosticLog.Write($"Background package metadata unavailable: {entry.Id}", exception);
            }
        }
    }

    private async Task FinishCacheMigrationAsync(CancellationToken token)
    {
        try
        {
            if (Directory.Exists(LegacyIconCachePath))
            {
                foreach (string path in Directory.EnumerateFiles(LegacyIconCachePath))
                {
                    token.ThrowIfCancellationRequested();
                    string hash = Path.GetFileName(path);
                    if (!IsSha256(hash)) continue;
                    // Retire only a generated file whose exact image is still present in SQLite.
                    var stored = await _persistent.ReadIconAsync(hash, token).ConfigureAwait(false);
                    if (stored is not null && IsVerifiedIcon(stored, hash) &&
                        await ReadLegacyIconAsync(hash, token).ConfigureAwait(false) is { } original &&
                        stored.AsSpan().SequenceEqual(original))
                    {
                        File.Delete(path);
                    }
                }
            }
            await _persistent.CompactAsync(token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("Cache maintenance failed", exception);
        }
    }

    internal async Task<PackageDetails> FindAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        if (!WinGetCommand.IsValidId(entry.Id) || !IsSha256(entry.ManifestHash))
        {
            return new PackageDetails(null, null, null);
        }

        var key = (entry.Id, entry.ManifestHash);
        if (_cache.TryGetValue(key, out var details))
        {
            return details;
        }

        var versionsUri = new Uri(Cdn,
            $"/cache/packages/{Uri.EscapeDataString(entry.Id)}/{entry.ManifestHash[..8]}/versionData.mszyml");
        var versionBytes = await DownloadVerifiedAsync(versionsUri, entry.ManifestHash, MaximumYamlBytes, cancellationToken)
            .ConfigureAwait(false);
        var versions = ParseYaml(DecompressVersionData(versionBytes));
        var version = GetMappings(versions, "vD").FirstOrDefault(item => GetText(item, "v") == entry.Version);
        string? relative = version is null ? null : GetText(version, "rP");
        string? manifestHash = version is null ? null : GetText(version, "s256H");

        if (relative is null || !relative.StartsWith("manifests/", StringComparison.Ordinal) || !IsSha256(manifestHash))
        {
            throw new InvalidDataException("The WinGet version file does not reference the expected manifest.");
        }

        string[] segments = relative.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || segment.Contains('\\')))
        {
            throw new InvalidDataException("The WinGet manifest path is invalid.");
        }

        var manifestUri = new Uri(Cdn, "/cache/" + string.Join('/', segments.Select(Uri.EscapeDataString)));
        var manifestBytes = await DownloadVerifiedAsync(manifestUri, manifestHash!, MaximumYamlBytes, cancellationToken)
            .ConfigureAwait(false);
        var manifest = ParseYaml(manifestBytes);
        if (!string.Equals(GetText(manifest, "PackageIdentifier"), entry.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The WinGet manifest references a different package.");
        }

        // Prefer English localization, then use the default manifest when unavailable.
        var english = GetMappings(manifest, "Localization")
            .Where(item => GetText(item, "PackageLocale")?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(item => string.Equals(GetText(item, "PackageLocale"), "en-US", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        string? publisher = english is null ? null : GetText(english, "Publisher");
        string? description = english is null ? null : GetText(english, "ShortDescription") ?? GetText(english, "Description");
        details = new PackageDetails(publisher ?? GetText(manifest, "Publisher"),
            description ?? GetText(manifest, "ShortDescription") ?? GetText(manifest, "Description"),
            (english is null ? null : GetIcon(english)) ?? GetIcon(manifest),
            english is null ? GetText(manifest, "PackageName") : GetText(english, "PackageName") ?? GetText(manifest, "PackageName"));
        _cache.TryAdd(key, details);
        return details;
    }

    private async Task<byte[]> LoadIconAsync(WinGetIcon icon, CancellationToken token)
    {
        if (!IsSha256(icon.Sha256) || !IsTrustedIconUri(icon.Source))
            throw new InvalidDataException("The WinGet icon reference is invalid.");

        // Several packages may share one icon. Read the content-addressed blob before downloading.
        try
        {
            var stored = await _persistent.ReadIconAsync(icon.Sha256, token).ConfigureAwait(false);
            if (stored is not null && IsVerifiedIcon(stored, icon.Sha256)) return stored;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Unable to read cached icon: {icon.Sha256}", exception);
        }

        return await ReadLegacyIconAsync(icon.Sha256, token).ConfigureAwait(false)
            ?? await DownloadVerifiedAsync(icon.Source, icon.Sha256, MaximumIconBytes, token).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadLegacyIconAsync(string hash, CancellationToken token)
    {
        string path = Path.Combine(LegacyIconCachePath, hash);
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length is > 0 and <= MaximumIconBytes)
            {
                var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
                if (IsVerifiedIcon(bytes, hash)) return bytes;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Unable to read legacy icon: {hash}", exception);
        }
        return null;
    }

    private static WinGetIcon? GetIcon(YamlMappingNode manifest)
    {
        foreach (var icon in GetMappings(manifest, "Icons"))
        {
            string? hash = GetText(icon, "IconSha256");
            if (GetText(icon, "IconFileType") is not ("png" or "jpeg" or "ico") ||
                GetText(icon, "IconTheme") is not (null or "default") ||
                !IsSha256(hash) ||
                !Uri.TryCreate(GetText(icon, "IconUrl"), UriKind.Absolute, out var uri) ||
                !IsTrustedIconUri(uri))
            {
                continue;
            }

            return new WinGetIcon(uri, hash!.ToLowerInvariant());
        }

        return null;
    }

    private static bool IsTrustedIconUri(Uri uri) =>
        uri.Scheme == Cdn.Scheme && uri.Host == Cdn.Host && uri.IsDefaultPort &&
        uri.AbsolutePath.StartsWith("/icons/", StringComparison.Ordinal);

    private static bool IsVerifiedIcon(byte[] bytes, string hash) =>
        bytes.Length is > 0 and <= MaximumIconBytes && HasHash(bytes, hash);

    private static async Task<byte[]> DownloadVerifiedAsync(Uri uri, string hash, int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var token = timeout.Token;

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximumBytes)
            {
                throw new InvalidDataException("The WinGet metadata file exceeds the supported size.");
            }

            output.Write(buffer, 0, count);
        }

        byte[] bytes = output.ToArray();
        if (!HasHash(bytes, hash))
        {
            throw new InvalidDataException("The downloaded WinGet file hash does not match the index.");
        }

        return bytes;
    }

    private static bool IsSha256(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    private static bool HasHash(byte[] bytes, string expected) =>
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static YamlMappingNode ParseYaml(byte[] bytes)
    {
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return yaml.Documents.FirstOrDefault()?.RootNode as YamlMappingNode
            ?? throw new InvalidDataException("The WinGet manifest could not be read.");
    }

    private static IEnumerable<YamlMappingNode> GetMappings(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) && child is YamlSequenceNode sequence
            ? sequence.OfType<YamlMappingNode>() : [];

    private static string? GetText(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar &&
        !string.IsNullOrWhiteSpace(scalar.Value) ? scalar.Value : null;

    private static byte[] DecompressVersionData(byte[] compressed)
    {
        if (!CreateDecompressor(MsZip, IntPtr.Zero, out var decompressor))
        {
            throw new Win32Exception();
        }

        using (decompressor)
        {
            Decompress(decompressor, compressed, (nuint)compressed.Length, null, 0, out nuint size);
            if (Marshal.GetLastWin32Error() != InsufficientBuffer || size == 0 || size > MaximumYamlBytes)
            {
                throw new InvalidDataException("The WinGet version file could not be read.");
            }

            var output = new byte[(int)size];
            if (!Decompress(decompressor, compressed, (nuint)compressed.Length, output, size, out _))
            {
                throw new Win32Exception();
            }

            return output;
        }
    }

    [DllImport("cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDecompressor(uint algorithm, IntPtr allocation, out DecompressorHandle handle);

    [DllImport("cabinet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Decompress(DecompressorHandle handle, byte[] input, nuint inputSize,
        [Out] byte[]? output, nuint outputSize, out nuint resultSize);

    [DllImport("cabinet.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDecompressor(IntPtr handle);

    private sealed class DecompressorHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => CloseDecompressor(handle);
    }
}
