using System.Text.RegularExpressions;

// Groups related package variants into one discoverable application.
namespace WinGetCatalogue;

public sealed class CatalogApplication
{
    // These package namespaces also include editions or localizations that use other suffixes.
    private static readonly Dictionary<string, string> FamilyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EclipseAdoptium.Temurin"] = "Eclipse Temurin",
        ["JetBrains.IntelliJIDEA"] = "IntelliJ IDEA",
        ["JetBrains.PyCharm"] = "PyCharm",
        ["Microsoft.DotNet.AspNetCore"] = "ASP.NET Core Runtime",
        ["Microsoft.DotNet.DesktopRuntime"] = ".NET Desktop Runtime",
        ["Microsoft.DotNet.Runtime"] = ".NET Runtime",
        ["Microsoft.DotNet.SDK"] = ".NET SDK",
        ["Microsoft.OpenJDK"] = "Microsoft Build of OpenJDK",
        ["Microsoft.VisualStudioCode"] = "Microsoft Visual Studio Code",
        ["Mozilla.Firefox"] = "Mozilla Firefox",
        ["Mozilla.Thunderbird"] = "Mozilla Thunderbird",
        ["Oracle.JDK"] = "Oracle JDK",
        ["Python.Python"] = "Python",
    };

    private static readonly HashSet<string> VariantSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "alpha", "arm", "arm64", "beta", "canary", "dev", "eap", "exe", "insider", "insiders",
        "lts", "msi", "msix", "nightly", "portable", "preview", "rc", "snapshot", "stable", "x64", "x86",
    };

    private CatalogApplication(string name, CatalogEntry primary, CatalogEntry[] packages, CatalogEntry? matchingPackage = null)
    {
        Name = name;
        Primary = primary;
        Packages = packages;
        MatchingPackage = matchingPackage;
    }

    public string Name { get; }
    public CatalogEntry Primary { get; }
    public CatalogEntry Representative => MatchingPackage ?? Primary;
    internal CatalogEntry[] Packages { get; }
    internal CatalogEntry? MatchingPackage { get; }

    internal CatalogApplication WithMatch(CatalogEntry entry) => new(Name, Primary, Packages, entry);

    // Group only known variants so unrelated applications retain separate entries.
    internal static CatalogApplication[] Group(CatalogEntry[] entries)
    {
        var packageIds = entries.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nameComparer = StringComparer.Create(SoftwareCatalog.Culture, ignoreCase: true);
        // A shared, descriptive package-family prefix can span multiple publishers.
        // Require at least two members and an explicit pack type to avoid merging unrelated products.
        var nameFamilies = entries.Select(entry => (Entry: entry, Family: GetNamedPackageFamily(entry.Name)))
            .Where(item => item.Family is not null)
            .GroupBy(item => item.Family!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.Entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Other repeated prefixes need an additional shared WinGet namespace and three members.
        var scopedFamilies = entries.Select(entry => GetScopedNameFamily(entry))
            .Where(family => family is not null)
            .GroupBy(family => family!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(2).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exactFamilies = entries.Select(entry => GetScopedKey(entry.Id, entry.Name))
            .Where(family => family is not null)
            .GroupBy(family => family!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any())
            .Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? GetNameKey(CatalogEntry entry)
        {
            if (GetNamedPackageFamily(entry.Name) is { } family && nameFamilies.Contains(family)) return "\0named:" + family;
            if (GetScopedNameFamily(entry) is { } scoped && scopedFamilies.Contains(scoped)) return "\0scoped:" + scoped;
            // Include an entry named exactly like its existing family, even without a suffix.
            if (GetScopedKey(entry.Id, entry.Name) is { } exact && (scopedFamilies.Contains(exact) || exactFamilies.Contains(exact)))
                return "\0scoped:" + exact;
            return null;
        }

        // Name rules may merge identifier families, but must never partition one. If members
        // suggest different names, the existing identifier family has the stronger evidence.
        var identifierFamilies = entries.GroupBy(entry => GetFamilyKey(entry.Id, packageIds), StringComparer.OrdinalIgnoreCase)
            .Select(family =>
            {
                var candidates = FamilyNames.ContainsKey(family.Key) ? [] : family.Select(GetNameKey)
                    .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
                string key = candidates.Length == 1 ? candidates[0] : family.Key;
                return (Key: key, Identifier: family.Key, Packages: family);
            });

        return identifierFamilies.GroupBy(family => family.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var packages = group.SelectMany(family => family.Packages)
                    .OrderBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).ToArray();
                string? namedFamily = group.Key.StartsWith("\0named:", StringComparison.Ordinal) ? group.Key[7..]
                    : group.Key.StartsWith("\0scoped:", StringComparison.Ordinal) ? group.Key[(group.Key.IndexOf('\u001f') + 1)..]
                    : null;
                var primary = packages.FirstOrDefault(entry => group.Any(family =>
                        family.Packages.Count() > 1 && family.Identifier.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)))
                    ?? packages.MaxBy(entry => Version.TryParse(entry.Version, out var version) ? version : null)
                    ?? packages[0];
                return new CatalogApplication(namedFamily ?? FamilyNames.GetValueOrDefault(group.Key, primary.Name), primary, packages);
            })
            .OrderBy(application => application.Name, nameComparer)
            .ThenBy(application => application.Primary.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? GetScopedNameFamily(CatalogEntry entry)
    {
        var match = Regex.Match(entry.Name, @"^(.{6,100}?)\s+[-–—|]\s+(\S.*)$", RegexOptions.CultureInvariant);
        return match.Success ? GetScopedKey(entry.Id, match.Groups[1].Value.Trim()) : null;
    }

    private static string? GetScopedKey(string id, string name)
    {
        int first = id.IndexOf('.');
        if (first < 0) return null;
        int second = id.IndexOf('.', first + 1);
        return (second < 0 ? id : id[..second]) + '\u001f' + name.Trim();
    }

    private static string? GetNamedPackageFamily(string name)
    {
        // The suffix after the separator identifies a specific member of the pack family.
        var match = Regex.Match(name,
            @"^(.{3,100}?\s+(?:voice|sound|language|font|theme)\s+(?:package|pack|bundle|bank))\s+[-–—|]\s+(\S.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string GetFamilyKey(string id, HashSet<string> packageIds)
    {
        int separator = id.IndexOf('.');
        while (separator >= 0 && (separator = id.IndexOf('.', separator + 1)) >= 0)
        {
            string prefix = id[..separator];
            if (FamilyNames.ContainsKey(prefix) || (packageIds.Contains(prefix) && IsVariantSuffix(id[(separator + 1)..])))
            {
                return prefix;
            }
        }

        return id;
    }

    private static bool IsVariantSuffix(string suffix) =>
        suffix.Split('.').All(part => VariantSuffixes.Contains(part) ||
            (part.Length > 0 && part.All(char.IsAsciiDigit)));

    public override string ToString() => Name;
}
