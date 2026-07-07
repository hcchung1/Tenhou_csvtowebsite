using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using System.Diagnostics;
using System.Data;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using TenhouCsvReader.Services;

namespace TenhouCsvReader;

public partial class MainWindow : Window
{
    private enum SidePaneViewMode
    {
        Web = 0,
        TrainingPlot = 1
    }

    private sealed class SourceSession
    {
        public required string SourcePath { get; init; }
        public required string CsvPath { get; init; }
        public required string DisplayPath { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string? ExtractedRootPath { get; init; }
        public string? CsvEntryInArchive { get; init; }
        public IReadOnlyList<string> TrainingImagePaths { get; init; } = Array.Empty<string>();
        public int SelectedImageIndex { get; set; }
        public string ActiveFilterText { get; set; } = string.Empty;
        public int CurrentPageIndex { get; set; }
    }

    private sealed class TrainingImageItem
    {
        public required string Name { get; init; }
        public required string Path { get; init; }

        public override string ToString() => Name;
    }

    private readonly Style _defaultCellTextStyle;
    private readonly Style _tenhouLinkCellTextStyle;

    private LargeCsvPagedReader? _reader;
    private CsvFilteredPagedReader? _filteredReader;
    private CancellationTokenSource? _pageLoadCts;
    private CancellationTokenSource? _backgroundIndexCts;

    private IReadOnlyList<string> _displayHeaders = Array.Empty<string>();
    private string? _activeFilePath;
    private string _activeFilterText = string.Empty;
    private string? _currentEmbeddedUrl;
    private int _currentPageIndex;
    private const double BrowserPaneMinWidth = 460;
    private const double BrowserPaneDefaultWidth = 560;
    private const double BrowserPaneMinGridWidth = 460;
    private double _lastBrowserPaneWidth = BrowserPaneDefaultWidth;
    private readonly string _uiStateFilePath;
    private readonly string _sessionTempRootPath;
    private readonly List<SourceSession> _sessions = new();
    private SourceSession? _activeSession;
    private SidePaneViewMode _sidePaneViewMode = SidePaneViewMode.Web;
    private bool _isSwitchingSession;
    private bool _isUpdatingTrainingImageSelection;
    private bool _isBusy;
    private bool _isBrowserPaneOpen;
    private bool _hasPreviousPage;
    private bool _hasNextPage;
    private double _trainingImageZoom = 1.0;
    private const double TrainingImageZoomMin = 0.2;
    private const double TrainingImageZoomMax = 5.0;
    private const double TrainingImageZoomStep = 0.1;

    public MainWindow()
    {
        InitializeComponent();

        _uiStateFilePath = BuildUiStateFilePath();
        _sessionTempRootPath = BuildSessionTempRootPath();
        LoadUiState();

        TryApplyWindowIcon();
        ConfigureSideBrowserUserDataFolder();

        _defaultCellTextStyle = BuildCellTextStyle(showToolTip: false, isLinkStyle: false);
        _tenhouLinkCellTextStyle = BuildCellTextStyle(showToolTip: true, isLinkStyle: true);

        ResetTrainingImageZoom();
        ApplyControlState();
        UpdateBrowserControlsState();
        UpdateTrainingImagePanelForActiveSession();
        _ = EnsureSideBrowserInitializedAsync();
    }

    private void ConfigureSideBrowserUserDataFolder()
    {
        try
        {
            if (SideBrowser.CreationProperties is not null)
            {
                return;
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return;
            }

            var userDataFolder = Path.Combine(localAppData, "TenhouCsvReader", "WebView2");
            Directory.CreateDirectory(userDataFolder);

            SideBrowser.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder
            };
        }
        catch
        {
            // Fallback to default behavior if user data path setup fails.
        }
    }

    private void TryApplyWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (!File.Exists(iconPath))
            {
                return;
            }

            var decoder = new IconBitmapDecoder(
                new Uri(iconPath, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var bestFrame = decoder.Frames
                .OrderByDescending(frame => frame.PixelWidth * frame.PixelHeight)
                .FirstOrDefault();

            if (bestFrame is not null)
            {
                Icon = bestFrame;
            }
        }
        catch
        {
            // Keep startup resilient even if icon loading fails.
        }
    }

    protected override async void OnClosed(EventArgs e)
    {
        SaveUiState();
        await DisposeAllSessionsAsync();
        base.OnClosed(e);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedInputPaths(e.Data, out var droppedPaths) && droppedPaths.Count > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_isBusy)
        {
            IndexStatusTextBlock.Text = "Please wait for the current operation to finish.";
            e.Handled = true;
            return;
        }

        if (!TryGetDroppedInputPaths(e.Data, out var inputPaths) || inputPaths.Count == 0)
        {
            IndexStatusTextBlock.Text = "Drop one or more .csv or .zip files to open.";
            e.Handled = true;
            return;
        }

