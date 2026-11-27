# Build the GitHub MSI

Run these commands from the repository root on Windows with the development tools listed in the project README. Use a fresh output directory so files from an older build cannot become part of the installer.

```powershell
$version = "0.1.4"
$releaseDir = Join-Path ([IO.Path]::GetTempPath()) "WinGetCatalogue-$([Guid]::NewGuid())"
$appDir = Join-Path $releaseDir "app"
$setupDir = Join-Path $releaseDir "setup"

dotnet publish WinGetCatalogue.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:WindowsPackageType=None -p:AppxPackage=false -p:WindowsAppSDKSelfContained=true -p:EnableWinAppRunSupport=false "-p:Version=$version" --output $appDir
if ($LASTEXITCODE -ne 0) { throw "Publishing the application failed." }

dotnet build packaging/WinGetCatalogue.wixproj -c Release -t:Rebuild "-p:ReleaseVersion=$version" "-p:AppPublishDir=$appDir" --output $setupDir
if ($LASTEXITCODE -ne 0) { throw "Building the MSI failed." }

Get-FileHash (Join-Path $setupDir "WinGetCatalogue-$version-win-x64.msi") -Algorithm SHA256
```

The WiX project restores its compiler through NuGet. This build produces an unsigned x64 MSI. The regular MSIX configuration is defined separately in the existing application project.

Before publishing a new version, install the MSI on a test machine, launch the application, check catalogue search and the installed-software view, and verify uninstallation. Tag the source commit that produced the installer so GitHub's source ZIP matches the release.
