using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

// Coordinates catalogue navigation, background metadata, installations, and reporting.
namespace WinGetCatalogue;

public sealed partial class MainPage : Page
{
    private static readonly Uri AppInstallerStoreUri = new("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1");
    private static readonly Uri AppInstallerHelpUri = new("https://learn.microsoft.com/windows/msix/app-installer/install-update-app-installer");
    private const string WinGetSetupMessage = "Install or update Microsoft App Installer to use WinGet for installations and updates.";
    private readonly CatalogSource _source = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromHours(1) };
    private readonly WinGetDetails _details = new();
    private readonly SemaphoreSlim _iconRequests = new(4);
    private int _cardDiagnosticCount;
    private readonly HashSet<string> _installations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WinGetInstallationFailure> _installationFailures = new(StringComparer.OrdinalIgnoreCase);
    private SoftwareCatalog? _catalog;
    private CatalogCategory _category = CatalogCategory.All;
    private CatalogSubcategory _subcategory = CatalogSubcategory.All;
    private CatalogEntry? _openEntry;
    private CatalogApplication? _openApplication;
    private CatalogEntry? _chosenPackage;
    private WinGetInstallationFailure? _requestedReport;
    private WinGetIssueDraft? _reportDraft;
    private bool _refreshing;
    private CancellationTokenSource? _backgroundFill;
    private InstalledSoftware[]? _installed;
    private string? _installedWarning;
    private InstalledSoftware? _selectedInstalled;
    private Task? _installedRefreshTask;
    private bool _installedRefreshPending;
    private bool _installedNeedsRefresh;
    private bool _upgradeRunning;
    private Task<bool>? _winGetCheckTask;
    private bool _winGetAvailable;
    private bool _openingAppInstaller;
    private bool _checkingAfterInstaller;

    public MainPage()
    {
        InitializeComponent();

        CategoryNavigation.MenuItems.Add(new NavigationViewItem
        {
            Content = "Installed software",
            Tag = "installed",
            Icon = new SymbolIcon(Symbol.Download),
        });
        foreach (var category in CatalogCategory.Categories)
        {
            CategoryNavigation.MenuItems.Add(new NavigationViewItem
            {
                Content = category.Name,
                Tag = category,
                Icon = new FontIcon { Glyph = category.Glyph, FontSize = 16 },
            });
        }

        Loaded += MainPage_Loaded;
        CategoryNavigation.PaneOpened += (_, _) => AdjustInstalledMargin();
        CategoryNavigation.PaneClosed += (_, _) => AdjustInstalledMargin();
        Unloaded += (_, _) =>
        {
            _refreshTimer.Stop();
            _backgroundFill?.Cancel();
        };
        _refreshTimer.Tick += async (_, _) => await RefreshCatalogAsync();
    }

    private async void MainPage_Loaded(object sender, RoutedEventArgs args)
    {
        _ = RefreshWinGetAvailabilityAsync();
        CategoryNavigation.SelectedItem ??= CategoryNavigation.MenuItems[1];
        CategoryHeading.Text = _category.Name;

        if (_catalog is null)
        {
            _catalog = await CatalogSource.ReadCacheAsync();
            UpdateResults();
            StartBackgroundFill();
        }

        await RefreshCatalogAsync();
        if (IsLoaded)
        {
            _refreshTimer.Start();
        }
    }

    // Share the probe between startup, the installed view, and setup retries.
    private Task<bool> RefreshWinGetAvailabilityAsync() =>
        _winGetCheckTask is { IsCompleted: false } activeCheck
            ? activeCheck
            : _winGetCheckTask = CheckWinGetAvailabilityAsync();

    private async Task<bool> CheckWinGetAvailabilityAsync()
    {
        CheckWinGet.IsEnabled = DetailCheckWinGet.IsEnabled = false;
        try
        {
            bool available = await WinGetCommand.CheckAvailabilityAsync();
            _winGetAvailable = available;
            if (available) _checkingAfterInstaller = false;

            WinGetSetup.Message = DetailWinGetSetup.Message = WinGetSetupMessage;
            WinGetSetup.IsOpen = DetailWinGetSetup.IsOpen = !available;
            if (_openEntry is { } entry)
                UpdatePackageInstallButton(entry);
            if (_selectedInstalled is { } installed)
                UpdateInstalledUpgradeButton(installed);
            return available;
        }
        finally
        {
            CheckWinGet.IsEnabled = DetailCheckWinGet.IsEnabled = true;
        }
    }

    private async void CheckWinGet_Click(object sender, RoutedEventArgs args) => await RecheckWinGetAfterSetupAsync();

    // Returning from the Store triggers another probe for the current Windows user.
    internal Task OnWindowActivatedAsync() => _checkingAfterInstaller && !_openingAppInstaller
        ? RecheckWinGetAfterSetupAsync()
        : Task.CompletedTask;

    private async Task RecheckWinGetAfterSetupAsync()
    {
        if (await RefreshWinGetAvailabilityAsync() && InstalledView.Visibility == Visibility.Visible)
            await RefreshInstalledAsync(afterChange: true);
    }

    private async void OpenAppInstaller_Click(object sender, RoutedEventArgs args)
    {
        if (_openingAppInstaller) return;
        _openingAppInstaller = true;
        OpenAppInstaller.IsEnabled = DetailOpenAppInstaller.IsEnabled = false;
        try
        {
            bool opened = await TryOpenAppInstallerUriAsync(AppInstallerStoreUri);
            if (!opened)
                opened = await TryOpenAppInstallerUriAsync(AppInstallerHelpUri);
            _checkingAfterInstaller = opened;
            WinGetSetup.Message = DetailWinGetSetup.Message = opened
                ? "After installing or updating App Installer, return here and select Check again if needed."
                : "Could not open App Installer. Open Microsoft Store and search for Microsoft App Installer.";
        }
        finally
        {
            _openingAppInstaller = false;
            OpenAppInstaller.IsEnabled = DetailOpenAppInstaller.IsEnabled = true;
        }
    }

    private static async Task<bool> TryOpenAppInstallerUriAsync(Uri uri)
    {
        try
        {
            bool opened = await Windows.System.Launcher.LaunchUriAsync(uri);
            DiagnosticLog.Write($"App Installer setup: {uri.Scheme} launcher returned {opened}");
            return opened;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write($"App Installer setup: {uri.Scheme} launcher failed", exception);
            return false;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs args) => await RefreshCatalogAsync();

    // Keep the cached catalogue usable while a remote refresh is in progress.
    private async Task RefreshCatalogAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        RefreshButton.IsEnabled = false;
        RefreshProgress.IsActive = true;
        RefreshProgress.Visibility = Visibility.Visible;
        CatalogScope.Text = _catalog is null
            ? "Loading the WinGet community source…"
            : $"{GetCatalogScope(_catalog)} · Refreshing…";

        try
        {
            var snapshot = await _source.RefreshAsync(_catalog);
            if (!ReferenceEquals(_catalog, snapshot))
            {
                _catalog = snapshot;
                UpdateResults();
                StartBackgroundFill();
            }

            CatalogScope.Text = GetCatalogScope(snapshot);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or HttpRequestException
            or OperationCanceledException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Debug.WriteLine(exception);
            CatalogScope.Text = _catalog is null
                ? "Catalogue unavailable. Check your connection and select Refresh."
                : $"{GetCatalogScope(_catalog)} · Refresh unavailable. Showing the local copy.";
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
            RefreshProgress.IsActive = false;
            RefreshProgress.Visibility = Visibility.Collapsed;
        }
    }

    private static string GetCatalogScope(SoftwareCatalog catalog) =>
        $"WinGet community source · {FormatCount(catalog.Entries.Length)} packages · index from {catalog.PublishedAt.ToLocalTime().ToString("d MMMM yyyy, HH:mm", SoftwareCatalog.Culture)}";

    private static string FormatCount(int count) => count.ToString("N0", SoftwareCatalog.Culture);

    private void StartBackgroundFill()
    {
        if (_catalog is null) return;
        _backgroundFill?.Cancel();
        var cancellation = new CancellationTokenSource();
        _backgroundFill = cancellation;
        var entries = _catalog.Entries;
        _ = Task.Run(async () =>
        {
            try { await _details.FillInBackgroundAsync(entries, cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        });
    }

    private void CategoryNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && tag == "installed")
        {
            CatalogView.Visibility = Visibility.Collapsed;
            InstalledView.Visibility = Visibility.Visible;
            AdjustInstalledMargin();
            if (_installed is null || _installedNeedsRefresh)
            {
                _ = RefreshInstalledAsync();
            }
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: CatalogCategory category })
        {
            CatalogView.Visibility = Visibility.Visible;
            InstalledView.Visibility = Visibility.Collapsed;
            _category = category;
            _subcategory = CatalogSubcategory.All;
            var subcategories = CatalogSubcategory.For(category.Key);
            SubcategoryPicker.ItemsSource = subcategories;
            SubcategoryPicker.Visibility = subcategories.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            SubcategoryPicker.SelectedItem = subcategories.FirstOrDefault();
            UpdateResults();
        }
    }

    private void AdjustInstalledMargin()
    {
        InstalledView.Margin = new Thickness(CategoryNavigation.IsPaneOpen ? 250 : 72, 20, 24, 20);
    }

    private async void InstalledRefresh_Click(object sender, RoutedEventArgs args) => await RefreshInstalledAsync();

    private Task RefreshInstalledAsync(bool afterChange = false)
    {
        if (_installedRefreshTask is { IsCompleted: false } activeRefresh)
        {
            // A running read may have started before WinGet finished changing packages.
            _installedRefreshPending |= afterChange;
            return activeRefresh;
        }
        return _installedRefreshTask = ReadInstalledInventoryAsync();
    }

    private async Task ReadInstalledInventoryAsync()
    {
        InstalledRefresh.IsEnabled = false;
        try
        {
            do
            {
                _installedRefreshPending = false;
                InstalledStatus.Text = "Checking installed software and updates…";
                if (!_winGetAvailable && !await RefreshWinGetAvailabilityAsync())
                {
                    _installedNeedsRefresh = true;
                    InstalledStatus.Text = "WinGet is needed to read installed software and available updates.";
                    break;
                }
                try
                {
                    var snapshot = await InstalledSoftwareReader.ReadAsync();
                    _installed = snapshot.Packages;
                    _installedWarning = snapshot.Warning;
                    _installedNeedsRefresh = false;
                    UpdateInstalledResults();
                    UpdateOpenInstalledDetails();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(exception);
                    _installedNeedsRefresh = true;
                    InstalledStatus.Text = await RefreshWinGetAvailabilityAsync()
                        ? $"Unable to read installed software: {exception.Message}"
                        : "WinGet is needed to read installed software and available updates.";
                    if (_selectedInstalled is not null)
                    {
                        InstalledDetailUpdate.Text = "Update status could not be refreshed.";
                        InstalledDialog.IsPrimaryButtonEnabled = false;
                    }
                }
            } while (_installedRefreshPending);
        }
        finally
        {
            InstalledRefresh.IsEnabled = true;
        }
    }

    private void InstalledSearch_TextChanged(object sender, TextChangedEventArgs args) => UpdateInstalledResults();
    private void InstalledFilter_Changed(object sender, RoutedEventArgs args) => UpdateInstalledResults();

    private void UpdateInstalledResults()
    {
        if (_installed is null) return;
        string query = InstalledSearch.Text.Trim();
        var results = _installed.Where(item =>
            (UpdatesOnly.IsChecked != true || item.UpdateAvailable) &&
            (item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             item.Id.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        InstalledList.ItemsSource = results;
        InstalledStatus.Text = $"{FormatCount(results.Length)} shown · {FormatCount(_installed.Length)} installed · {FormatCount(_installed.Count(item => item.UpdateAvailable))} updates available";
        if (_installedWarning is not null) InstalledStatus.Text += $"\nSource unavailable: {_installedWarning}";
    }

    private async void InstalledList_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not InstalledSoftware item || _selectedInstalled is not null) return;
        _selectedInstalled = item;
        UpdateInstalledDetails(item);
        InstalledDetailStatus.IsOpen = false;
        InstalledDialog.XamlRoot = XamlRoot;
        try { await InstalledDialog.ShowAsync(); }
        finally { _selectedInstalled = null; }
    }

    private void UpdateInstalledDetails(InstalledSoftware item)
    {
        InstalledDetailName.Text = item.Name;
        InstalledDetailId.Text = item.Id;
        InstalledDetailVersion.Text = item.InstalledLabel;
        InstalledDetailUpdate.Text = item.UpdateLabel;
        UpdateInstalledUpgradeButton(item);
    }

    private void UpdateInstalledUpgradeButton(InstalledSoftware? item) =>
        InstalledDialog.IsPrimaryButtonEnabled = item?.UpdateAvailable == true &&
            _winGetAvailable && !_upgradeRunning && !_installedNeedsRefresh;

    private void UpdateOpenInstalledDetails()
    {
        if (_selectedInstalled is not { } selected || _installed is null) return;

        var matches = _installed.Where(item => item.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase) &&
            item.Source.Equals(selected.Source, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Prefer the newly installed version after an upgrade, then the originally selected version.
        var current = matches.FirstOrDefault(item => item.InstalledVersion == selected.AvailableVersion)
            ?? matches.FirstOrDefault(item => item.InstalledVersion == selected.InstalledVersion)
            ?? matches.FirstOrDefault();
        if (current is not null)
        {
            _selectedInstalled = current;
            UpdateInstalledDetails(current);
            return;
        }

        _selectedInstalled = selected with { AvailableVersion = null, UpdateAvailable = false };
        InstalledDetailVersion.Text = "Installed version could not be confirmed.";
        InstalledDetailUpdate.Text = "Update status unavailable.";
        InstalledDialog.IsPrimaryButtonEnabled = false;
    }

    private async void InstalledDialog_UpgradeClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_selectedInstalled is not { UpdateAvailable: true } item || _upgradeRunning || !_winGetAvailable) return;
        _upgradeRunning = true;
        InstalledDialog.IsPrimaryButtonEnabled = false;
        try
        {
            using var process = WinGetCommand.StartUpgrade(item.Id, item.Source);
            InstalledDetailStatus.Severity = InfoBarSeverity.Informational;
            InstalledDetailStatus.Message = "Follow the update in the WinGet window.";
            InstalledDetailStatus.IsOpen = true;
            await process.WaitForExitAsync();
            if (_selectedInstalled == item)
            {
                InstalledDetailStatus.Severity = process.ExitCode == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
                InstalledDetailStatus.Message = process.ExitCode == 0
                    ? "WinGet finished the update."
                    : WinGetCommand.GetFailureMessage(process.ExitCode);
            }
            await RefreshInstalledAsync(afterChange: true);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            bool available = await RefreshWinGetAvailabilityAsync();
            if (_selectedInstalled == item)
            {
                InstalledDetailStatus.Severity = InfoBarSeverity.Error;
                InstalledDetailStatus.Message = available ? exception.Message
                    : "WinGet is unavailable. Close this dialog to open App Installer.";
                InstalledDetailStatus.IsOpen = true;
            }
        }
        finally
        {
            _upgradeRunning = false;
            UpdateInstalledUpgradeButton(_selectedInstalled);
        }
    }

    private void SubcategoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SubcategoryPicker.SelectedItem is CatalogSubcategory subcategory && subcategory != _subcategory)
        {
            _subcategory = subcategory;
            UpdateResults();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs args) => UpdateResults();

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_openApplication is null && SearchBox.Focus(FocusState.Keyboard))
        {
            SearchBox.SelectAll();
            args.Handled = true;
        }
    }

    private void UpdateResults()
    {
        if (_catalog is null)
        {
            return;
        }

        CategoryHeading.Text = _category.Name;
        var results = _catalog.Search(_category.Key, SearchBox.Text, _subcategory.Key);
        SoftwareGrid.ItemsSource = results;
        SoftwareGrid.Visibility = results.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = results.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (results.Length > 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(SoftwareGrid.ItemsSource, results))
                {
                    ResizeGrid(SoftwareGrid.ActualWidth);
                    SoftwareGrid.ScrollIntoView(results[0], ScrollIntoViewAlignment.Leading);
                }
            });
        }

        string count = results.Length == 1 ? "1 application" : $"{FormatCount(results.Length)} applications";
        if (ResultCount.Text != count)
        {
            ResultCount.Text = count;
            if (FrameworkElementAutomationPeer.FromElement(ResultCount) is AutomationPeer peer)
            {
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }
    }

    private void SoftwareGrid_SizeChanged(object sender, SizeChangedEventArgs args) => ResizeGrid(args.NewSize.Width);

    private void ResizeGrid(double width)
    {
        if (width <= 0 || SoftwareGrid.ItemsPanelRoot is not ItemsWrapGrid panel)
        {
            return;
        }

        double available = Math.Max(width - 4, 1);
        int columns = Math.Max(1, (int)(available / 232));
        panel.ItemWidth = Math.Floor(available / columns);
    }

    // Grid containers are recycled, so cancel work belonging to their previous item.
    private void SoftwareGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.Tag is CancellationTokenSource pending)
        {
            pending.Cancel();
            args.ItemContainer.Tag = null;
        }

        if (args.ItemContainer.ContentTemplateRoot is FrameworkElement card)
        {
            ((Image)card.FindName("CardIcon")).Source = null;
            ((FontIcon)card.FindName("CardGlyph")).Visibility = Visibility.Visible;
            ((TextBlock)card.FindName("CardDescription")).Text = "";
        }

        if (!args.InRecycleQueue)
        {
            args.RegisterUpdateCallback(LoadCardIcon);
            args.Handled = true;
        }
    }

    // Limit concurrent metadata requests and update only the current card.
    private async void LoadCardIcon(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not CatalogApplication application || args.ItemContainer.ContentTemplateRoot is not FrameworkElement card)
        {
            return;
        }

        var container = args.ItemContainer;
        var representative = application.Representative;
        bool trace = Interlocked.Increment(ref _cardDiagnosticCount) <= 30;
        if (trace) DiagnosticLog.Write($"Card loading started: {representative.Id}");
        ((TextBlock)card.FindName("CardName")).Text = application.Name;
        ((TextBlock)card.FindName("CardDescription")).Text = "";
        using var request = new CancellationTokenSource();
        container.Tag = request;
        try
        {
            await _iconRequests.WaitAsync(request.Token);
            try
            {
                var result = await Task.Run(() => _details.GetCachedAsync(representative, request.Token), request.Token);
                if (trace) DiagnosticLog.Write($"Card metadata loaded: {representative.Id}, description={result.Details.Description is not null}, icon={result.Icon is not null}");
                request.Token.ThrowIfCancellationRequested();
                ((TextBlock)card.FindName("CardDescription")).Text =
                    result.Details.Description ?? "";
                if (application.Packages.Length == 1 && ContainsHan(application.Name) &&
                    !string.IsNullOrWhiteSpace(result.Details.Name) && !ContainsHan(result.Details.Name))
                    ((TextBlock)card.FindName("CardName")).Text = result.Details.Name;
                await SetIconAsync(result.Icon, (Image)card.FindName("CardIcon"),
                    (FontIcon)card.FindName("CardGlyph"), request.Token);
            }
            finally
            {
                _iconRequests.Release();
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write($"Card loading failed: {representative.Id}", exception);
        }
        finally
        {
            if (ReferenceEquals(container.Tag, request))
            {
                container.Tag = null;
            }
        }
    }

    private async void SoftwareGrid_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (_openApplication is not null || args.ClickedItem is not CatalogApplication application)
        {
            return;
        }

        _openApplication = application;
        try
        {
            if (application.Packages.Length == 1)
            {
                await ShowPackageAsync(application.Primary, canReturnToFamily: false);
                return;
            }

            FamilyDialog.Title = application.Name;
            FamilyDialog.XamlRoot = XamlRoot;
            FamilySearchBox.Text = "";
            UpdateFamilyPackages();

            while (true)
            {
                _chosenPackage = null;
                await FamilyDialog.ShowAsync();
                if (_chosenPackage is not { } package ||
                    await ShowPackageAsync(package, canReturnToFamily: true) != ContentDialogResult.Secondary)
                {
                    break;
                }
            }
        }
        finally
        {
            _openApplication = null;
            _chosenPackage = null;
            FamilyPackages.ItemsSource = null;
        }
    }

    private void FamilySearchBox_TextChanged(object sender, TextChangedEventArgs args) => UpdateFamilyPackages();

    private void UpdateFamilyPackages()
    {
        if (_openApplication is not { } application)
        {
            return;
        }

        var search = new CatalogQuery(FamilySearchBox.Text);
        var preferred = application.MatchingPackage;
        var results = application.Packages.Select(package => (Package: package, Score: search.Score(package)))
            .Where(match => match.Score >= 0)
            .OrderByDescending(match => match.Score)
            .ThenByDescending(match => match.Package == preferred)
            .Select(match => match.Package).ToArray();
        FamilyPackages.ItemsSource = results;
        bool showMatch = search.IsEmpty && preferred is not null;
        FamilyMatchingPackage.Visibility = showMatch ? Visibility.Visible : Visibility.Collapsed;
        if (showMatch)
            FamilyMatchingPackage.Text = $"Matches your search or filter: {preferred!.Name} ({preferred.Id})";
        FamilyPackageCount.Text = results.Length switch
        {
            0 => "No packages found",
            1 => "1 WinGet package",
            _ => $"{FormatCount(results.Length)} WinGet packages",
        };
        FamilyPackages.Visibility = results.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FamilyPackages_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is CatalogEntry package)
        {
            _chosenPackage = package;
            FamilyDialog.Hide();
        }
    }

    private async Task<ContentDialogResult> ShowPackageAsync(CatalogEntry entry, bool canReturnToFamily)
    {
        while (true)
        {
            _requestedReport = null;
            var result = await ShowPackageDetailAsync(entry, canReturnToFamily);
            if (_requestedReport is not { } failure)
            {
                return result;
            }

            await ShowReportAsync(failure);
        }
    }

    private async Task<ContentDialogResult> ShowPackageDetailAsync(CatalogEntry entry, bool canReturnToFamily)
    {
        SoftwareDialog.SecondaryButtonText = canReturnToFamily ? "Back to packages" : "";
        _openEntry = entry;
        SoftwareDialog.Title = entry.Name;
        DetailCategory.Text = entry.Classification;
        CategoryReportExpander.IsExpanded = false;
        CategoryReportSuggestion.ItemsSource = CatalogCategory.Categories.Where(category => category.Key.Length > 0).ToArray();
        CategoryReportSuggestion.SelectedItem = null;
        CategoryReportComment.Text = "";
        CategoryReportFeedback.Text = "";
        DetailGlyph.Glyph = entry.CategoryInfo.Glyph;
        DetailGlyph.Visibility = Visibility.Visible;
        DetailIcon.Source = null;
        DetailId.Text = entry.Id;
        DetailPublisher.Text = entry.Publisher ?? "Loading publisher…";
        DetailDescription.Text = "Loading description…";
        bool validId = WinGetCommand.IsValidId(entry.Id);
        DetailCommand.Text = validId ? WinGetCommand.GetCommand(entry.Id) : "Command unavailable for this ID.";
        CopyCommandButton.IsEnabled = validId;
        DetailStatus.IsOpen = false;
        ReportFailureButton.Visibility = Visibility.Collapsed;
        if (_installationFailures.TryGetValue(entry.Id, out var previousFailure))
        {
            ShowInstallationFailure(previousFailure.ExitCode);
        }

        UpdatePackageInstallButton(entry);
        SoftwareDialog.XamlRoot = XamlRoot;

        using var closed = new CancellationTokenSource();
        try
        {
            var shown = SoftwareDialog.ShowAsync();
            _ = LoadDetailsAsync(entry, closed.Token);
            return await shown;
        }
        finally
        {
            closed.Cancel();
            _openEntry = null;
        }
    }

    private void UpdatePackageInstallButton(CatalogEntry entry) =>
        SoftwareDialog.IsPrimaryButtonEnabled = _winGetAvailable &&
            WinGetCommand.IsValidId(entry.Id) && !_installations.Contains(entry.Id);

    // Keep original names unless WinGet supplies a non-Chinese localized name.
    private static bool ContainsHan(string value) => value.Any(ch => (ch >= '\u3400' && ch <= '\u9fff') || (ch >= '\uf900' && ch <= '\ufaff'));

    // Load details after opening the dialog; closing it cancels outstanding work.
    private async Task LoadDetailsAsync(CatalogEntry entry, CancellationToken cancellationToken)
    {
        PackageDetails? details = null;
        bool unavailable = false;
        try
        {
            details = await Task.Run(() => _details.GetMetadataAsync(entry, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write($"Package details unavailable: {entry.Id}", exception);
            unavailable = true;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        string? alias = entry.PublisherAliases.FirstOrDefault();
        DetailPublisher.Text = details?.Publisher ?? entry.Publisher ??
            (alias is null ? "Publisher unavailable." : $"{alias} (indexed name)");
        if (ContainsHan(entry.Name) && !string.IsNullOrWhiteSpace(details?.Name) && !ContainsHan(details.Name))
            SoftwareDialog.Title = details.Name;
        DetailDescription.Text = details?.Description ?? entry.Description ??
            (unavailable ? "Description temporarily unavailable." : "No description available.");

        if (details?.Icon is null) return;
        try
        {
            // Updating the icon is optional and never delays the persisted description.
            var media = await Task.Run(() => _details.GetCachedAsync(entry, cancellationToken), cancellationToken);
            await SetIconAsync(media.Icon, DetailIcon, DetailGlyph, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            DiagnosticLog.Write($"Package details icon unavailable: {entry.Id}", exception);
        }
    }

    private static async Task SetIconAsync(byte[]? bytes, Image image, FontIcon glyph, CancellationToken cancellationToken)
    {
        if (bytes is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(bytes);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);

        var source = new BitmapImage { DecodePixelWidth = 96 };
        await source.SetSourceAsync(stream);
        cancellationToken.ThrowIfCancellationRequested();
        image.Source = source;
        glyph.Visibility = Visibility.Collapsed;
    }

    // Collect enough context to reproduce a classification error without submitting it automatically.
    private CategoryIssueReport? CreateCategoryReport()
    {
        if (_openEntry is not { } entry || CategoryReportSuggestion.SelectedItem is not CatalogCategory suggestion)
        {
            CategoryReportFeedback.Text = "Choose a suggested category first.";
            return null;
        }
        return CategoryIssueReport.Create(entry, suggestion, CategoryReportComment.Text);
    }

    private async void CategoryReport_SaveClick(object sender, RoutedEventArgs args)
    {
        if (CreateCategoryReport() is not { } report) return;
        try
        {
            string path = await report.SaveAsync();
            CategoryReportFeedback.Text = $"Report saved locally: {path}. Share it with the maintainers.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(exception);
            CategoryReportFeedback.Text = "Unable to save the report. You can copy it instead.";
        }
    }

    private void CategoryReport_CopyClick(object sender, RoutedEventArgs args)
    {
        if (CreateCategoryReport() is not { } report) return;
        try
        {
            CopyToClipboard(report.ToReportText());
            CategoryReportFeedback.Text = "Report copied. Share it with the maintainers.";
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x800401D0))
        {
            Debug.WriteLine(exception);
            CategoryReportFeedback.Text = "Clipboard busy. Please try again.";
        }
    }

    private async void CategoryReport_OpenFolderClick(object sender, RoutedEventArgs args)
    {
        try
        {
            Directory.CreateDirectory(CategoryIssueReport.ReportsDirectory);
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(CategoryIssueReport.ReportsDirectory);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder))
                CategoryReportFeedback.Text = "Unable to open the reports folder.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine(exception);
            CategoryReportFeedback.Text = "Unable to open the reports folder.";
        }
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            CopyToClipboard(DetailCommand.Text);
            ShowDetailStatus("Command copied", "You can paste it into PowerShell.", InfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x800401D0))
        {
            ShowDetailStatus("Clipboard busy", "Try again or select the command to copy it.", InfoBarSeverity.Warning);
        }
    }

    private async void SoftwareDialog_InstallClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (!_winGetAvailable || _openEntry is not { } entry || !_installations.Add(entry.Id))
        {
            return;
        }

        SoftwareDialog.IsPrimaryButtonEnabled = false;
        _installationFailures.Remove(entry.Id);
        ReportFailureButton.Visibility = Visibility.Collapsed;
        try
        {
            DateTime startedUtc = DateTime.UtcNow;
            using var process = WinGetCommand.StartInstallation(entry.Id);
            int processId = process.Id;
            ShowDetailStatus("WinGet started", "Follow the installation in the command window.", InfoBarSeverity.Informational);
            await process.WaitForExitAsync();
            if (WinGetIssueReport.IsReportable(process.ExitCode))
            {
                _installationFailures[entry.Id] = new WinGetInstallationFailure(entry, process.ExitCode, processId, startedUtc);
            }

            if (_openEntry?.Id == entry.Id)
            {
                if (process.ExitCode == 0)
                {
                    ShowDetailStatus("WinGet finished", "WinGet reported that the operation succeeded.", InfoBarSeverity.Success);
                }
                else
                {
                    ShowInstallationFailure(process.ExitCode);
                }
            }

            if (process.ExitCode == 0)
            {
                _installedNeedsRefresh = true;
                if (_installed is not null || InstalledView.Visibility == Visibility.Visible)
                    _ = RefreshInstalledAsync(afterChange: true);
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or Win32Exception or InvalidOperationException)
        {
            bool available = await RefreshWinGetAvailabilityAsync();
            if (available && _openEntry?.Id == entry.Id)
            {
                ShowDetailStatus("Unable to start WinGet", exception.Message, InfoBarSeverity.Error);
            }
        }
        finally
        {
            _installations.Remove(entry.Id);
            if (_openEntry?.Id == entry.Id)
            {
                UpdatePackageInstallButton(entry);
            }
        }
    }

    private void ShowInstallationFailure(int exitCode)
    {
        ReportFailureButton.Visibility = WinGetIssueReport.IsReportable(exitCode) ? Visibility.Visible : Visibility.Collapsed;
        ShowDetailStatus("WinGet exited with an error", WinGetCommand.GetFailureMessage(exitCode), InfoBarSeverity.Warning);
    }

    private void ReportFailure_Click(object sender, RoutedEventArgs args)
    {
        if (_openEntry is { } entry && _installationFailures.TryGetValue(entry.Id, out var failure))
        {
            _requestedReport = failure;
            SoftwareDialog.Hide();
        }
    }

    private async Task ShowReportAsync(WinGetInstallationFailure failure)
    {
        var draft = await Task.Run(() => WinGetIssueReport.Prepare(failure));
        _reportDraft = draft;
        ReportTitle.Text = draft.Title;
        ReportFeedback.Text = "";
        ReportFieldPicker.ItemsSource = draft.Fields.Select(field => field.Name).ToArray();
        ReportFieldPicker.SelectedIndex = 1;
        ReportDialog.XamlRoot = XamlRoot;
        try
        {
            await ReportDialog.ShowAsync();
        }
        finally
        {
            _reportDraft = null;
        }
    }

    private void ReportFieldPicker_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        int index = ReportFieldPicker.SelectedIndex;
        ReportFieldValue.Text = _reportDraft is { } draft && index >= 0 ? draft.Fields[index].Value : "";
        ReportFeedback.Text = "";
    }

    private void ReportField_CopyClick(object sender, RoutedEventArgs args)
    {
        try
        {
            CopyToClipboard(ReportFieldValue.Text);
            ReportFeedback.Text = "Field copied.";
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x800401D0))
        {
            ReportFeedback.Text = "Clipboard busy. Try again or select the text to copy it.";
        }
    }

    private async void ReportDialog_OpenGitHub(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_reportDraft is not { } draft)
        {
            return;
        }

        ReportDialog.IsPrimaryButtonEnabled = false;
        try
        {
            CopyToClipboard(draft.GetClipboardText());
            bool opened = await Windows.System.Launcher.LaunchUriAsync(draft.FormUri);
            ReportFeedback.Text = opened
                ? "Report copied. The title is filled in on the GitHub form."
                : "Report copied. Windows could not open the GitHub form.";
        }
        catch (Exception exception) when (exception.HResult == unchecked((int)0x800401D0))
        {
            ReportFeedback.Text = "Clipboard busy. Try again.";
        }
        catch (Exception exception)
        {
            Debug.WriteLine(exception);
            ReportFeedback.Text = "Unable to open the GitHub form.";
        }
        finally
        {
            ReportDialog.IsPrimaryButtonEnabled = true;
        }
    }

    private static void CopyToClipboard(string text)
    {
        var content = new DataPackage();
        content.SetText(text);
        Clipboard.SetContent(content);
        Clipboard.Flush();
    }

    private void ShowDetailStatus(string title, string message, InfoBarSeverity severity)
    {
        DetailStatus.Title = title;
        DetailStatus.Message = message;
        DetailStatus.Severity = severity;
        DetailStatus.IsOpen = true;
    }
}