        await OpenInputSourcesAsync(inputPaths);
        e.Handled = true;
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isBrowserPaneOpen)
        {
            return;
        }

        EnsureBrowserPaneLayout(useDefaultWhenCollapsed: false);
    }

    private void BrowserSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_isBrowserPaneOpen)
        {
            return;
        }

        EnsureBrowserPaneLayout(useDefaultWhenCollapsed: false);
        SaveUiState();
    }

    private async void OpenCsvButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await OpenInputSourcesAsync(dialog.FileNames);
    }

    private async void OpenZipButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ZIP archives (*.zip)|*.zip|All supported files (*.zip;*.csv)|*.zip;*.csv|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await OpenInputSourcesAsync(dialog.FileNames);
    }

    private async void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        await CloseCurrentTabAsync();
    }

    public async Task OpenCsvFromLaunchAsync(string filePath)
    {
        if (!TryValidateInputPath(filePath, out var normalizedPath, out var errorMessage))
        {
            IndexStatusTextBlock.Text = $"Open startup file failed: {errorMessage}";
            return;
        }

        await OpenInputSourcesAsync([normalizedPath]);
    }

    private async void SessionTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSwitchingSession || !IsLoaded)
        {
            return;
        }

        if (SessionTabControl.SelectedItem is not TabItem selectedTab || selectedTab.Tag is not SourceSession session)
        {
            return;
        }

        await OpenSessionAsync(session, preserveState: true);
    }

    private void TrainingImageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingTrainingImageSelection || _activeSession is null)
        {
            return;
        }

        if (TrainingImageComboBox.SelectedItem is not TrainingImageItem selectedImage)
        {
            return;
        }

        _activeSession.SelectedImageIndex = TrainingImageComboBox.SelectedIndex;
        ShowTrainingImage(selectedImage.Path);
    }

    private void TrainingImageZoomOutButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustTrainingImageZoom(-TrainingImageZoomStep);
    }

    private void TrainingImageZoomInButton_Click(object sender, RoutedEventArgs e)
    {
        AdjustTrainingImageZoom(TrainingImageZoomStep);
    }

    private void TrainingImageZoomResetButton_Click(object sender, RoutedEventArgs e)
    {
        ResetTrainingImageZoom();
    }

    private void TrainingImageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        var delta = e.Delta > 0 ? TrainingImageZoomStep : -TrainingImageZoomStep;
        AdjustTrainingImageZoom(delta);
        e.Handled = true;
    }

    private async Task OpenInputSourcesAsync(IEnumerable<string> rawPaths)
    {
        var normalizedPaths = new List<string>();
        string? lastValidationError = null;

        foreach (var rawPath in rawPaths)
        {
            if (TryValidateInputPath(rawPath, out var normalizedPath, out var errorMessage))
            {
                normalizedPaths.Add(normalizedPath);
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                lastValidationError = errorMessage;
            }
        }

        if (normalizedPaths.Count == 0)
        {
            IndexStatusTextBlock.Text = lastValidationError is null
                ? "No supported files selected."
                : $"Open failed: {lastValidationError}";
            return;
        }

        foreach (var normalizedPath in normalizedPaths)
        {
            var existingSession = _sessions.FirstOrDefault(session =>
                string.Equals(session.SourcePath, normalizedPath, StringComparison.OrdinalIgnoreCase));

            if (existingSession is not null)
            {
                SelectSessionTab(existingSession);
                await OpenSessionAsync(existingSession, preserveState: true);
                continue;
            }

            SourceSession newSession;

            try
            {
                newSession = await CreateSourceSessionAsync(normalizedPath);
            }
            catch (Exception ex)
            {
                IndexStatusTextBlock.Text = $"Open failed ({Path.GetFileName(normalizedPath)}): {ex.Message}";
                continue;
            }

            _sessions.Add(newSession);
            AddSessionTab(newSession);
            SelectSessionTab(newSession);
            await OpenSessionAsync(newSession, preserveState: true, forceReload: true);
        }

        ApplyControlState();
    }

    private async Task OpenSessionAsync(SourceSession session, bool preserveState, bool forceReload = false)
    {
        if (preserveState)
        {
            PersistActiveSessionState();
        }

        if (!forceReload && ReferenceEquals(_activeSession, session) && _reader is not null)
        {
            UpdateTrainingImagePanelForActiveSession();
            ApplyControlState();
            return;
        }

        _activeSession = session;
        UpdateTrainingImagePanelForActiveSession();

        await OpenCsvAsync(
            filePath: session.CsvPath,
            displayPathOverride: session.DisplayPath,
            restoreFilterText: session.ActiveFilterText,
            restorePageIndex: session.CurrentPageIndex);

        ApplyControlState();
    }

    private async Task CloseCurrentTabAsync()
    {
        if (SessionTabControl.SelectedItem is not TabItem selectedTab || selectedTab.Tag is not SourceSession selectedSession)
        {
            IndexStatusTextBlock.Text = "No tab to close.";
            return;
        }

        PersistActiveSessionState();

        var wasActiveSession = ReferenceEquals(selectedSession, _activeSession);

        _isSwitchingSession = true;
        SessionTabControl.Items.Remove(selectedTab);
        _isSwitchingSession = false;

        _sessions.Remove(selectedSession);

        if (wasActiveSession)
        {
            await DisposeReaderAsync();
            _activeSession = null;
        }

        CleanupSessionTempFiles(selectedSession);

        if (SessionTabControl.Items.Count == 0)
        {
            await ResetToEmptyStateAsync();
            ApplyControlState();
            return;
        }

        var nextTab = SessionTabControl.SelectedItem as TabItem ?? (TabItem)SessionTabControl.Items[0];
        if (nextTab.Tag is not SourceSession nextSession)
        {
            await ResetToEmptyStateAsync();
            ApplyControlState();
            return;
        }

        SelectSessionTab(nextSession);
        await OpenSessionAsync(nextSession, preserveState: false, forceReload: true);
    }

    private async Task<SourceSession> CreateSourceSessionAsync(string normalizedPath)
    {
        var extension = Path.GetExtension(normalizedPath);

        if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceSession
            {
                SourcePath = normalizedPath,
                CsvPath = normalizedPath,
                DisplayPath = normalizedPath,
                DisplayName = BuildSessionDisplayName(normalizedPath, isArchive: false)
            };
        }

        if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only .csv and .zip are supported.");
        }

        IndexStatusTextBlock.Text = $"Extracting archive: {Path.GetFileName(normalizedPath)}";

        var sessionFolderName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var sessionRootPath = Path.Combine(_sessionTempRootPath, sessionFolderName);
        Directory.CreateDirectory(sessionRootPath);

        try
        {
            using var archive = ZipFile.OpenRead(normalizedPath);
            var csvEntries = archive.Entries
                .Where(entry => IsArchiveFileEntry(entry) &&
                                string.Equals(Path.GetExtension(entry.Name), ".csv", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.Name.Contains("pred", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(entry => entry.Length)
                .ThenBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (csvEntries.Count == 0)
            {
                throw new InvalidDataException("No CSV file found in zip.");
            }

            var csvEntry = csvEntries[0];
            var extractedCsvPath = await ExtractArchiveEntryAsync(csvEntry, sessionRootPath, "csv");

            var pngEntries = archive.Entries
                .Where(entry => IsArchiveFileEntry(entry) &&
                                string.Equals(Path.GetExtension(entry.Name), ".png", StringComparison.OrdinalIgnoreCase))
                .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var extractedPngPaths = new List<string>(pngEntries.Count);

            foreach (var pngEntry in pngEntries)
            {
                var extractedPngPath = await ExtractArchiveEntryAsync(pngEntry, sessionRootPath, "images");
                extractedPngPaths.Add(extractedPngPath);
            }

            var displayPath = $"{normalizedPath} | CSV: {csvEntry.FullName}";

            return new SourceSession
            {
                SourcePath = normalizedPath,
                CsvPath = extractedCsvPath,
                DisplayPath = displayPath,
                DisplayName = BuildSessionDisplayName(normalizedPath, isArchive: true),
                ExtractedRootPath = sessionRootPath,
                CsvEntryInArchive = csvEntry.FullName,
                TrainingImagePaths = extractedPngPaths
            };
        }
        catch
        {
            CleanupDirectoryIfExists(sessionRootPath);
            throw;
        }
    }

    private static bool IsArchiveFileEntry(ZipArchiveEntry entry)
    {
        return !string.IsNullOrWhiteSpace(entry.Name);
    }

    private static async Task<string> ExtractArchiveEntryAsync(ZipArchiveEntry entry, string sessionRootPath, string categoryFolder)
    {
        var categoryRootPath = Path.GetFullPath(Path.Combine(sessionRootPath, categoryFolder));
        Directory.CreateDirectory(categoryRootPath);

        var relativePath = entry.FullName
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("Archive entry path is empty.");
        }

        var extractedPath = Path.GetFullPath(Path.Combine(categoryRootPath, relativePath));
        var normalizedCategoryRootPath = categoryRootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!extractedPath.StartsWith(normalizedCategoryRootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Archive contains unsafe path: {entry.FullName}");
        }

        var destinationFolderPath = Path.GetDirectoryName(extractedPath);
        if (!string.IsNullOrWhiteSpace(destinationFolderPath))
        {
            Directory.CreateDirectory(destinationFolderPath);
        }

        await using var outputStream = new FileStream(
            path: extractedPath,
            mode: FileMode.Create,
            access: FileAccess.Write,
            share: FileShare.None,
            bufferSize: 64 * 1024,
            useAsync: true);

        await using var inputStream = entry.Open();
        await inputStream.CopyToAsync(outputStream);

        return extractedPath;
    }

    private void AddSessionTab(SourceSession session)
    {
        var tabItem = new TabItem
        {
            Header = session.DisplayName,
            Tag = session,
            ToolTip = session.DisplayPath
        };

        SessionTabControl.Items.Add(tabItem);
    }

    private void SelectSessionTab(SourceSession session)
    {
        var tab = SessionTabControl.Items
            .OfType<TabItem>()
            .FirstOrDefault(item => ReferenceEquals(item.Tag, session));

        if (tab is null)
        {
            return;
        }

        _isSwitchingSession = true;
        SessionTabControl.SelectedItem = tab;
        _isSwitchingSession = false;
    }

    private void PersistActiveSessionState()
    {
        if (_activeSession is null)
        {
            return;
        }

        _activeSession.ActiveFilterText = _activeFilterText;
        _activeSession.CurrentPageIndex = _currentPageIndex;

        if (TrainingImageComboBox.SelectedIndex >= 0)
        {
            _activeSession.SelectedImageIndex = TrainingImageComboBox.SelectedIndex;
        }
    }

    private async Task ResetToEmptyStateAsync()
    {
        await DisposeReaderAsync();
        _activeSession = null;
        _activeFilePath = null;
        _activeFilterText = string.Empty;
        _currentPageIndex = 0;
        _displayHeaders = Array.Empty<string>();
        _hasPreviousPage = false;
        _hasNextPage = false;

        CsvDataGrid.ItemsSource = null;
        FilePathTextBlock.Text = "No file loaded.";
        FilterTextBox.Text = string.Empty;
        GoToPageTextBox.Text = "1";
        PageInfoTextBlock.Text = string.Empty;
        IndexStatusTextBlock.Text = "No file loaded.";
        UpdateTrainingImagePanelForActiveSession();
    }

    private void ResetTrainingImageZoom()
    {
        SetTrainingImageZoom(1.0);
    }

    private void AdjustTrainingImageZoom(double delta)
    {
        SetTrainingImageZoom(_trainingImageZoom + delta);
    }

    private void SetTrainingImageZoom(double newZoom)
    {
        _trainingImageZoom = Math.Clamp(newZoom, TrainingImageZoomMin, TrainingImageZoomMax);
        ApplyTrainingImageZoom();
    }

    private void ApplyTrainingImageZoom()
    {
        TrainingImageScaleTransform.ScaleX = _trainingImageZoom;
        TrainingImageScaleTransform.ScaleY = _trainingImageZoom;
        TrainingImageZoomTextBlock.Text = $"{_trainingImageZoom * 100:0}%";

        var hasImage = TrainingImageViewer.Source is not null;
        TrainingImageZoomOutButton.IsEnabled = hasImage && !_isBusy && _trainingImageZoom > TrainingImageZoomMin + 0.0001;
        TrainingImageZoomInButton.IsEnabled = hasImage && !_isBusy && _trainingImageZoom < TrainingImageZoomMax - 0.0001;
        TrainingImageZoomResetButton.IsEnabled = hasImage && !_isBusy && Math.Abs(_trainingImageZoom - 1.0) > 0.0001;
    }

    private void UpdateTrainingImagePanelForActiveSession()
    {
        if (_activeSession is null || _activeSession.TrainingImagePaths.Count == 0)
        {
            _isUpdatingTrainingImageSelection = true;
            TrainingImageComboBox.ItemsSource = null;
            TrainingImageComboBox.SelectedIndex = -1;
            _isUpdatingTrainingImageSelection = false;
            TrainingImageStatusTextBlock.Text = string.Empty;
            TrainingImageViewer.Source = null;
            ResetTrainingImageZoom();
            SideTrainingPlotPane.Visibility = Visibility.Collapsed;

            if (_sidePaneViewMode == SidePaneViewMode.TrainingPlot)
            {
                SetSidePaneMode(SidePaneViewMode.Web, ensurePaneVisible: false);
            }

            ApplyControlState();
            return;
        }

        var imageItems = _activeSession.TrainingImagePaths
            .Select(path => new TrainingImageItem
            {
                Name = Path.GetFileName(path),
                Path = path
            })
            .ToList();

        _isUpdatingTrainingImageSelection = true;
        TrainingImageComboBox.ItemsSource = imageItems;
        if (imageItems.Count == 0)
        {
            TrainingImageComboBox.SelectedIndex = -1;
            TrainingImageStatusTextBlock.Text = "No PNG image found.";
            TrainingImageViewer.Source = null;
            ResetTrainingImageZoom();
            _isUpdatingTrainingImageSelection = false;
            return;
        }

        var selectedIndex = Math.Clamp(_activeSession.SelectedImageIndex, 0, imageItems.Count - 1);
        _activeSession.SelectedImageIndex = selectedIndex;
        TrainingImageComboBox.SelectedIndex = selectedIndex;
        _isUpdatingTrainingImageSelection = false;

        ShowTrainingImage(imageItems[selectedIndex].Path);

        if (!_isBrowserPaneOpen)
        {
            SetSidePaneMode(SidePaneViewMode.TrainingPlot, ensurePaneVisible: true);
        }
        else
        {
            if (_sidePaneViewMode == SidePaneViewMode.TrainingPlot)
            {
                SideTrainingPlotPane.Visibility = Visibility.Visible;
            }

            ApplyControlState();
        }
    }

    private void ShowTrainingImage(string imagePath)
    {
        if (!File.Exists(imagePath))
        {
            TrainingImageViewer.Source = null;
            TrainingImageStatusTextBlock.Text = "Image file not found.";
            ResetTrainingImageZoom();
            return;
        }

        try
        {
            using var stream = new FileStream(
                path: imagePath,
                mode: FileMode.Open,
                access: FileAccess.Read,
                share: FileShare.ReadWrite | FileShare.Delete);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            TrainingImageViewer.Source = image;
            TrainingImageStatusTextBlock.Text = Path.GetFileName(imagePath);
            ResetTrainingImageZoom();
        }
        catch (Exception ex)
        {
            TrainingImageViewer.Source = null;
            TrainingImageStatusTextBlock.Text = $"Load image failed: {ex.Message}";
            ResetTrainingImageZoom();
        }
    }

    private static string BuildSessionDisplayName(string path, bool isArchive)
    {
        var fileName = Path.GetFileName(path);
        return isArchive ? $"ZIP: {fileName}" : $"CSV: {fileName}";
    }

    private void CleanupSessionTempFiles(SourceSession session)
    {
        if (string.IsNullOrWhiteSpace(session.ExtractedRootPath))
        {
            return;
        }

        CleanupDirectoryIfExists(session.ExtractedRootPath);
    }

    private static void CleanupDirectoryIfExists(string directoryPath)
    {
        try
        {
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
        catch
        {
            // Ignore best-effort cleanup failures.
        }
    }

    private async void PreviousPageButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadPageAsync(_currentPageIndex - 1);
    }

    private async void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadPageAsync(_currentPageIndex + 1);
    }

    private async void GoPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(GoToPageTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetPageNumber) ||
            targetPageNumber <= 0)
        {
            IndexStatusTextBlock.Text = "Invalid page number.";
            return;
        }

        await LoadPageAsync(targetPageNumber - 1);
    }

    private async void PageSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _activeSession is null)
        {
            return;
        }

        await OpenSessionAsync(_activeSession, preserveState: true, forceReload: true);
    }

    private async void ApplyFilterButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyFilterAsync();
    }

    private async void ClearFilterButton_Click(object sender, RoutedEventArgs e)
    {
        await ClearFilterAsync(reloadData: true);
    }

    private async void FilterTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await ApplyFilterAsync();
    }

    private void BackgroundIndexCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (BackgroundIndexCheckBox.IsChecked == true)
        {
            StartBackgroundIndexing();
        }
        else
        {
            _backgroundIndexCts?.Cancel();
            _backgroundIndexCts?.Dispose();
            _backgroundIndexCts = null;
            RefreshIndexStatus();
        }
    }

    private void CsvDataGrid_AutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var isTenhouLinkColumn = string.Equals(e.PropertyName, "tenhou_link", StringComparison.OrdinalIgnoreCase);

        if (e.Column is DataGridTextColumn textColumn)
        {
            textColumn.ElementStyle = isTenhouLinkColumn ? _tenhouLinkCellTextStyle : _defaultCellTextStyle;
        }

        e.Column.MinWidth = 80;

        if (isTenhouLinkColumn)
        {
            // Keep this very long column stable and clipped so clicking it won't drag view to far right.
            e.Column.Width = new DataGridLength(360);
            e.Column.MaxWidth = 420;
            return;
        }

        e.Column.Width = DataGridLength.SizeToHeader;
    }

    private void CsvDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var source = e.OriginalSource as DependencyObject;
            if (source is null)
            {
                return;
            }

            var cell = FindVisualParent<DataGridCell>(source);
            if (cell is null || cell.DataContext is null)
            {
                return;
            }

            CsvDataGrid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
            CsvDataGrid.SelectedItem = cell.DataContext;
            cell.Focus();
        }
        catch
        {
            // Guard against visual tree edge cases from right-click source elements.
        }
    }

    private void CsvDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsCtrlPressed())
        {
            return;
        }

        if (!TryGetLinkFromSource(e.OriginalSource, out var link))
        {
            return;
        }

        if (!TryOpenUrl(link, out var error))
        {
            IndexStatusTextBlock.Text = $"Open link failed: {error}";
            return;
        }

        IndexStatusTextBlock.Text = "Opened link in browser.";
        e.Handled = true;
    }

    private void CsvDataGrid_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsCtrlPressed())
        {
            return;
        }

        if (!TryGetLinkFromSource(e.OriginalSource, out var link))
        {
            return;
        }

        OpenLinkInSidePane(link);
        e.Handled = true;
    }

    private void CsvDataGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (TryGetLinkFromSource(e.OriginalSource, out _) && IsCtrlPressed())
        {
            CsvDataGrid.Cursor = Cursors.Hand;
            return;
        }

        CsvDataGrid.Cursor = Cursors.Arrow;
    }

    private void CsvDataGrid_MouseLeave(object sender, MouseEventArgs e)
    {
        CsvDataGrid.Cursor = Cursors.Arrow;
    }

    private async Task EnsureSideBrowserInitializedAsync()
    {
        try
        {
            if (SideBrowser.CoreWebView2 is not null)
            {
                return;
            }

            await SideBrowser.EnsureCoreWebView2Async();
            ConfigureSideBrowserCore(SideBrowser.CoreWebView2);
        }
        catch
        {
            // Keep application usable even if WebView2 runtime is unavailable.
        }
    }

    private void SideBrowser_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            IndexStatusTextBlock.Text = $"WebView2 initialization failed: {e.InitializationException.Message}";
            UpdateBrowserControlsState();
            return;
        }

        ConfigureSideBrowserCore(SideBrowser.CoreWebView2);
        UpdateBrowserControlsState();
    }

    private void ConfigureSideBrowserCore(CoreWebView2? core)
    {
        if (core is null)
        {
            return;
        }

        core.NewWindowRequested -= SideBrowser_NewWindowRequested;
        core.NewWindowRequested += SideBrowser_NewWindowRequested;

        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsZoomControlEnabled = true;
    }

    private void SideBrowser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Uri))
        {
            return;
        }

        e.Handled = true;
        NavigateSideBrowser(e.Uri);
    }

    private void BrowserBackButton_Click(object sender, RoutedEventArgs e)
    {
        var core = SideBrowser.CoreWebView2;
        if (!_isBrowserPaneOpen || _sidePaneViewMode != SidePaneViewMode.Web || core is null || !core.CanGoBack)
        {
            return;
        }

        core.GoBack();
        UpdateBrowserControlsState();
    }

    private void BrowserForwardButton_Click(object sender, RoutedEventArgs e)
    {
        var core = SideBrowser.CoreWebView2;
        if (!_isBrowserPaneOpen || _sidePaneViewMode != SidePaneViewMode.Web || core is null || !core.CanGoForward)
        {
            return;
        }

        core.GoForward();
        UpdateBrowserControlsState();
    }

    private void BrowserRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var core = SideBrowser.CoreWebView2;
        if (!_isBrowserPaneOpen || _sidePaneViewMode != SidePaneViewMode.Web || core is null)
        {
            return;
        }

        core.Reload();
        UpdateBrowserControlsState();
    }

    private void BrowserPaneToggleViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sidePaneViewMode == SidePaneViewMode.Web)
        {
            if (_activeSession is null || _activeSession.TrainingImagePaths.Count == 0)
            {
                IndexStatusTextBlock.Text = "No training PNG available for this tab.";
                return;
            }

            SetSidePaneMode(SidePaneViewMode.TrainingPlot, ensurePaneVisible: true);
            return;
        }

        SetSidePaneMode(SidePaneViewMode.Web, ensurePaneVisible: true);
    }

    private void BrowserAddressTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        if (_sidePaneViewMode != SidePaneViewMode.Web)
        {
            e.Handled = true;
            return;
        }

        NavigateSideBrowser(BrowserAddressTextBox.Text);
        e.Handled = true;
    }

    private void BrowserOpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBrowserPaneOpen || _sidePaneViewMode != SidePaneViewMode.Web || string.IsNullOrWhiteSpace(_currentEmbeddedUrl))
        {
            return;
        }

        if (!TryOpenUrl(_currentEmbeddedUrl, out var error))
        {
            IndexStatusTextBlock.Text = $"Open external failed: {error}";
            return;
        }

        IndexStatusTextBlock.Text = "Opened current side link in browser.";
    }

    private void BrowserCloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideBrowserPane();
    }

    private void SideBrowser_SourceChanged(object sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (SideBrowser.Source is not null)
        {
            _currentEmbeddedUrl = SideBrowser.Source.AbsoluteUri;
            BrowserAddressTextBox.Text = _currentEmbeddedUrl;
        }

        UpdateBrowserControlsState();
    }

    private void SideBrowser_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            IndexStatusTextBlock.Text = $"Navigation failed: {e.WebErrorStatus}";
        }

        UpdateBrowserControlsState();
    }

    private void CsvDataGridContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        CopyCellMenuItem.IsEnabled = TryGetCurrentCellValue(out _);
        CopyRowCsvMenuItem.IsEnabled = HasSelectedRow();
    }

    private void CopyCellMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryGetCurrentCellValue(out var cellValue))
            {
                IndexStatusTextBlock.Text = "No cell selected to copy.";
                return;
            }

            if (!TrySetClipboardText(cellValue))
            {
                IndexStatusTextBlock.Text = "Copy failed: clipboard is currently busy.";
                return;
            }

            IndexStatusTextBlock.Text = "Copied cell value.";
        }
        catch (Exception ex)
        {
            IndexStatusTextBlock.Text = $"Copy cell failed: {ex.Message}";
        }
    }

    private void CopyRowCsvMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!TryBuildSelectedRowsCsv(out var csvText))
            {
                IndexStatusTextBlock.Text = "No row selected to copy.";
                return;
            }

            if (!TrySetClipboardText(csvText))
            {
                IndexStatusTextBlock.Text = "Copy failed: clipboard is currently busy.";
                return;
            }

            IndexStatusTextBlock.Text = "Copied row as CSV.";
        }
        catch (Exception ex)
        {
            IndexStatusTextBlock.Text = $"Copy row failed: {ex.Message}";
        }
    }

    private void CsvDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (!TryBuildSelectedRowsCsv(out var csvText))
        {
            return;
        }

        if (!TrySetClipboardText(csvText))
        {
            IndexStatusTextBlock.Text = "Copy failed: clipboard is currently busy.";
            return;
        }

        IndexStatusTextBlock.Text = "Copied row as CSV.";
        e.Handled = true;
    }

    private async Task OpenCsvAsync(
        string filePath,
        string? displayPathOverride = null,
        string? restoreFilterText = null,
        int restorePageIndex = 0)
    {
        await DisposeReaderAsync();

        try
        {
            SetBusy(true);
            IndexStatusTextBlock.Text = "Loading CSV metadata...";

            _reader = new LargeCsvPagedReader(filePath, GetSelectedPageSize());
            await _reader.InitializeAsync();

            _activeFilePath = filePath;
            _displayHeaders = BuildUniqueColumnNames(_reader.Headers);
            _activeFilterText = string.Empty;
            _currentPageIndex = 0;

            var displayPath = string.IsNullOrWhiteSpace(displayPathOverride) ? filePath : displayPathOverride;
            FilePathTextBlock.Text = displayPath;
            FilterTextBox.Text = string.Empty;
            GoToPageTextBox.Text = "1";
            PageInfoTextBlock.Text = "Loading page 1...";
            CsvDataGrid.ItemsSource = null;

            if (!string.IsNullOrWhiteSpace(restoreFilterText))
            {
                FilterTextBox.Text = restoreFilterText;
                var filterRestored = await TryApplyFilterAsync(restoreFilterText, loadFirstPage: false);
                if (!filterRestored)
                {
                    FilterTextBox.Text = string.Empty;
                }
            }

            await LoadPageAsync(Math.Max(0, restorePageIndex));
            StartBackgroundIndexing();
        }
        catch (Exception ex)
        {
            await DisposeReaderAsync();
            FilePathTextBlock.Text = "No file loaded.";
            IndexStatusTextBlock.Text = $"Open failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyFilterAsync()
    {
        if (_reader is null)
        {
            IndexStatusTextBlock.Text = "Open a CSV file first.";
            return;
        }

        var filterText = FilterTextBox.Text.Trim();
        var success = await TryApplyFilterAsync(filterText, loadFirstPage: true);
        if (!success)
        {
            return;
        }

        StartBackgroundIndexing();
    }

    private async Task<bool> TryApplyFilterAsync(string filterText, bool loadFirstPage)
    {
        if (_reader is null)
        {
            IndexStatusTextBlock.Text = "Open a CSV file first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(filterText))
        {
            IndexStatusTextBlock.Text = "Filter text is empty.";
            return false;
        }

        try
        {
            var conditions = CsvFilterParser.Parse(filterText, _displayHeaders);
            if (conditions.Count == 0)
            {
                IndexStatusTextBlock.Text = "Filter text is empty.";
                return false;
            }

            var newFilteredReader = _reader.CreateFilteredReader(conditions);

            await ClearFilterAsync(reloadData: false);
            _filteredReader = newFilteredReader;
            _activeFilterText = filterText;
            if (_activeSession is not null)
            {
                _activeSession.ActiveFilterText = filterText;
            }

            if (loadFirstPage)
            {
                GoToPageTextBox.Text = "1";
                PageInfoTextBlock.Text = "Loading filtered page 1...";
                await LoadPageAsync(0);
            }

            return true;
        }
        catch (Exception ex)
        {
            IndexStatusTextBlock.Text = $"Filter error: {ex.Message}";
            return false;
        }
    }

    private async Task ClearFilterAsync(bool reloadData)
    {
        _activeFilterText = string.Empty;
        if (_activeSession is not null)
        {
            _activeSession.ActiveFilterText = string.Empty;
        }

        await DisposeFilteredReaderAsync();

        if (reloadData && _reader is not null)
        {
            GoToPageTextBox.Text = "1";
            await LoadPageAsync(0);
            StartBackgroundIndexing();
        }
    }

    private async Task LoadPageAsync(int pageIndex)
    {
        if (_reader is null)
        {
            return;
        }

        _pageLoadCts?.Cancel();
        _pageLoadCts?.Dispose();
        _pageLoadCts = new CancellationTokenSource();

        try
        {
            SetBusy(true);
            IndexStatusTextBlock.Text = _filteredReader is null
                ? $"Loading page {pageIndex + 1:N0}..."
                : $"Loading filtered page {pageIndex + 1:N0}...";

            CsvPageData page;
            var isFilteredView = _filteredReader is not null;

            if (_filteredReader is null)
            {
                page = await _reader.GetPageAsync(pageIndex, _pageLoadCts.Token);
            }
            else
            {
                page = await _filteredReader.GetPageAsync(pageIndex, _pageLoadCts.Token);
            }

            _currentPageIndex = page.PageIndex;
            if (_activeSession is not null)
            {
                _activeSession.CurrentPageIndex = page.PageIndex;
                _activeSession.ActiveFilterText = _activeFilterText;
            }

            var table = BuildDataTable(_displayHeaders, page.Rows);
            CsvDataGrid.ItemsSource = table.DefaultView;

            _hasPreviousPage = page.HasPreviousPage;
            _hasNextPage = page.HasNextPage;
            GoToPageTextBox.Text = (_currentPageIndex + 1).ToString(CultureInfo.InvariantCulture);
            PageInfoTextBlock.Text = BuildPageInfoText(page, isFilteredView);

            RefreshIndexStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
            IndexStatusTextBlock.Text = "The selected page is outside the available range.";
        }
        catch (Exception ex)
        {
            IndexStatusTextBlock.Text = $"Read failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StartBackgroundIndexing()
    {
        _backgroundIndexCts?.Cancel();
        _backgroundIndexCts?.Dispose();
        _backgroundIndexCts = null;

        if (BackgroundIndexCheckBox.IsChecked != true)
        {
            return;
        }

        if (_filteredReader is not null)
        {
            StartFilteredBackgroundIndexing(_filteredReader);
            return;
        }

        if (_reader is not null)
        {
            StartFullBackgroundIndexing(_reader);
        }
    }

    private void StartFullBackgroundIndexing(LargeCsvPagedReader reader)
    {
        _backgroundIndexCts = new CancellationTokenSource();
        var token = _backgroundIndexCts.Token;

        var progress = new Progress<CsvIndexProgress>(state =>
        {
            if (!ReferenceEquals(reader, _reader) || _filteredReader is not null || _isBusy)
            {
                return;
            }

            IndexStatusTextBlock.Text = state.IsCompleted
                ? $"Index complete: {state.IndexedRows:N0} rows."
                : $"Indexing rows: {state.IndexedRows:N0}+";
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await reader.BuildFullIndexAsync(progress, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_isBusy && ReferenceEquals(reader, _reader))
                    {
                        IndexStatusTextBlock.Text = $"Background indexing failed: {ex.Message}";
                    }
                });
            }
        }, token);
    }

    private void StartFilteredBackgroundIndexing(CsvFilteredPagedReader reader)
    {
        _backgroundIndexCts = new CancellationTokenSource();
        var token = _backgroundIndexCts.Token;

        var progress = new Progress<CsvFilterIndexProgress>(state =>
        {
            if (!ReferenceEquals(reader, _filteredReader) || _isBusy)
            {
                return;
            }

            IndexStatusTextBlock.Text = state.IsCompleted
                ? $"Filter complete: {state.MatchedRows:N0} matches (scanned {state.ScannedRows:N0} rows)."
                : $"Filtering... {state.MatchedRows:N0} matches, scanned {state.ScannedRows:N0}+ rows.";
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await reader.BuildFullIndexAsync(progress, token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!_isBusy && ReferenceEquals(reader, _filteredReader))
                    {
                        IndexStatusTextBlock.Text = $"Background filtering failed: {ex.Message}";
                    }
                });
            }
        }, token);
    }

    private int GetSelectedPageSize()
    {
        if (PageSizeComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return 1000;
    }

    private string BuildPageInfoText(CsvPageData page, bool isFilteredView)
    {
        var pageTitle = isFilteredView ? "Filtered page" : "Page";

        if (page.Rows.Count == 0)
        {
            return page.TotalRows is long totalRows
                ? $"{pageTitle} {page.PageIndex + 1:N0} | No rows (total {totalRows:N0})"
                : $"{pageTitle} {page.PageIndex + 1:N0} | No rows";
        }

        var rowRangeText = isFilteredView
            ? $"Matches {page.StartRowNumber:N0}-{page.EndRowNumber:N0}"
            : $"Rows {page.StartRowNumber:N0}-{page.EndRowNumber:N0}";

        return page.TotalRows is long knownTotal
            ? $"{pageTitle} {page.PageIndex + 1:N0} | {rowRangeText} of {knownTotal:N0}"
            : $"{pageTitle} {page.PageIndex + 1:N0} | {rowRangeText}";
    }

    private void RefreshIndexStatus()
    {
        if (_filteredReader is not null)
        {
            if (_filteredReader.IsScanComplete)
            {
                IndexStatusTextBlock.Text = $"Filter complete: {_filteredReader.MatchedRows:N0} matches (scanned {_filteredReader.ScannedRows:N0} rows).";
            }
            else
            {
                IndexStatusTextBlock.Text = $"Filter active ({_activeFilterText}). {_filteredReader.MatchedRows:N0} matches, scanned {_filteredReader.ScannedRows:N0}+ rows.";
            }

            return;
        }

        if (_reader is null)
        {
            return;
        }

        if (_reader.IsIndexComplete)
        {
            IndexStatusTextBlock.Text = $"Index complete: {_reader.IndexedRows:N0} rows.";
        }
        else
        {
            IndexStatusTextBlock.Text = $"Indexed rows: {_reader.IndexedRows:N0}+";
        }
    }

    private static DataTable BuildDataTable(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var table = new DataTable();
        var columnCount = headers.Count;

        foreach (var row in rows)
        {
            if (row.Length > columnCount)
            {
                columnCount = row.Length;
            }
        }

        for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
        {
            var name = columnIndex < headers.Count ? headers[columnIndex] : $"extra_{columnIndex + 1}";
            table.Columns.Add(name);
        }

        foreach (var row in rows)
        {
            var dataRow = table.NewRow();

            for (var columnIndex = 0; columnIndex < columnCount; columnIndex++)
            {
                dataRow[columnIndex] = columnIndex < row.Length ? row[columnIndex] : string.Empty;
            }

            table.Rows.Add(dataRow);
        }

        return table;
    }

    private static IReadOnlyList<string> BuildUniqueColumnNames(IReadOnlyList<string> rawNames)
    {
        var uniqueNames = new List<string>(rawNames.Count);
        var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < rawNames.Count; index++)
        {
            var baseName = string.IsNullOrWhiteSpace(rawNames[index])
                ? $"column_{index + 1}"
                : rawNames[index].Trim();

            if (!nameCounts.TryGetValue(baseName, out var count))
            {
                nameCounts[baseName] = 1;
                uniqueNames.Add(baseName);
                continue;
            }

            count++;
            nameCounts[baseName] = count;
            uniqueNames.Add($"{baseName}_{count}");
        }

        return uniqueNames;
    }

    private static Style BuildCellTextStyle(bool showToolTip, bool isLinkStyle)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        style.Setters.Add(new Setter(TextBlock.ForegroundProperty, isLinkStyle ? Brushes.SteelBlue : Brushes.Black));
        style.Setters.Add(new Setter(TextBlock.TextDecorationsProperty, null));

        var selectedTrigger = new DataTrigger
        {
            Binding = new Binding("IsSelected")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1)
            },
            Value = true
        };
        selectedTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.White));
        style.Triggers.Add(selectedTrigger);

        if (showToolTip)
        {
            if (isLinkStyle)
            {
                style.Setters.Add(new Setter(
                    FrameworkElement.ToolTipProperty,
                    new Binding("Text")
                    {
                        RelativeSource = new RelativeSource(RelativeSourceMode.Self),
                        StringFormat = "Follow link (Ctrl+Click)\n{0}"
                    }));
            }
            else
            {
                style.Setters.Add(new Setter(
                    FrameworkElement.ToolTipProperty,
                    new Binding("Text")
                    {
                        RelativeSource = new RelativeSource(RelativeSourceMode.Self)
                    }));
            }
        }

        if (isLinkStyle)
        {
            var hoverTrigger = new MultiDataTrigger();
            hoverTrigger.Conditions.Add(new Condition
            {
                Binding = new Binding("IsMouseOver")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.Self)
                },
                Value = true
            });
            hoverTrigger.Conditions.Add(new Condition
            {
                Binding = new Binding("IsSelected")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1)
                },
                Value = false
            });
            hoverTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, Brushes.RoyalBlue));
            hoverTrigger.Setters.Add(new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Underline));
            style.Triggers.Add(hoverTrigger);
        }

        return style;
    }

    private bool TryGetLinkFromSource(object? source, out string link)
    {
        link = string.Empty;

        if (source is not DependencyObject dependencyObject)
        {
            return false;
        }

        var cell = FindVisualParent<DataGridCell>(dependencyObject);
        if (cell is null || cell.DataContext is not DataRowView rowView)
        {
            return false;
        }

        if (!TryGetCellValue(rowView, cell.Column, out var cellValue))
        {
            return false;
        }

        return TryNormalizeHttpUrl(cellValue, out link);
    }

    private void OpenLinkInSidePane(string link)
    {
        ShowBrowserPane();
        SetSidePaneMode(SidePaneViewMode.Web, ensurePaneVisible: false);
        NavigateSideBrowser(link);
    }

    private void ShowBrowserPane()
    {
        _isBrowserPaneOpen = true;
        BrowserPane.Visibility = Visibility.Visible;
        BrowserSplitter.Visibility = Visibility.Visible;
        BrowserSplitterColumn.Width = new GridLength(6);

        EnsureBrowserPaneLayout(useDefaultWhenCollapsed: true);
        _ = EnsureSideBrowserInitializedAsync();
        SetSidePaneMode(_sidePaneViewMode, ensurePaneVisible: false);
        UpdateBrowserControlsState();
    }

    private void SetSidePaneMode(SidePaneViewMode mode, bool ensurePaneVisible)
    {
        if (ensurePaneVisible)
        {
            ShowBrowserPane();
        }

        var hasTrainingImage = _activeSession is not null && _activeSession.TrainingImagePaths.Count > 0;
        if (mode == SidePaneViewMode.TrainingPlot && !hasTrainingImage)
        {
            mode = SidePaneViewMode.Web;
        }

        _sidePaneViewMode = mode;

        var showWeb = mode == SidePaneViewMode.Web;
        SideBrowser.Visibility = showWeb ? Visibility.Visible : Visibility.Collapsed;
        SideTrainingPlotPane.Visibility = showWeb ? Visibility.Collapsed : Visibility.Visible;
        BrowserPaneToggleViewButton.Content = showWeb ? "Show Plot" : "Show Web";

        ApplyControlState();
    }

    private void EnsureBrowserPaneLayout(bool useDefaultWhenCollapsed)
    {
        BrowserColumn.MinWidth = BrowserPaneMinWidth;

        var maxBrowserWidth = Math.Max(
            BrowserPaneMinWidth,
            ActualWidth - BrowserPaneMinGridWidth - BrowserSplitterColumn.Width.Value - 32);

        var currentWidth = BrowserColumn.ActualWidth;
        if (BrowserColumn.Width.IsAbsolute && BrowserColumn.Width.Value > 0)
        {
            currentWidth = BrowserColumn.Width.Value;
        }

        if (currentWidth <= 0 || double.IsNaN(currentWidth))
        {
            currentWidth = useDefaultWhenCollapsed ? _lastBrowserPaneWidth : BrowserPaneMinWidth;
        }

        var clampedWidth = Math.Min(maxBrowserWidth, Math.Max(BrowserPaneMinWidth, currentWidth));
        _lastBrowserPaneWidth = clampedWidth;
        BrowserColumn.Width = new GridLength(clampedWidth, GridUnitType.Pixel);
    }

    private void HideBrowserPane()
    {
        _isBrowserPaneOpen = false;
        _currentEmbeddedUrl = null;

        try
        {
            if (SideBrowser.CoreWebView2 is not null)
            {
                SideBrowser.CoreWebView2.Navigate("about:blank");
            }
            else
            {
                SideBrowser.Source = new Uri("about:blank");
            }
        }
        catch
        {
        }

        BrowserAddressTextBox.Text = string.Empty;
        SideTrainingPlotPane.Visibility = Visibility.Collapsed;
        BrowserPane.Visibility = Visibility.Collapsed;
        BrowserSplitter.Visibility = Visibility.Collapsed;
        BrowserSplitterColumn.Width = new GridLength(0);
        if (BrowserColumn.ActualWidth > 0)
        {
            _lastBrowserPaneWidth = BrowserColumn.ActualWidth;
        }
        BrowserColumn.Width = new GridLength(0);
        BrowserColumn.MinWidth = 0;
        UpdateBrowserControlsState();
    }

    private void NavigateSideBrowser(string rawValue)
    {
        if (!TryNormalizeHttpUrl(rawValue, out var url))
        {
            IndexStatusTextBlock.Text = "Invalid URL for side browser.";
            return;
        }

        try
        {
            ShowBrowserPane();
            SetSidePaneMode(SidePaneViewMode.Web, ensurePaneVisible: false);
            _currentEmbeddedUrl = url;
            BrowserAddressTextBox.Text = url;
            if (SideBrowser.CoreWebView2 is not null)
            {
                var core = SideBrowser.CoreWebView2;
                core.Stop();
                core.Navigate("about:blank");
                core.Navigate(url);
            }
            else
            {
                SideBrowser.Source = new Uri("about:blank");
                SideBrowser.Source = new Uri(url);
            }

            IndexStatusTextBlock.Text = "Opened link in side panel.";
        }
        catch (Exception ex)
        {
            IndexStatusTextBlock.Text = $"Open side browser failed: {ex.Message}";
        }
        finally
        {
            UpdateBrowserControlsState();
        }
    }

    private void UpdateBrowserControlsState()
    {
        var isOpen = _isBrowserPaneOpen;
        var hasUrl = !string.IsNullOrWhiteSpace(_currentEmbeddedUrl);
        var hasTrainingImage = _activeSession is not null && _activeSession.TrainingImagePaths.Count > 0;
        var isWebMode = _sidePaneViewMode == SidePaneViewMode.Web;
        var core = SideBrowser.CoreWebView2;

        BrowserPaneToggleViewButton.IsEnabled = isOpen && hasTrainingImage;
        BrowserAddressTextBox.IsEnabled = isOpen && isWebMode && core is not null;
        BrowserBackButton.IsEnabled = isOpen && isWebMode && core?.CanGoBack == true;
        BrowserForwardButton.IsEnabled = isOpen && isWebMode && core?.CanGoForward == true;
        BrowserRefreshButton.IsEnabled = isOpen && isWebMode && core is not null && hasUrl;
        BrowserOpenExternalButton.IsEnabled = isOpen && isWebMode && hasUrl;
        BrowserCloseButton.IsEnabled = isOpen;
    }

    private static bool TryNormalizeHttpUrl(string rawValue, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;

        var candidate = rawValue.Trim();
        if (candidate.Length == 0)
        {
            return false;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (!Uri.TryCreate($"https://{candidate}", UriKind.Absolute, out uri))
            {
                return false;
            }
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    private static string BuildUiStateFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppData, "TenhouCsvReader", "ui-state.txt");
    }

    private static string BuildSessionTempRootPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppData, "TenhouCsvReader", "sessions");
    }

    private void LoadUiState()
    {
        try
        {
            if (!File.Exists(_uiStateFilePath))
            {
                return;
            }

            var rawText = File.ReadAllText(_uiStateFilePath).Trim();
            if (!double.TryParse(rawText, NumberStyles.Float, CultureInfo.InvariantCulture, out var savedWidth))
            {
                return;
            }

            if (savedWidth >= BrowserPaneMinWidth)
            {
                _lastBrowserPaneWidth = savedWidth;
            }
        }
        catch
        {
            // Ignore bad state file and keep defaults.
        }
    }

    private void SaveUiState()
    {
        try
        {
            var folderPath = Path.GetDirectoryName(_uiStateFilePath);
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            var widthToPersist = _lastBrowserPaneWidth <= 0 ? BrowserPaneDefaultWidth : _lastBrowserPaneWidth;
            File.WriteAllText(_uiStateFilePath, widthToPersist.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // Keep shutdown resilient if state persistence fails.
        }
    }

    private static bool IsCtrlPressed()
    {
        return (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
    }

    private static bool TryOpenUrl(string url, out string? error)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });

            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryGetCurrentCellValue(out string cellValue)
    {
        cellValue = string.Empty;

        var cellInfo = CsvDataGrid.CurrentCell;
        if (cellInfo.Item is not DataRowView rowView || cellInfo.Column is null)
        {
            return false;
        }

        return TryGetCellValue(rowView, cellInfo.Column, out cellValue);
    }

    private static bool TryGetCellValue(DataRowView rowView, DataGridColumn column, out string cellValue)
    {
        cellValue = string.Empty;

        var columnName = column.SortMemberPath;

        if (!string.IsNullOrWhiteSpace(columnName) && rowView.Row.Table.Columns.Contains(columnName))
        {
            cellValue = ToCellString(rowView.Row[columnName]);
            return true;
        }

        var columnIndex = column.DisplayIndex;
        if (columnIndex < 0 || columnIndex >= rowView.Row.ItemArray.Length)
        {
            return false;
        }

        cellValue = ToCellString(rowView.Row.ItemArray[columnIndex]);
        return true;
    }

    private bool TryBuildSelectedRowsCsv(out string csvText)
    {
        csvText = string.Empty;

        var selectedRows = new List<DataRowView>(CsvDataGrid.SelectedItems.Count > 0 ? CsvDataGrid.SelectedItems.Count : 1);

        try
        {
            foreach (var item in CsvDataGrid.SelectedItems)
            {
                if (item is DataRowView rowView)
                {
                    selectedRows.Add(rowView);
                }
            }
        }
        catch
        {
            return false;
        }

        if (selectedRows.Count == 0 && CsvDataGrid.CurrentCell.Item is DataRowView currentRow)
        {
            selectedRows.Add(currentRow);
        }

        if (selectedRows.Count == 0)
        {
            return false;
        }

        var builder = new StringBuilder(selectedRows.Count * 64);

        for (var rowIndex = 0; rowIndex < selectedRows.Count; rowIndex++)
        {
            if (rowIndex > 0)
            {
                builder.AppendLine();
            }

            builder.Append(SerializeRowToCsv(selectedRows[rowIndex]));
        }

        csvText = builder.ToString();
        return true;
    }

    private bool HasSelectedRow()
    {
        if (CsvDataGrid.SelectedItem is DataRowView)
        {
            return true;
        }

        if (CsvDataGrid.CurrentCell.Item is DataRowView)
        {
            return true;
        }

        return false;
    }

    private static string SerializeRowToCsv(DataRowView rowView)
    {
        object?[] rowValues;

        try
        {
            rowValues = rowView.Row.ItemArray;
        }
        catch
        {
            return string.Empty;
        }

        var builder = new StringBuilder(rowValues.Length * 32);

        for (var index = 0; index < rowValues.Length; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(EscapeCsvField(ToCellString(rowValues[index])));
        }

        return builder.ToString();
    }

    private static string EscapeCsvField(string value)
    {
        var requiresQuoting =
            value.Contains(',') ||
            value.Contains('"') ||
            value.Contains('\r') ||
            value.Contains('\n') ||
            (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));

        if (!requiresQuoting)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string ToCellString(object? value)
    {
        if (value is null || value == DBNull.Value)
        {
            return string.Empty;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T typedParent)
            {
                return typedParent;
            }

            child = GetParentObject(child);
        }

        return null;
    }

    private static DependencyObject? GetParentObject(DependencyObject child)
    {
        try
        {
            return child switch
            {
                Visual => VisualTreeHelper.GetParent(child),
                Visual3D => VisualTreeHelper.GetParent(child),
                FrameworkContentElement frameworkContentElement => frameworkContentElement.Parent,
                _ => LogicalTreeHelper.GetParent(child)
            };
        }
        catch
        {
            return LogicalTreeHelper.GetParent(child);
        }
    }

    private static bool TrySetClipboardText(string text)
    {
        var value = text ?? string.Empty;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(value, false);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(10);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryGetDroppedInputPaths(IDataObject dataObject, out IReadOnlyList<string> inputPaths)
    {
        inputPaths = Array.Empty<string>();

        if (!dataObject.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (dataObject.GetData(DataFormats.FileDrop) is not string[] droppedPaths || droppedPaths.Length == 0)
        {
            return false;
        }

        var validPaths = new List<string>(droppedPaths.Length);

        foreach (var droppedPath in droppedPaths)
        {
            if (!TryValidateInputPath(droppedPath, out var normalizedPath, out _))
            {
                continue;
            }

            validPaths.Add(normalizedPath);
        }

        if (validPaths.Count == 0)
        {
            return false;
        }

        inputPaths = validPaths;
        return true;
    }

    private static bool TryValidateInputPath(string filePath, out string normalizedPath, out string errorMessage)
    {
        normalizedPath = string.Empty;
        errorMessage = string.Empty;

        var candidatePath = filePath?.Trim();
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            errorMessage = "path is empty.";
            return false;
        }

        while (candidatePath.Length >= 2 &&
               ((candidatePath[0] == '"' && candidatePath[^1] == '"') ||
                (candidatePath[0] == '\'' && candidatePath[^1] == '\'')))
        {
            candidatePath = candidatePath[1..^1].Trim();
        }

        if (Uri.TryCreate(candidatePath, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            candidatePath = uri.LocalPath;
        }

        try
        {
            normalizedPath = Path.GetFullPath(candidatePath);
        }
        catch (Exception ex)
        {
            errorMessage = $"invalid path ({ex.Message})";
            return false;
        }

        if (!File.Exists(normalizedPath))
        {
            errorMessage = $"file not found ({normalizedPath})";
            return false;
        }

        var extension = Path.GetExtension(normalizedPath);
        if (!string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "only .csv and .zip files are supported.";
            return false;
        }

        return true;
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        ApplyControlState();
    }

    private void ApplyControlState()
    {
        var hasReader = _reader is not null;
        var hasTabs = SessionTabControl.Items.Count > 0;

        OpenCsvButton.IsEnabled = !_isBusy;
        OpenZipButton.IsEnabled = !_isBusy;
        CloseTabButton.IsEnabled = !_isBusy && hasTabs;
        SessionTabControl.IsEnabled = !_isBusy && hasTabs;
        PageSizeComboBox.IsEnabled = !_isBusy;
        BackgroundIndexCheckBox.IsEnabled = !_isBusy;
        TrainingImageComboBox.IsEnabled = !_isBusy &&
                                          _isBrowserPaneOpen &&
                                          _sidePaneViewMode == SidePaneViewMode.TrainingPlot &&
                                          _activeSession?.TrainingImagePaths.Count > 1;

        FilterTextBox.IsEnabled = hasReader && !_isBusy;
        ApplyFilterButton.IsEnabled = hasReader && !_isBusy;
        ClearFilterButton.IsEnabled = hasReader && !_isBusy;

        GoToPageTextBox.IsEnabled = hasReader && !_isBusy;
        GoPageButton.IsEnabled = hasReader && !_isBusy;

        PreviousPageButton.IsEnabled = hasReader && !_isBusy && _hasPreviousPage;
        NextPageButton.IsEnabled = hasReader && !_isBusy && _hasNextPage;
        UpdateBrowserControlsState();
        ApplyTrainingImageZoom();
    }

    private async Task DisposeFilteredReaderAsync()
    {
        _backgroundIndexCts?.Cancel();
        _backgroundIndexCts?.Dispose();
        _backgroundIndexCts = null;

        if (_filteredReader is not null)
        {
            await _filteredReader.DisposeAsync();
            _filteredReader = null;
        }

        _hasPreviousPage = false;
        _hasNextPage = false;
        ApplyControlState();
    }

    private async Task DisposeReaderAsync()
    {
        _pageLoadCts?.Cancel();
        _pageLoadCts?.Dispose();
        _pageLoadCts = null;

        await DisposeFilteredReaderAsync();

        if (_reader is not null)
        {
            await _reader.DisposeAsync();
            _reader = null;
        }

        _activeFilePath = null;
        _activeFilterText = string.Empty;
        _currentPageIndex = 0;
        _hasPreviousPage = false;
        _hasNextPage = false;
        _displayHeaders = Array.Empty<string>();
        ApplyControlState();
    }

    private async Task DisposeAllSessionsAsync()
    {
        await DisposeReaderAsync();

        foreach (var session in _sessions)
        {
            CleanupSessionTempFiles(session);
        }

        _sessions.Clear();
        _activeSession = null;

        _isSwitchingSession = true;
        SessionTabControl.Items.Clear();
        _isSwitchingSession = false;

        CleanupDirectoryIfExists(_sessionTempRootPath);
    }
}
