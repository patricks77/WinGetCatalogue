using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

// Reads installed software and available updates from WinGet sources.
namespace WinGetCatalogue;

public sealed record InstalledSoftware(string Name, string Id, string InstalledVersion, string? AvailableVersion, bool UpdateAvailable, string Source = "winget")
{
    public string InstalledLabel => $"Installed: {InstalledVersion}";
    public string UpdateLabel => UpdateAvailable ? $"Update: {AvailableVersion} · {Source}" : $"No update detected · {Source}";

    internal bool SamePackage(InstalledSoftware item) =>
        Id.Equals(item.Id, StringComparison.OrdinalIgnoreCase) &&
        Source.Equals(item.Source, StringComparison.OrdinalIgnoreCase);
}

internal sealed record InstalledUpgrade(InstalledSoftware Previous, string? TargetVersion, int ExitCode, bool PackageInUse)
{
    // WinGet can return zero as soon as an MSIX package is staged. Verify the target against the inventory.
    internal InstalledSoftware? FindInstalledUpdate(InstalledSoftware[]? inventory) =>
        inventory?.FirstOrDefault(item => Previous.SamePackage(item) &&
            !item.InstalledVersion.Equals(Previous.InstalledVersion, StringComparison.OrdinalIgnoreCase) &&
            item.InstalledVersion.Equals(TargetVersion, StringComparison.OrdinalIgnoreCase));
}

internal sealed record InstalledSnapshot(InstalledSoftware[] Packages, string? Warning);

internal static class InstalledSoftwareReader
{
    internal static async Task<InstalledSnapshot> ReadAsync()
    {
        var packages = new List<InstalledSoftware>();
        var warnings = new List<string>();
        int sourcesRead = 0;
        foreach (string source in new[] { "winget", "msstore" })
        {
            try
            {
                packages.AddRange(await ReadSourceAsync(source));
                sourcesRead++;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException or Win32Exception)
            {
                Debug.WriteLine($"Could not read the WinGet source '{source}': {exception}");
                warnings.Add($"{source} : {exception.Message}");
            }
        }
        if (sourcesRead == 0) throw new InvalidOperationException(string.Join(" · ", warnings));
        return new InstalledSnapshot(packages.OrderBy(package => package.Name,
            StringComparer.Create(SoftwareCatalog.Culture, true)).ToArray(),
            warnings.Count == 0 ? null : string.Join(" · ", warnings));
    }

    private static async Task<InstalledSoftware[]> ReadSourceAsync(string source)
    {
        string path = WinGetCommand.ExecutablePath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("WinGet was not found.");
        }

        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "list", "--source", source, "--disable-interactivity" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("WinGet did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("Reading WinGet output timed out after 45 seconds.");
        }

        string text = await output;
        string error = await errors;
        if (process.ExitCode != 0)
        {
            if (unchecked((uint)process.ExitCode) == 0x8A150014)
                return [];
            if (WinGetCommand.GetSourceFailureMessage(process.ExitCode) is { } message)
                throw new InvalidOperationException(message);
            throw new InvalidOperationException($"WinGet exited with code 0x{process.ExitCode:X8}. {error.Trim()}");
        }

