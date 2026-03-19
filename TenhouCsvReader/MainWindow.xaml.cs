using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using System.Diagnostics;
using System.Data;
using System.Globalization;
using System.IO;
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
    private bool _isBusy;
    private bool _isBrowserPaneOpen;
    private bool _hasPreviousPage;
    private bool _hasNextPage;

    public MainWindow()
    {
        InitializeComponent();

        _uiStateFilePath = BuildUiStateFilePath();
        LoadUiState();

        TryApplyWindowIcon();
        ConfigureSideBrowserUserDataFolder();

        _defaultCellTextStyle = BuildCellTextStyle(showToolTip: false, isLinkStyle: false);
        _tenhouLinkCellTextStyle = BuildCellTextStyle(showToolTip: true, isLinkStyle: true);

        ApplyControlState();
        UpdateBrowserControlsState();
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
        await DisposeReaderAsync();
        base.OnClosed(e);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedCsvPath(e.Data, out _)
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

        if (!TryGetDroppedCsvPath(e.Data, out var csvPath))
        {
            IndexStatusTextBlock.Text = "Drop a single .csv file to open.";
            e.Handled = true;
            return;
        }

        await OpenCsvAsync(csvPath);
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
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await OpenCsvAsync(dialog.FileName);
    }

    public async Task OpenCsvFromLaunchAsync(string filePath)
    {
        if (!TryValidateCsvPath(filePath, out var normalizedPath, out var errorMessage))
        {
            IndexStatusTextBlock.Text = $"Open startup file failed: {errorMessage}";
            return;
        }

        await OpenCsvAsync(normalizedPath);
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
        if (!IsLoaded || _reader is null || string.IsNullOrWhiteSpace(_activeFilePath))
        {
            return;
        }

        await OpenCsvAsync(_activeFilePath);
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
        if (!_isBrowserPaneOpen || core is null || !core.CanGoBack)
        {
            return;
        }

        core.GoBack();
        UpdateBrowserControlsState();
    }

    private void BrowserForwardButton_Click(object sender, RoutedEventArgs e)
    {
        var core = SideBrowser.CoreWebView2;
        if (!_isBrowserPaneOpen || core is null || !core.CanGoForward)
        {
            return;
        }

        core.GoForward();
        UpdateBrowserControlsState();
    }

    private void BrowserRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        var core = SideBrowser.CoreWebView2;
        if (!_isBrowserPaneOpen || core is null)
        {
            return;
        }

        core.Reload();
        UpdateBrowserControlsState();
    }

    private void BrowserAddressTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        NavigateSideBrowser(BrowserAddressTextBox.Text);
        e.Handled = true;
    }

    private void BrowserOpenExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBrowserPaneOpen || string.IsNullOrWhiteSpace(_currentEmbeddedUrl))
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

    private async Task OpenCsvAsync(string filePath)
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

            FilePathTextBlock.Text = filePath;
            FilterTextBox.Text = string.Empty;
            GoToPageTextBox.Text = "1";
            PageInfoTextBlock.Text = "Loading page 1...";
            CsvDataGrid.ItemsSource = null;

            await LoadPageAsync(0);
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
        if (string.IsNullOrWhiteSpace(filterText))
        {
            IndexStatusTextBlock.Text = "Filter text is empty.";
            return;
        }

        try
        {
            var conditions = CsvFilterParser.Parse(filterText, _displayHeaders);
            if (conditions.Count == 0)
            {
                IndexStatusTextBlock.Text = "Filter text is empty.";
                return;
            }

            var newFilteredReader = _reader.CreateFilteredReader(conditions);

            await ClearFilterAsync(reloadData: false);
            _filteredReader = newFilteredReader;
            _activeFilterText = filterText;

            GoToPageTextBox.Text = "1";
            PageInfoTextBlock.Text = "Loading filtered page 1...";

            await LoadPageAsync(0);
            StartBackgroundIndexing();
        }
        catch (Exception ex)
        {
            IndexStatusTextBlock.Text = $"Filter error: {ex.Message}";
        }
    }

    private async Task ClearFilterAsync(bool reloadData)
    {
        _activeFilterText = string.Empty;

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
        UpdateBrowserControlsState();
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
            _currentEmbeddedUrl = url;
            BrowserAddressTextBox.Text = url;
            if (SideBrowser.CoreWebView2 is not null)
            {
                var core = SideBrowser.CoreWebView2;
                if (string.Equals(core.Source, url, StringComparison.OrdinalIgnoreCase))
                {
                    core.Reload();
                }
                else
                {
                    core.Navigate(url);
                }
            }
            else
            {
                if (SideBrowser.Source is not null &&
                    string.Equals(SideBrowser.Source.AbsoluteUri, url, StringComparison.OrdinalIgnoreCase))
                {
                    SideBrowser.Source = new Uri("about:blank");
                }

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
        var core = SideBrowser.CoreWebView2;

        BrowserAddressTextBox.IsEnabled = isOpen && core is not null;
        BrowserBackButton.IsEnabled = isOpen && core?.CanGoBack == true;
        BrowserForwardButton.IsEnabled = isOpen && core?.CanGoForward == true;
        BrowserRefreshButton.IsEnabled = isOpen && core is not null && hasUrl;
        BrowserOpenExternalButton.IsEnabled = isOpen && hasUrl;
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

    private static bool TryGetDroppedCsvPath(IDataObject dataObject, out string csvPath)
    {
        csvPath = string.Empty;

        if (!dataObject.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (dataObject.GetData(DataFormats.FileDrop) is not string[] droppedPaths || droppedPaths.Length != 1)
        {
            return false;
        }

        var candidatePath = droppedPaths[0];
        if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
        {
            return false;
        }

        if (!string.Equals(Path.GetExtension(candidatePath), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        csvPath = candidatePath;
        return true;
    }

    private static bool TryValidateCsvPath(string filePath, out string normalizedPath, out string errorMessage)
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

        if (!string.Equals(Path.GetExtension(normalizedPath), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            errorMessage = "only .csv files are supported.";
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

        OpenCsvButton.IsEnabled = !_isBusy;
        PageSizeComboBox.IsEnabled = !_isBusy;
        BackgroundIndexCheckBox.IsEnabled = !_isBusy;

        FilterTextBox.IsEnabled = hasReader && !_isBusy;
        ApplyFilterButton.IsEnabled = hasReader && !_isBusy;
        ClearFilterButton.IsEnabled = hasReader && !_isBusy;

        GoToPageTextBox.IsEnabled = hasReader && !_isBusy;
        GoPageButton.IsEnabled = hasReader && !_isBusy;

        PreviousPageButton.IsEnabled = hasReader && !_isBusy && _hasPreviousPage;
        NextPageButton.IsEnabled = hasReader && !_isBusy && _hasNextPage;
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
}
