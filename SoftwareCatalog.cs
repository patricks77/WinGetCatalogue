using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// Defines searchable catalogue entries and applies optional curated overrides.
namespace WinGetCatalogue;

public sealed record CatalogEntry(string Id, string Name, string? Publisher, string[] PublisherAliases,
    string Category, string Subcategory, string? Description, string Version, string ManifestHash, string? Moniker = null)
{
    [JsonIgnore]
    public CatalogCategory CategoryInfo => CatalogCategory.Find(Category);

    [JsonIgnore]
    public string Classification => Subcategory == "autres"
        ? CategoryInfo.Name
        : $"{CategoryInfo.Name} · {CatalogSubcategory.Find(Category, Subcategory).Name}";

    public override string ToString() => Name;
}

internal sealed record SoftwareCatalog(DateTimeOffset PublishedAt, string? ETag, CatalogEntry[] Entries)
{
    internal static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-US");
    private CatalogApplication[]? _applications;

    internal CatalogApplication[] Search(string category, string query, string subcategory)
    {
        var search = new CatalogQuery(query);
        _applications ??= CatalogApplication.Group(Entries);
        if (search.IsEmpty && category.Length == 0 && subcategory.Length == 0) return _applications;

        // Choose the best eligible member while keeping all family members accessible.
        var matches = new List<(CatalogApplication Application, int Score)>();
        foreach (var application in _applications)
        {
            CatalogEntry? best = null;
            int bestScore = -1;
            foreach (var entry in application.Packages)
            {
                if (category.Length > 0 && entry.Category != category ||
                    subcategory.Length > 0 && entry.Subcategory != subcategory) continue;

                int score = search.Score(entry);
                if (score > bestScore || score >= 0 && score == bestScore && entry == application.Primary)
                {
                    best = entry;
                    bestScore = score;
                }
            }

            if (best is not null) matches.Add((application.WithMatch(best), bestScore));
        }
        return matches.OrderByDescending(match => match.Score).Select(match => match.Application).ToArray();
    }

    internal static Dictionary<string, CatalogOverride> LoadOverrides()
    {
        using var stream = typeof(SoftwareCatalog).Assembly.GetManifestResourceStream("WinGetCatalogue.Data.fiches-fr.json")
            ?? throw new InvalidDataException("The catalogue overrides could not be found.");
        var file = JsonSerializer.Deserialize(stream, CatalogJsonContext.Default.CatalogOverrides)
            ?? throw new InvalidDataException("The catalogue overrides could not be read.");
        return file.Entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
    }

    internal static CatalogClassificationSnapshots LoadClassifications() =>
        new(ReadClassifications("classifications-proposed.json"), ReadClassifications("classifications-reviewed.json"));

    private static Dictionary<string, CatalogClassification> ReadClassifications(string name)
    {
        var result = new Dictionary<string, CatalogClassification>(StringComparer.OrdinalIgnoreCase);
        using var stream = typeof(SoftwareCatalog).Assembly.GetManifestResourceStream($"WinGetCatalogue.Data.{name}");
        if (stream is null) return result;
        foreach (var entry in JsonSerializer.Deserialize(stream, CatalogJsonContext.Default.CatalogClassificationArray) ?? [])
            if (!string.IsNullOrWhiteSpace(entry.Id) && IsValidClassification(entry.Category, entry.Subcategory))
                result[entry.Id] = entry;
        return result;
    }

    internal static bool IsValidClassification(string category, string subcategory) =>
        !string.IsNullOrEmpty(category) && category != "autres" && !string.IsNullOrEmpty(subcategory) &&
        CatalogCategory.Categories.Any(item => item.Key == category) &&
        CatalogSubcategory.For(category).Any(item => item.Key == subcategory);

    internal static (string Category, string Subcategory) ApplyClassification(
        string id, string hash, string category, string subcategory,
        CatalogClassificationSnapshots snapshots)
    {
        // WinGet-based rules stay authoritative. Human decisions apply to the package identity,
        // while a suggestion can be tied to a description from a particular manifest revision.
        if (category != "autres") return (category, subcategory);
        if (snapshots.Reviewed.TryGetValue(id, out var correction))
            return (correction.Category, correction.Subcategory);
        if (snapshots.Proposed.TryGetValue(id, out var suggestion) &&
            string.Equals(hash, suggestion.ManifestHash, StringComparison.OrdinalIgnoreCase))
            return (suggestion.Category, suggestion.Subcategory);
        return (category, subcategory);
    }
}

internal sealed class CatalogQuery
{
    private const CompareOptions TextOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private readonly string _query;
    private readonly string _compactQuery;
    private readonly string[] _terms;
    private readonly string[] _publisherTerms;

    internal CatalogQuery(string query)
    {
        _query = query.Trim();
        _terms = _query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _compactQuery = string.Concat(_terms);
        _publisherTerms = _terms.Select(NormalizePublisher).ToArray();
    }

    internal bool IsEmpty => _terms.Length == 0;

    internal bool Matches(CatalogEntry entry) => Score(entry) >= 0;

    internal int Score(CatalogEntry entry)
    {
        if (IsEmpty) return 0;
        if (entry.Id.Equals(_query, StringComparison.OrdinalIgnoreCase)) return 100;
        if (SoftwareCatalog.Culture.CompareInfo.Compare(entry.Name, _query, TextOptions) == 0) return 95;
        // WinGet monikers are compact aliases. Joining query words makes "VS Code" match "vscode".
        if (!string.IsNullOrWhiteSpace(entry.Moniker) && entry.Moniker.Equals(_compactQuery, StringComparison.OrdinalIgnoreCase)) return 90;

        int score = 0;
        for (int i = 0; i < _terms.Length; i++)
        {
            string term = _terms[i];
            string publisherTerm = _publisherTerms[i];
            if (SoftwareCatalog.Culture.CompareInfo.IsPrefix(entry.Name, term, TextOptions)) score += 8;
            else if (Contains(entry.Name, term) || Contains(entry.Moniker, term)) score += 4;
            else if (Contains(entry.Id, term)) score += 2;
            else if (Contains(entry.Publisher, term) || publisherTerm.Length > 0 &&
                entry.PublisherAliases.Any(alias => alias.Contains(publisherTerm, StringComparison.Ordinal))) score++;
            else return -1;
        }

        return score / _terms.Length;
    }

    private static string NormalizePublisher(string value)
    {
        var normalized = new StringBuilder();
        foreach (char character in value.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(character))
            {
                normalized.Append(char.ToLowerInvariant(character));
            }
        }

        return normalized.ToString();
    }

    private static bool Contains(string? value, string term) => value is not null &&
        SoftwareCatalog.Culture.CompareInfo.IndexOf(value, term, TextOptions) >= 0;
}

internal sealed record CatalogOverrides(CatalogOverride[] Entries);
internal sealed record CatalogOverride(string Id, string Name, string Publisher, string Category, string? Description);
internal sealed record CatalogClassification(string Id, string ManifestHash, string Category, string Subcategory);
internal sealed record CatalogClassificationSnapshots(
    IReadOnlyDictionary<string, CatalogClassification> Proposed,
    IReadOnlyDictionary<string, CatalogClassification> Reviewed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SoftwareCatalog))]
[JsonSerializable(typeof(CatalogOverrides))]
[JsonSerializable(typeof(CatalogClassification[]))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;