        return Parse(text, source);
    }

    // WinGet pads cells to the headers' display columns. Even a single space may
    // separate two cells, and a blank Available cell must not consume Source.
    internal static InstalledSoftware[] Parse(string text, string source = "winget")
    {
        var lines = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "").Replace("\r", "").Split('\n');
        int separator = Array.FindIndex(lines, line => Regex.IsMatch(line.Trim(), @"^-{10,}$"));
        if (separator < 1)
        {
            if (lines.Any(line => line.Trim().StartsWith("No installed package found", StringComparison.OrdinalIgnoreCase) ||
                                  line.Trim().StartsWith("Aucun package installé", StringComparison.OrdinalIgnoreCase) ||
                                  line.Trim().StartsWith("Aucun paquet installé", StringComparison.OrdinalIgnoreCase)))
                return [];
            throw new InvalidDataException("The WinGet list format was not recognized.");
        }

        string header = lines[separator - 1].TrimStart('\uFEFF');
        // Keep multiword labels together when padded; fall back to individual
        // words for narrow tables whose column labels are only one space apart.
        var headings = Regex.Matches(header.TrimEnd(), @"\S+(?: \S+)*(?= {2,}|$)").ToArray();
        if (headings.Length is < 3 or > 5 || Regex.IsMatch(header, @"^\s*(Name|Nom)\s", RegexOptions.IgnoreCase))
            headings = Regex.Matches(header, @"Version disponible|\S+", RegexOptions.IgnoreCase).ToArray();
        if (headings.Length is < 3 or > 5)
            throw new InvalidDataException("The WinGet list columns were not recognized.");

        int[] starts = headings.Select(heading => DisplayWidth(header.AsSpan(0, heading.Index))).ToArray();
        bool hasAvailable = headings.Length >= 4 &&
            !headings[3].Value.Equals("Source", StringComparison.OrdinalIgnoreCase);
        var packages = new List<InstalledSoftware>();
        foreach (string line in lines.Skip(separator + 1))
        {
            string[]? fields = ReadColumns(line, starts);
            if (fields is null || fields[0].Length == 0 || fields[2].Length == 0 ||
                !WinGetCommand.IsValidId(fields[1]) || fields[1].EndsWith('…')) continue;

            string? available = hasAvailable && fields[3].Length > 0 && !fields[3].EndsWith('…') &&
                !fields[3].Equals(source, StringComparison.OrdinalIgnoreCase) ? fields[3] : null;
            packages.Add(new InstalledSoftware(fields[0], fields[1], fields[2], available, available is not null, source));
        }

        return packages.Distinct().OrderBy(package => package.Name, StringComparer.Create(SoftwareCatalog.Culture, true)).ToArray();
    }

    private static string[]? ReadColumns(string line, int[] starts)
    {
        var fields = new string[starts.Length];
        int offset = 0;
        int column = 0;
        for (int i = 1; i < starts.Length; i++)
        {
            int cellStart = offset;
            while (offset < line.Length && column < starts[i])
            {
                var remaining = line.AsSpan(offset);
                int length = StringInfo.GetNextTextElementLength(remaining);
                column += TextElementWidth(remaining[..length]);
                offset += length;
            }
            if (column > starts[i]) return null;
            fields[i - 1] = line[cellStart..offset].Trim();
        }
        fields[^1] = line[offset..].Trim();
        return fields;
    }

    private static int DisplayWidth(ReadOnlySpan<char> text)
    {
        int width = 0;
        while (!text.IsEmpty)
        {
            int length = StringInfo.GetNextTextElementLength(text);
            width += TextElementWidth(text[..length]);
            text = text[length..];
        }
        return width;
    }

    private static int TextElementWidth(ReadOnlySpan<char> element)
    {
        if (char.IsAscii(element[0])) return 1;

        // Match WinGet's use of the first code point's East Asian Width property.
        const int EastAsianWidth = 0x1004;
        Rune rune = Rune.DecodeFromUtf16(element, out Rune value, out _) == System.Buffers.OperationStatus.Done
            ? value : Rune.ReplacementChar;
        int width = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)
            ? GetUnicodeProperty(rune.Value, EastAsianWidth)
            : GetLegacyUnicodeProperty(rune.Value, EastAsianWidth);
        return width is 3 or 5 ? 2 : 1; // ICU U_EA_FULLWIDTH and U_EA_WIDE.
    }

    [DllImport("icu.dll", EntryPoint = "u_getIntPropertyValue", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetUnicodeProperty(int codePoint, int property);

    [DllImport("icuuc.dll", EntryPoint = "u_getIntPropertyValue", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetLegacyUnicodeProperty(int codePoint, int property);
}
