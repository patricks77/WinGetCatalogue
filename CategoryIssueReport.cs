using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinGetCatalogue;

// A report is local until the user explicitly shares it with the maintainers.
internal sealed record CategoryIssueReport(
    string PackageId, string PackageName, string PackageVersion, string ManifestHash,
    string CurrentCategory, string CurrentSubcategory, string SuggestedCategory,
    string SuggestedCategoryName, string Details, DateTimeOffset CreatedUtc)
{
    internal static string ReportsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGetCatalogue", "category-reports");

    internal static CategoryIssueReport Create(CatalogEntry entry, CatalogCategory suggestion, string details) =>
        new(entry.Id, entry.Name, entry.Version, entry.ManifestHash, entry.Category, entry.Subcategory,
            suggestion.Key, suggestion.Name, details.Trim(), DateTimeOffset.UtcNow);

    internal string ToReportText() =>
        $"Incorrect category: {PackageName}\n" +
        $"WinGet ID: {PackageId}\nVersion: {PackageVersion}\nManifest hash: {ManifestHash}\n" +
        $"Current category: {CatalogCategory.Find(CurrentCategory).Name}" +
        (CurrentSubcategory == "autres" ? "" : $" / {CatalogSubcategory.Find(CurrentCategory, CurrentSubcategory).Name}") +
        $"\nSuggested category: {SuggestedCategoryName}\nDetails: {Details}";

    internal async Task<string> SaveAsync()
    {
        Directory.CreateDirectory(ReportsDirectory);
        string path = Path.Combine(ReportsDirectory, $"category-{CreatedUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        // The Release package disables reflection-based JSON serialization.
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this, CategoryReportJsonContext.Default.CategoryIssueReport));
        return path;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CategoryIssueReport))]
internal sealed partial class CategoryReportJsonContext : JsonSerializerContext;
