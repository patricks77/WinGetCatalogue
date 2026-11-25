# Classification maintenance

## Source of truth

WinGet remains the primary source for package identity, manifest version, tags and installation. `CatalogCategory` and `CatalogSubcategory` classify the index using these tags and local heuristics. `Other software` is a valid outcome when the available evidence is insufficient.

`Data/classifications-proposed.json` contains suggestions that apply to the matching manifest revision. `Data/classifications-reviewed.json` contains manually reviewed corrections that apply to the package ID across revisions. For those corrections, the hash records the version reviewed. WinGet-based classification takes precedence, followed by the reviewed correction and then the matching suggestion. Each entry has four camel-case fields (`id`, `manifestHash`, `category` and `subcategory`).

The snapshots are embedded at build time. End users need neither AI access nor a local model; refreshing WinGet still discovers new packages, but a new or changed package may remain unclassified until a future release updates the snapshots.

## Private review workflow

The maintainer's optional local classifier is kept outside this project and outside the distributed application. It obtains public package metadata, generates candidate classifications using the maintainer's local model and saves candidates in a private SQLite database. Model output is a suggestion, not a verified classification. The database retains the source manifest hash and description for review, and can resume interrupted runs. A missing description remains unclassified.

1. Refresh the WinGet index and generate or resume candidate suggestions locally.
2. Users may save or copy a category report from the package details page and share it with maintainers. Saved reports stay on the user's device; the application does not send them automatically. Configure a real project issue destination when one exists.
3. Check reported problems against WinGet metadata and, when necessary, a reliable upstream source. Choose the corrected category and subcategory for approved corrections.
4. Run `export` to generate both JSON snapshots from the same review database. Rejected packages are excluded. Inspect the diffs, rebuild and test before publishing. Repeat after WinGet updates.

The current private tool lives under the Windows user profile in `AppData\Local\WinGetCatalogue\Curator`. Its `summary`, `list [offset]`, `approve <id> <category> [subcategory]`, `reject <id>` and `export` commands support the review cycle. `list` returns up to 50 pending records at a time. Invoking it without arguments starts or resumes generation; a numeric argument limits the number of packages for a trial. The current local connection configuration is intentionally machine-specific and is not a prerequisite for building or running the app.

For example, from PowerShell on the maintainer's machine:

```powershell
$curator = "$env:LOCALAPPDATA\WinGetCatalogue\Curator\Curator.csproj"
dotnet run --project $curator -- list
dotnet run --project $curator -- list 50
dotnet run --project $curator -- summary
dotnet run --project $curator -- approve Example.Package securite protection
dotnet run --project $curator -- reject Example.OtherPackage
dotnet run --project $curator -- export
```

Use real package IDs and category keys from the application's rules. The first run migrates existing approvals and rejections to a separate database table. Generating a new proposal updates the manifest and wording while preserving the review decision. Rebuild after both files export successfully.

Keep access tokens and certificates out of source control, logs, builds and issue reports. Do not install a private certificate as a system-wide trusted root just for this workflow.

## Checks before release

- Build the solution and launch the packaged application.
- Test category and subcategory filtering, search and a few package details.
- Check that a reviewed correction survives a manifest update, that stale proposals and rejected entries are excluded, and that WinGet-based classifications take precedence.
- Save a category report from a Release build and verify that the JSON opens.
- Check generated install commands without executing installations.
- Confirm that both classification snapshots contain only intended package identifiers, hashes and category keys, and that no private model connection configuration is packaged.
