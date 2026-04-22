using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MdTranslatorViewer.Services;
using DrawingColor = System.Drawing.Color;

namespace MdTranslatorViewer;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string TranslationLanguage = "ko";
    private const int FileReadRetryCount = 5;
    private const double MinimumTabWidth = 32;
    private const double MaximumTabWidth = 200;
    private const double CompactTabCloseButtonThreshold = 72;
    private const double MouseWheelTabScrollStep = 72;
    private const double TabItemChromeWidth = 3;
    private const double NormalWindowCornerRadius = 10;
    private const int MaximumDocumentSelectionHistoryEntries = 128;
    private const int WindowMessageAppCommand = 0x0319;
    private const int WindowMessageXButtonUp = 0x020C;
    private const int AppCommandBrowserBackward = 1;
    private const int AppCommandBrowserForward = 2;
    private const int AppCommandDeviceMask = 0xF000;
    private const int XButton1 = 1;
    private const int XButton2 = 2;
    private static readonly TimeSpan FileReadRetryDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan TabScrollIndicatorFadeDuration = TimeSpan.FromMilliseconds(180);
    private static readonly HashSet<string> AllowedExternalSchemes =
    [
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        "mailto",
    ];
    private static readonly HashSet<string> BlockedLocalLinkExtensions =
    [
        ".appref-ms",
        ".bat",
        ".cmd",
        ".com",
        ".exe",
        ".hta",
        ".js",
        ".jse",
        ".lnk",
        ".msi",
        ".msp",
        ".ps1",
        ".ps1xml",
        ".psc1",
        ".psd1",
        ".psm1",
        ".reg",
        ".scr",
        ".url",
        ".vb",
        ".vbe",
        ".vbs",
        ".ws",
        ".wsc",
        ".wsf",
        ".wsh",
    ];

    private static readonly JsonSerializerOptions ScrollSnapshotJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions DocumentPayloadJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string? _startupPath;
    private readonly string _webViewUserDataFolder;
    private readonly AppStateService _appStateService = new();
    private readonly MarkdownTranslationService _translationService = new();
    private readonly MarkdownHtmlRenderer _htmlRenderer = new();
    private readonly DispatcherTimer _reloadTimer;
    private readonly MarkdownPipeline _markdownPipeline;
    private readonly ObservableCollection<DocumentTab> _openDocuments = [];
    private readonly List<DocumentTab> _documentSelectionHistory = [];
    private readonly string _webViewMessageToken = Guid.NewGuid().ToString("N");

    private FileSystemWatcher? _fileWatcher;
    private Task<CoreWebView2Environment>? _webViewEnvironmentTask;
    private bool _documentWebViewReady;
    private bool _documentShellLoaded;
    private bool _initialized;
    private bool _isTabDragInProgress;
    private bool _restoringOpenTabs;
    private bool _syncingTabSelection;
    private bool _tabSelectionPointerDown;
    private bool _navigatingDocumentSelectionHistory;
    private bool _updatingTabLayout;
    private bool _updatingTabScrollIndicator;
    private bool _titleBarDragPending;
    private bool _warnedAboutElevatedDragDrop;
    private Point _tabDragStartPoint;
    private DocumentTab? _tabDragCandidate;
    private double _tabWidth = MaximumTabWidth - TabItemChromeWidth;
    private Visibility _tabCloseButtonVisibility = Visibility.Visible;
    private int _selectDocumentRequestVersion;
    private int _documentSelectionHistoryIndex = -1;
    private int _lastPointerNavigationDirection;
    private Point _titleBarDragStartPoint;
    private int _lastPointerNavigationMessageTime;
    private DocumentTab? _selectedDocument;
    private ViewerAppState _appState = ViewerAppState.Default;
    private IntPtr _windowHandle;

    public MainWindow(string? startupPath)
    {
        _startupPath = startupPath;
        var sessionsRoot = AppStoragePaths.WebViewSessionsDirectory;
        Directory.CreateDirectory(sessionsRoot);
        _webViewUserDataFolder = Path.Combine(
            sessionsRoot,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        CleanupStaleWebViewUserData();
        _appState = _appStateService.Load();
        _appState = _appState with { ColorThemePreset = CurrentTheme.Id };
        _reloadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _reloadTimer.Tick += ReloadTimer_OnTick;
        _markdownPipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml()
            .Build();

        InitializeComponent();
        ApplyThemePreset(CurrentTheme);
        UpdateSettingsMenuState();
        StateChanged += (_, _) =>
        {
            UpdateWindowControls();
            UpdateWindowChromeLayout();
        };
        Activated += MainWindow_OnActivated;
        ApplySavedWindowPlacement();
        UpdateWindowControls();
        UpdateWindowChromeLayout();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DocumentTab> OpenDocuments => _openDocuments;

    public DocumentTab? SelectedDocument
    {
        get => _selectedDocument;
        set
        {
            if (ReferenceEquals(_selectedDocument, value))
            {
                return;
            }

            _selectedDocument = value;
            OnPropertyChanged();
        }
    }

    public bool CanNavigateBack => GetNavigableDocumentSelectionHistoryIndex(-1) >= 0;

    public bool CanNavigateForward => GetNavigableDocumentSelectionHistoryIndex(1) >= 0;

    public double TabWidth
    {
        get => _tabWidth;
        private set
        {
            if (Math.Abs(_tabWidth - value) < 0.5)
            {
                return;
            }

            _tabWidth = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TabItemWidth));
            UpdateTabCloseButtonVisibility();
        }
    }

    public double TabMinWidth => MinimumTabWidth - TabItemChromeWidth;

    public double TabMaxWidth => MaximumTabWidth - TabItemChromeWidth;

    public double TabItemWidth => UsesTitleSizedTabs ? double.NaN : TabWidth;

    public double TabItemMinWidth => UsesTitleSizedTabs
        ? CompactTabCloseButtonThreshold
        : TabMinWidth;

    public double TabItemMaxWidth => TabMaxWidth;

    public Visibility TabCloseButtonVisibility
    {
        get => _tabCloseButtonVisibility;
        private set
        {
            if (_tabCloseButtonVisibility == value)
            {
                return;
            }

            _tabCloseButtonVisibility = value;
            OnPropertyChanged();
        }
    }

    public Task OpenDocumentFromExternalAsync(string path)
    {
        return OpenDocumentAsync(path, forceReload: true);
    }

    private bool UsesTitleSizedTabs => _appState.TopTabWidthMode == TopTabWidthMode.SizeToTitle;

    private ViewerThemePreset CurrentTheme => ViewerThemeCatalog.Get(_appState.ColorThemePreset);

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_OnThreadPreprocessMessage;
        _reloadTimer.Stop();
        _fileWatcher?.Dispose();
        SaveAppState();

        foreach (var document in _openDocuments)
        {
            document.Dispose();
        }

        TryDeleteWebViewUserData();

        base.OnClosed(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowHandle = new WindowInteropHelper(this).Handle;
        ComponentDispatcher.ThreadPreprocessMessage -= ComponentDispatcher_OnThreadPreprocessMessage;
        ComponentDispatcher.ThreadPreprocessMessage += ComponentDispatcher_OnThreadPreprocessMessage;
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            await EnsureDocumentWebViewReadyAsync();
            SetTranslationEnabled(_appState.TranslateEnabled, persist: false);
            UpdateTabsState();

            var restoredTabs = await RestoreOpenTabsAsync();

            if (!string.IsNullOrWhiteSpace(_startupPath) && File.Exists(_startupPath))
            {
                await OpenDocumentAsync(_startupPath);
                return;
            }

            if (restoredTabs)
            {
                WarnIfDragDropBlockedByElevation();
                return;
            }

            await RenderSelectedDocumentAsync();
            WarnIfDragDropBlockedByElevation();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Failed to initialize the viewer.\n\n{ex.Message}",
                "MD Translator Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private Task<CoreWebView2Environment> GetWebViewEnvironmentAsync()
    {
        return _webViewEnvironmentTask ??= CreateWebViewEnvironmentAsync(_webViewUserDataFolder);
    }

    private static async Task<CoreWebView2Environment> CreateWebViewEnvironmentAsync(string userDataFolder)
    {
        Directory.CreateDirectory(userDataFolder);
        return await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
    }

    private async Task EnsureDocumentWebViewReadyAsync()
    {
        if (_documentWebViewReady)
        {
            return;
        }

        var environment = await GetWebViewEnvironmentAsync();
        await DocumentWebView.EnsureCoreWebView2Async(environment);
        ConfigureWebView(DocumentWebView);
        _documentWebViewReady = true;
    }

    private void ConfigureWebView(Microsoft.Web.WebView2.Wpf.WebView2 webView)
    {
        var settings = webView.CoreWebView2.Settings;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;

        webView.CoreWebView2.NavigationStarting -= WebView_OnNavigationStarting;
        webView.CoreWebView2.NavigationStarting += WebView_OnNavigationStarting;
        webView.CoreWebView2.NewWindowRequested -= WebView_OnNewWindowRequested;
        webView.CoreWebView2.NewWindowRequested += WebView_OnNewWindowRequested;
        webView.CoreWebView2.WebMessageReceived -= WebView_OnWebMessageReceived;
        webView.CoreWebView2.WebMessageReceived += WebView_OnWebMessageReceived;
    }

    private DocumentTab GetOrCreateDocumentTab(string fullPath, bool updateTabsState = true)
    {
        var document = _openDocuments.FirstOrDefault(item =>
            string.Equals(item.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));

        if (document is not null)
        {
            return document;
        }

        document = new DocumentTab(fullPath);
        _openDocuments.Add(document);

        if (updateTabsState)
        {
            UpdateTabsState();
        }

        return document;
    }

    private async Task OpenDocumentAsync(
        string path,
        bool forceReload = false,
        bool activateDocument = true,
        bool persistSession = true)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"File not found:\n{path}", "MD Translator Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var fullPath = Path.GetFullPath(path);
        var document = GetOrCreateDocumentTab(fullPath);

        if (activateDocument)
        {
            await SelectDocumentAsync(document);
        }

        if (persistSession)
        {
            PersistSessionState();
        }

        var currentFileContentVersion = TryGetFileContentVersion(document.FilePath);
        if (document.Markdown is not null &&
            !document.HasChangedOnDisk(currentFileContentVersion))
        {
            if (!forceReload)
            {
                _ = EnsureTranslationForDocumentAsync(
                    document,
                    announceStatus: _appState.TranslateEnabled && ReferenceEquals(SelectedDocument, document));
            }

            return;
        }

        await LoadDocumentIntoTabAsync(
            document,
            preserveScroll: activateDocument && document.Markdown is not null);
    }

    private async Task LoadDocumentIntoTabAsync(DocumentTab document, bool preserveScroll = false)
    {
        if (!File.Exists(document.FilePath))
        {
            if (await IsDocumentStillMissingAsync(document.FilePath))
            {
                await HandleMissingDocumentAsync(document);
                return;
            }
        }

        var (loadVersion, cancellationToken) = document.BeginLoad();

        if (ReferenceEquals(SelectedDocument, document))
        {
            SetLoading(true, "Opening document...");
            StatusText.Text = "Loading document...";
            ConfigureWatcher(document.FilePath);
        }

        try
        {
            await EnsureDocumentWebViewReadyAsync();
            var markdown = await ReadMarkdownFileAsync(document.FilePath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var fileContentVersion = TryGetFileContentVersion(document.FilePath);

            document.MarkLoaded(markdown, fileContentVersion);
            var cachedTranslation = await _translationService.TryGetCachedTranslationAsync(
                markdown,
                TranslationLanguage,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(cachedTranslation))
            {
                var showTranslatedStatus = _appState.TranslateEnabled && ReferenceEquals(SelectedDocument, document);
                document.MarkTranslated(
                    cachedTranslation,
                    updateStatus: showTranslatedStatus);

                if (!showTranslatedStatus)
                {
                    document.SetStatus($"Loaded {document.DisplayName}");
                }
            }
            else
            {
                document.SetStatus($"Loaded {document.DisplayName}");

                if (_appState.TranslateEnabled && ReferenceEquals(SelectedDocument, document))
                {
                    document.SetStatus($"Loaded {document.DisplayName}. Translating...");
                }
            }

            if (ReferenceEquals(SelectedDocument, document))
            {
                await RenderSelectedDocumentAsync(preserveScroll: preserveScroll);
                SetLoading(false);
            }

            _ = EnsureTranslationForDocumentAsync(
                document,
                loadVersion,
                cancellationToken,
                markdown,
                announceStatus: _appState.TranslateEnabled && ReferenceEquals(SelectedDocument, document));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!File.Exists(document.FilePath))
            {
                if (await IsDocumentStillMissingAsync(document.FilePath))
                {
                    await HandleMissingDocumentAsync(document);
                    return;
                }

                await LoadDocumentIntoTabAsync(document, preserveScroll);
                return;
            }

            document.MarkLoadFailed();

            if (ReferenceEquals(SelectedDocument, document))
            {
                StatusText.Text = "Load failed";
                SetLoading(false);
                await RenderSelectedDocumentAsync();
            }

            MessageBox.Show(
                this,
                $"Failed to open the document.\n\n{ex.Message}",
                "MD Translator Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task EnsureTranslationForDocumentAsync(DocumentTab document, bool announceStatus = false)
    {
        if (!document.TryBeginTranslation(
                announceStatus,
                out var loadVersion,
                out var cancellationToken,
                out var markdown))
        {
            return;
        }

        await EnsureTranslationForDocumentAsync(document, loadVersion, cancellationToken, markdown);
    }

    private async Task EnsureTranslationForDocumentAsync(
        DocumentTab document,
        int loadVersion,
        CancellationToken cancellationToken,
        string? markdown = null,
        bool announceStatus = false)
    {
        var sourceMarkdown = markdown ?? document.Markdown;
        if (string.IsNullOrWhiteSpace(sourceMarkdown))
        {
            document.FinishTranslationSkipped();
            return;
        }

        try
        {
            var translatedMarkdown = await _translationService.TranslateMarkdownAsync(
                sourceMarkdown,
                TranslationLanguage,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (!document.IsCurrentLoad(loadVersion))
            {
                return;
            }

            document.MarkTranslated(
                translatedMarkdown,
                updateStatus: _appState.TranslateEnabled && ReferenceEquals(SelectedDocument, document));

            if (ReferenceEquals(SelectedDocument, document) && _appState.TranslateEnabled)
            {
                await RenderSelectedDocumentAsync(preserveScroll: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!document.IsCurrentLoad(loadVersion))
            {
                return;
            }

            document.MarkTranslationFailed(
                updateStatus: _appState.TranslateEnabled && ReferenceEquals(SelectedDocument, document));

            if (ReferenceEquals(SelectedDocument, document) && _appState.TranslateEnabled)
            {
                await RenderSelectedDocumentAsync(preserveScroll: true);
                MessageBox.Show(
                    this,
                    $"The document opened, but translation failed.\n\n{ex.Message}",
                    "MD Translator Viewer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private async Task RenderSelectedDocumentAsync(bool preserveScroll = false)
    {
        await RenderSelectedDocumentAsync(
            preserveScroll,
            scrollSnapshotOverride: null);
    }

    private async Task RenderSelectedDocumentAsync(
        bool preserveScroll,
        ScrollSnapshot? scrollSnapshotOverride)
    {
        await EnsureDocumentWebViewReadyAsync();

        if (SelectedDocument is null)
        {
            RenderEmptyState();
            return;
        }

        UpdateWindowStateForSelectedDocument();
        var scrollSnapshot = preserveScroll
            ? await CaptureScrollSnapshotAsync()
            : scrollSnapshotOverride;

        if (SelectedDocument.Markdown is null)
        {
            DocumentWebView.NavigateToString(_htmlRenderer.RenderEmpty("Opening document...", _webViewMessageToken, CurrentTheme.DocumentTheme));
            _documentShellLoaded = false;
            return;
        }

        if (_appState.TranslateEnabled && SelectedDocument.TranslatedMarkdown is not null)
        {
            await NavigateDocumentAsync(
                SelectedDocument,
                SelectedDocument.TranslatedMarkdown,
                SelectedDocument.FilePath,
                "Translated",
                scrollSnapshot);
            return;
        }

        await NavigateDocumentAsync(
            SelectedDocument,
            SelectedDocument.Markdown,
            SelectedDocument.FilePath,
            "Document",
            scrollSnapshot);
        _ = EnsureTranslationForDocumentAsync(
            SelectedDocument,
            announceStatus: _appState.TranslateEnabled && SelectedDocument.TranslatedMarkdown is null);
    }

    private async Task NavigateDocumentAsync(
        DocumentTab document,
        string markdown,
        string filePath,
        string paneTitle,
        ScrollSnapshot? scrollSnapshot)
    {
        var payload = _htmlRenderer.CreateDocumentPayload(
            markdown,
            filePath,
            paneTitle,
            _markdownPipeline,
            document.GetCodeBlockTranslationPayloads());
        await EnsureDocumentShellLoadedAsync();
        await RenderDocumentPayloadAsync(payload);
        await RestoreScrollSnapshotAsync(scrollSnapshot);
    }

    private async Task EnsureDocumentShellLoadedAsync()
    {
        if (_documentShellLoaded && await IsDocumentShellReadyAsync())
        {
            return;
        }

        var navigationTask = WaitForNavigationAsync(DocumentWebView);
        DocumentWebView.NavigateToString(_htmlRenderer.RenderDocumentShell(_webViewMessageToken, CurrentTheme.DocumentTheme));
        await navigationTask;
        await WaitForDocumentShellReadyAsync();
        _documentShellLoaded = true;
    }

    private async Task RenderDocumentPayloadAsync(DocumentRenderPayload payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, DocumentPayloadJsonOptions);
        var script = $$"""
            (() => {
                const payload = {{payloadJson}};
                if (typeof window.mdvRenderDocument !== "function") {
                    return false;
                }
                window.mdvRenderDocument(payload);
                return true;
            })();
            """;

        var result = await DocumentWebView.ExecuteScriptAsync(script);
        if (!string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
        {
            _documentShellLoaded = false;
            throw new InvalidOperationException("The document renderer was not ready.");
        }
    }

    private void RenderEmptyState()
    {
        var emptyPage = _htmlRenderer.RenderEmpty("Open a Markdown file to start.", _webViewMessageToken, CurrentTheme.DocumentTheme);
        DocumentWebView.NavigateToString(emptyPage);
        _documentShellLoaded = false;
        SetLoading(false);
        FilePathText.Text = "Open a Markdown file to start.";
        StatusText.Text = "Ready";
        Title = "MD Translator Viewer";
        ConfigureWatcher(null);
    }

    private void UpdateWindowStateForSelectedDocument()
    {
        if (SelectedDocument is null)
        {
            RenderEmptyState();
            return;
        }

        FilePathText.Text = SelectedDocument.FilePath;
        StatusText.Text = SelectedDocument.StatusMessage;
        Title = $"MD Translator Viewer - {SelectedDocument.DisplayName}";
        ConfigureWatcher(SelectedDocument.FilePath);
    }

    private void ConfigureWatcher(string? filePath)
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        _fileWatcher = new FileSystemWatcher(Path.GetDirectoryName(filePath)!, Path.GetFileName(filePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };

        _fileWatcher.Changed += FileWatcher_OnChanged;
        _fileWatcher.Created += FileWatcher_OnChanged;
        _fileWatcher.Deleted += FileWatcher_OnDeleted;
        _fileWatcher.Renamed += FileWatcher_OnRenamed;
    }

    private void UpdateTabsState()
    {
        var hasTabs = _openDocuments.Count > 0;
        NoTabsText.Visibility = hasTabs ? Visibility.Collapsed : Visibility.Visible;
        TabsScrollViewer.Visibility = hasTabs ? Visibility.Visible : Visibility.Collapsed;
        OverflowTabsButton.Visibility = hasTabs ? Visibility.Visible : Visibility.Hidden;
        OverflowTabsButton.IsEnabled = hasTabs;
        UpdateTabOverflowState();
        RefreshTabOverflowMenu();
    }

    private void RequestTabLayoutUpdate()
    {
        UpdateTabOverflowState();
    }

    private void UpdateTabOverflowState()
    {
        if (_updatingTabLayout || OverflowTabsButton is null || TabsScrollViewer is null)
        {
            return;
        }

        _updatingTabLayout = true;
        try
        {
            if (_openDocuments.Count == 0)
            {
                TabWidth = TabMaxWidth;
                HideTabScrollIndicator(immediate: true);
                return;
            }

            if (UsesTitleSizedTabs)
            {
                UpdateTabCloseButtonVisibility();
                UpdateTabScrollIndicatorState(revealIfEligible: TabsInteractionHost?.IsMouseOver == true);
                return;
            }

            HideTabScrollIndicator(immediate: true);

            var currentViewportWidth = ResolveCurrentTabViewportWidth();
            if (currentViewportWidth <= 0)
            {
                TabWidth = TabMaxWidth;
                return;
            }

            var targetContentWidth = (currentViewportWidth / _openDocuments.Count) - TabItemChromeWidth;
            TabWidth = Math.Clamp(targetContentWidth, TabMinWidth, TabMaxWidth);
        }
        finally
        {
            _updatingTabLayout = false;
        }
    }

    private void UpdateTabCloseButtonVisibility()
    {
        TabCloseButtonVisibility = UsesTitleSizedTabs || TabWidth >= CompactTabCloseButtonThreshold
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private double ResolveCurrentTabViewportWidth()
    {
        if (TabsScrollViewer.ActualWidth > 0)
        {
            return TabsScrollViewer.ActualWidth;
        }

        if (TabsScrollViewer.ViewportWidth > 0)
        {
            return TabsScrollViewer.ViewportWidth;
        }

        return DocumentTabsList?.ActualWidth > 0 ? DocumentTabsList.ActualWidth : 0;
    }

    private bool CanShowTabScrollIndicator()
    {
        return UsesTitleSizedTabs &&
               _openDocuments.Count > 0 &&
               TabsScrollViewer is { ScrollableWidth: > 0.5, ViewportWidth: > 0.5 };
    }

    private void UpdateTabScrollIndicatorState(bool revealIfEligible = false)
    {
        if (TabsScrollViewer is null || TabsScrollIndicator is null)
        {
            return;
        }

        _updatingTabScrollIndicator = true;
        try
        {
            TabsScrollIndicator.Minimum = 0;
            TabsScrollIndicator.Maximum = Math.Max(0, TabsScrollViewer.ScrollableWidth);
            TabsScrollIndicator.ViewportSize = Math.Max(0, TabsScrollViewer.ViewportWidth);
            TabsScrollIndicator.SmallChange = MouseWheelTabScrollStep;
            TabsScrollIndicator.LargeChange = Math.Max(MouseWheelTabScrollStep * 2, TabsScrollViewer.ViewportWidth * 0.85);
            TabsScrollIndicator.Value = Math.Clamp(
                TabsScrollViewer.HorizontalOffset,
                TabsScrollIndicator.Minimum,
                TabsScrollIndicator.Maximum);
        }
        finally
        {
            _updatingTabScrollIndicator = false;
        }

        if (!CanShowTabScrollIndicator())
        {
            HideTabScrollIndicator(immediate: true);
            return;
        }

        if (revealIfEligible || TabsInteractionHost?.IsMouseOver == true)
        {
            ShowTabScrollIndicator();
            return;
        }

        HideTabScrollIndicator();
    }

    private void ShowTabScrollIndicator()
    {
        if (TabsScrollIndicatorHost is null || !CanShowTabScrollIndicator())
        {
            HideTabScrollIndicator(immediate: true);
            return;
        }

        TabsScrollIndicatorHost.Visibility = Visibility.Visible;
        TabsScrollIndicatorHost.IsHitTestVisible = true;

        var animation = CreateTabScrollIndicatorAnimation(
            toOpacity: 1,
            EasingMode.EaseOut);
        TabsScrollIndicatorHost.BeginAnimation(
            UIElement.OpacityProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void HideTabScrollIndicator(bool immediate = false)
    {
        if (TabsScrollIndicatorHost is null)
        {
            return;
        }

        if (immediate)
        {
            TabsScrollIndicatorHost.BeginAnimation(UIElement.OpacityProperty, null);
            TabsScrollIndicatorHost.Opacity = 0;
            TabsScrollIndicatorHost.Visibility = Visibility.Collapsed;
            TabsScrollIndicatorHost.IsHitTestVisible = false;
            return;
        }

        if (TabsScrollIndicatorHost.Visibility != Visibility.Visible)
        {
            return;
        }

        TabsScrollIndicatorHost.IsHitTestVisible = false;
        var animation = CreateTabScrollIndicatorAnimation(
            toOpacity: 0,
            EasingMode.EaseIn);
        animation.Completed += (_, _) =>
        {
            if (TabsScrollIndicatorHost is null)
            {
                return;
            }

            if (TabsInteractionHost?.IsMouseOver == true && CanShowTabScrollIndicator())
            {
                TabsScrollIndicatorHost.Visibility = Visibility.Visible;
                TabsScrollIndicatorHost.IsHitTestVisible = true;
                TabsScrollIndicatorHost.Opacity = 1;
                return;
            }

            TabsScrollIndicatorHost.Visibility = Visibility.Collapsed;
        };

        TabsScrollIndicatorHost.BeginAnimation(
            UIElement.OpacityProperty,
            animation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimation CreateTabScrollIndicatorAnimation(double toOpacity, EasingMode easingMode)
    {
        return new DoubleAnimation
        {
            To = toOpacity,
            Duration = new Duration(TabScrollIndicatorFadeDuration),
            EasingFunction = new CubicEase
            {
                EasingMode = easingMode,
            },
        };
    }

    private void RefreshTabOverflowMenu()
    {
        if (TabOverflowMenu is null)
        {
            return;
        }

        TabOverflowMenu.Items.Clear();
        var menuItemStyle = (Style?)FindResource("OverflowMenuItemStyle");
        var separatorStyle = (Style?)FindResource("OverflowSeparatorStyle");

        if (_openDocuments.Count == 0)
        {
            TabOverflowMenu.Items.Add(new MenuItem
            {
                Header = "No open tabs",
                IsEnabled = false,
                Style = menuItemStyle,
            });
            return;
        }

        var closeAllItem = new MenuItem
        {
            Header = "Close all tabs",
            Style = menuItemStyle,
        };
        closeAllItem.Click += CloseAllTabsMenuItem_OnClick;
        TabOverflowMenu.Items.Add(closeAllItem);
        TabOverflowMenu.Items.Add(new Separator
        {
            Style = separatorStyle,
        });

        foreach (var document in _openDocuments)
        {
            var item = new MenuItem
            {
                Header = document.DisplayName,
                IsCheckable = true,
                IsChecked = ReferenceEquals(document, SelectedDocument),
                Tag = document,
                Style = menuItemStyle,
            };
            item.Click += OverflowTabMenuItem_OnClick;
            TabOverflowMenu.Items.Add(item);
        }
    }

    private void FileWatcher_OnChanged(object sender, FileSystemEventArgs e)
    {
        if (AutoReloadCheckBox.IsChecked != true ||
            SelectedDocument is null ||
            !string.Equals(SelectedDocument.FilePath, e.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            _reloadTimer.Stop();
            _reloadTimer.Start();
            StatusText.Text = "Change detected...";
        });
    }

    private void FileWatcher_OnRenamed(object sender, RenamedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var document = _openDocuments.FirstOrDefault(item =>
                    string.Equals(item.FilePath, e.OldFullPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.FilePath, e.FullPath, StringComparison.OrdinalIgnoreCase));

                if (document is null)
                {
                    return;
                }

                var renameOutcome = await ResolveRenameOutcomeAsync(e.OldFullPath, e.FullPath);
                if (!_openDocuments.Contains(document))
                {
                    return;
                }

                switch (renameOutcome)
                {
                    case FileRenameOutcome.ReplacedInPlace:
                        if (ReferenceEquals(SelectedDocument, document))
                        {
                            QueueSelectedDocumentReload("File replaced. Reloading...");
                        }
                        break;
                    case FileRenameOutcome.Renamed:
                        document.UpdatePath(e.FullPath);
                        if (ReferenceEquals(SelectedDocument, document))
                        {
                            UpdateWindowStateForSelectedDocument();
                        }

                        PersistSessionState();

                        if (ReferenceEquals(SelectedDocument, document) &&
                            AutoReloadCheckBox.IsChecked == true)
                        {
                            QueueSelectedDocumentReload("File renamed. Reloading...");
                        }
                        else if (ReferenceEquals(SelectedDocument, document))
                        {
                            StatusText.Text = "File renamed";
                        }
                        break;
                    default:
                        await HandleMissingDocumentAsync(document);
                        break;
                }
            }
            catch (Exception ex)
            {
                ShowFileTrackingWarning("Failed to track a renamed file.", ex);
            }
        });
    }

    private void FileWatcher_OnDeleted(object sender, FileSystemEventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var document = _openDocuments.FirstOrDefault(item =>
                    string.Equals(item.FilePath, e.FullPath, StringComparison.OrdinalIgnoreCase));

                if (document is null ||
                    !await IsDocumentStillMissingAsync(document.FilePath))
                {
                    return;
                }

                await HandleMissingDocumentAsync(document);
            }
            catch (Exception ex)
            {
                ShowFileTrackingWarning("Failed to track a deleted file.", ex);
            }
        });
    }

    private async void ReloadTimer_OnTick(object? sender, EventArgs e)
    {
        _reloadTimer.Stop();
        if (SelectedDocument is not null)
        {
            await LoadDocumentIntoTabAsync(SelectedDocument, preserveScroll: true);
        }
    }

    private async void OpenFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open Markdown File",
            Filter = "Markdown files|*.md;*.markdown;*.mdx;*.mkd|All files|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        foreach (var fileName in dialog.FileNames)
        {
            await OpenDocumentAsync(fileName);
        }
    }

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedDocument is not null)
        {
            await LoadDocumentIntoTabAsync(SelectedDocument, preserveScroll: true);
        }
    }

    private async void GoBackButton_OnClick(object sender, RoutedEventArgs e)
    {
        await NavigateDocumentSelectionHistoryAsync(-1);
    }

    private async void GoForwardButton_OnClick(object sender, RoutedEventArgs e)
    {
        await NavigateDocumentSelectionHistoryAsync(1);
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (SettingsMenu.IsOpen)
        {
            SettingsMenu.IsOpen = false;
            return;
        }

        UpdateSettingsMenuState();
        SettingsMenu.PlacementTarget = SettingsButton;
        SettingsMenu.Placement = PlacementMode.Right;
        SettingsMenu.HorizontalOffset = 8;
        SettingsMenu.VerticalOffset = -4;
        SettingsMenu.IsOpen = true;
    }

    private void SettingsMenu_OnOpened(object sender, RoutedEventArgs e)
    {
        UpdateSettingsMenuState();
    }

    private void AdaptiveTabWidthMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        SetTopTabWidthMode(TopTabWidthMode.Adaptive);
    }

    private void SizeToTitleTabWidthMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        SetTopTabWidthMode(TopTabWidthMode.SizeToTitle);
    }

    private async void DarkModernThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await SetColorThemePresetAsync(ViewerColorThemePreset.DarkModern);
    }

    private async void DarkPlusThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await SetColorThemePresetAsync(ViewerColorThemePreset.DarkPlus);
    }

    private async void LightModernThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await SetColorThemePresetAsync(ViewerColorThemePreset.LightModern);
    }

    private async void LightPlusThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await SetColorThemePresetAsync(ViewerColorThemePreset.LightPlus);
    }

    private void SetTopTabWidthMode(TopTabWidthMode mode)
    {
        if (_appState.TopTabWidthMode == mode)
        {
            UpdateSettingsMenuState();
            return;
        }

        _appState = _appState with { TopTabWidthMode = mode };
        OnPropertyChanged(nameof(TabItemWidth));
        OnPropertyChanged(nameof(TabItemMinWidth));
        OnPropertyChanged(nameof(TabItemMaxWidth));
        UpdateTabCloseButtonVisibility();
        UpdateTabOverflowState();

        if (!UsesTitleSizedTabs)
        {
            TabsScrollViewer.ScrollToHorizontalOffset(0);
        }

        BringSelectedTabIntoView(SelectedDocument);
        UpdateSettingsMenuState();
        StatusText.Text = mode == TopTabWidthMode.Adaptive
            ? "Top tabs now fill the window"
            : "Top tabs now size to their titles";
        SaveAppState();
    }

    private async Task SetColorThemePresetAsync(ViewerColorThemePreset preset)
    {
        var theme = ViewerThemeCatalog.Get(preset);
        if (_appState.ColorThemePreset == theme.Id)
        {
            UpdateSettingsMenuState();
            return;
        }

        _appState = _appState with { ColorThemePreset = theme.Id };
        ApplyThemePreset(theme);
        UpdateSettingsMenuState();
        _documentShellLoaded = false;
        await RenderSelectedDocumentAsync(preserveScroll: true);
        SaveAppState();
    }

    private void UpdateSettingsMenuState()
    {
        AdaptiveTabWidthMenuItem.Header = FormatCurrentMenuHeader(
            "Window-fit tabs",
            _appState.TopTabWidthMode == TopTabWidthMode.Adaptive);
        SizeToTitleTabWidthMenuItem.Header = FormatCurrentMenuHeader(
            "Fixed-width tabs",
            _appState.TopTabWidthMode == TopTabWidthMode.SizeToTitle);
        DarkModernThemeMenuItem.Header = FormatCurrentMenuHeader(
            "Dark Modern",
            _appState.ColorThemePreset == ViewerColorThemePreset.DarkModern);
        DarkPlusThemeMenuItem.Header = FormatCurrentMenuHeader(
            "Dark+",
            _appState.ColorThemePreset == ViewerColorThemePreset.DarkPlus);
        LightModernThemeMenuItem.Header = FormatCurrentMenuHeader(
            "Light Modern",
            _appState.ColorThemePreset == ViewerColorThemePreset.LightModern);
        LightPlusThemeMenuItem.Header = FormatCurrentMenuHeader(
            "Light+",
            _appState.ColorThemePreset == ViewerColorThemePreset.LightPlus);
    }

    private static string FormatCurrentMenuHeader(string label, bool isCurrent)
    {
        return isCurrent
            ? $"{label} (Current)"
            : label;
    }

    private void ApplyThemePreset(ViewerThemePreset theme)
    {
        foreach (var (resourceKey, colorValue) in theme.BrushColors)
        {
            Resources[resourceKey] = new SolidColorBrush(ParseCssColor(colorValue));
        }

        DocumentWebView.DefaultBackgroundColor = ToDrawingColor(ParseCssColor(theme.DocumentTheme.Background));
    }

    private static Color ParseCssColor(string colorValue)
    {
        if (!string.IsNullOrWhiteSpace(colorValue) && colorValue[0] == '#')
        {
            var hex = colorValue[1..];
            return hex.Length switch
            {
                3 => Color.FromRgb(
                    ExpandHexDigit(hex[0]),
                    ExpandHexDigit(hex[1]),
                    ExpandHexDigit(hex[2])),
                4 => Color.FromArgb(
                    ExpandHexDigit(hex[3]),
                    ExpandHexDigit(hex[0]),
                    ExpandHexDigit(hex[1]),
                    ExpandHexDigit(hex[2])),
                6 => Color.FromRgb(
                    byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                8 => Color.FromArgb(
                    byte.Parse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
                _ => throw new FormatException($"Unsupported color value '{colorValue}'."),
            };
        }

        var converted = ColorConverter.ConvertFromString(colorValue);
        if (converted is Color color)
        {
            return color;
        }

        throw new FormatException($"Unsupported color value '{colorValue}'.");
    }

    private static byte ExpandHexDigit(char digit)
    {
        var value = byte.Parse(digit.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (byte)((value << 4) | value);
    }

    private static DrawingColor ToDrawingColor(Color color)
    {
        return DrawingColor.FromArgb(color.A, color.R, color.G, color.B);
    }

    private async void TranslateToggleButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetTranslationEnabled(TranslateToggleButton.IsChecked == true);

        if (_appState.TranslateEnabled &&
            SelectedDocument is not null &&
            SelectedDocument.Markdown is not null &&
            SelectedDocument.TranslatedMarkdown is null)
        {
            SelectedDocument.SetStatus($"Loaded {SelectedDocument.DisplayName}. Translating...");
        }

        await RenderSelectedDocumentAsync(preserveScroll: true);
    }

    private void SetTranslationEnabled(bool isEnabled, bool persist = true)
    {
        TranslateToggleButton.IsChecked = isEnabled;
        _appState = _appState with { TranslateEnabled = isEnabled };

        if (!persist)
        {
            return;
        }

        SaveAppState();
    }

    private void SaveAppState()
    {
        _appStateService.Save(_appState with
        {
            TranslateEnabled = TranslateToggleButton.IsChecked == true,
            WindowPlacement = CaptureWindowPlacement(),
            OpenDocumentPaths = _openDocuments
                .Select(document => document.FilePath)
                .Where(File.Exists)
                .ToArray(),
            SelectedDocumentPath = SelectedDocument is not null && File.Exists(SelectedDocument.FilePath)
                ? SelectedDocument.FilePath
                : null,
        });
    }

    private async Task<bool> RestoreOpenTabsAsync()
    {
        var savedPaths = _appState.OpenDocumentPaths
            .Select(TryGetExistingFullPath)
            .Where(path => path is not null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (savedPaths.Length == 0)
        {
            PersistSessionState();
            return false;
        }

        _restoringOpenTabs = true;

        try
        {
            var restoredDocuments = savedPaths
                .Select(path => GetOrCreateDocumentTab(path, updateTabsState: false))
                .ToArray();

            UpdateTabsState();
            DocumentTabsList.SelectedItem = null;

            var selectedPath = TryGetExistingFullPath(_appState.SelectedDocumentPath);
            var selectedDocument = selectedPath is null
                ? restoredDocuments.FirstOrDefault()
                : restoredDocuments.FirstOrDefault(document =>
                    string.Equals(document.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));

            selectedDocument ??= restoredDocuments.FirstOrDefault();
            if (selectedDocument is null)
            {
                PersistSessionState();
                return false;
            }

            await LoadDocumentIntoTabAsync(selectedDocument);

            if (!_openDocuments.Contains(selectedDocument))
            {
                selectedDocument = restoredDocuments.FirstOrDefault(_openDocuments.Contains) ??
                                   _openDocuments.FirstOrDefault();
            }

            if (selectedDocument is null)
            {
                PersistSessionState();
                return false;
            }

            await SelectDocumentAsync(selectedDocument);
            PersistSessionState();
            _ = RestoreBackgroundTabsAsync(restoredDocuments, selectedDocument);
            return true;
        }
        finally
        {
            _restoringOpenTabs = false;
        }
    }

    private async Task RestoreBackgroundTabsAsync(
        IReadOnlyList<DocumentTab> restoredDocuments,
        DocumentTab selectedDocument)
    {
        await Task.Yield();

        foreach (var document in restoredDocuments)
        {
            if (ReferenceEquals(document, selectedDocument) ||
                !_openDocuments.Contains(document) ||
                document.Markdown is not null)
            {
                continue;
            }

            await LoadDocumentIntoTabAsync(document);
        }
    }

    private void ApplySavedWindowPlacement()
    {
        var placement = _appState.WindowPlacement;
        if (!placement.HasValue || !IsPlacementVisible(placement))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = placement.Width;
        Height = placement.Height;

        if (placement.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private WindowPlacementState CaptureWindowPlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return new WindowPlacementState
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        };
    }

    private static bool IsPlacementVisible(WindowPlacementState placement)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        var horizontalVisible = placement.Left < virtualRight && placement.Left + placement.Width > virtualLeft;
        var verticalVisible = placement.Top < virtualBottom && placement.Top + placement.Height > virtualTop;
        return horizontalVisible && verticalVisible;
    }

    private void SetLoading(bool isLoading, string? message = null)
    {
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Text = message ?? "Loading...";
    }

    private void Window_OnPreviewDragEnter(object sender, DragEventArgs e)
    {
        UpdateExternalFileDropState(e);
    }

    private void Window_OnPreviewDragOver(object sender, DragEventArgs e)
    {
        UpdateExternalFileDropState(e);
    }

    private void Window_OnPreviewDragLeave(object sender, DragEventArgs e)
    {
        HideDropOverlay();
    }

    private async void Window_OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedPaths(e.Data, out var droppedPaths))
        {
            HideDropOverlay();
            return;
        }

        HideDropOverlay();
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        var markdownPaths = CollectMarkdownPathsFromPaths(droppedPaths);
        if (markdownPaths.Count == 0)
        {
            StatusText.Text = "No Markdown files found in dropped items";
            return;
        }

        foreach (var markdownPath in markdownPaths)
        {
            await OpenDocumentAsync(markdownPath);
        }
    }

    private void UpdateExternalFileDropState(DragEventArgs e)
    {
        if (!TryGetDroppedPaths(e.Data, out var droppedPaths))
        {
            HideDropOverlay();
            return;
        }

        var hasSupportedDrop = HasSupportedMarkdownDrop(droppedPaths);
        var allowsCopy = (e.AllowedEffects & DragDropEffects.Copy) != 0;
        var showWpfOverlay = hasSupportedDrop && allowsCopy && !ReferenceEquals(e.Source, DocumentWebView);

        DropOverlay.Visibility = showWpfOverlay ? Visibility.Visible : Visibility.Collapsed;
        e.Effects = hasSupportedDrop && allowsCopy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void HideDropOverlay()
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void WarnIfDragDropBlockedByElevation()
    {
        if (_warnedAboutElevatedDragDrop || !IsRunningElevated())
        {
            return;
        }

        _warnedAboutElevatedDragDrop = true;
        StatusText.Text = "Running as administrator disables drag and drop from normal Explorer";
        MessageBox.Show(
            this,
            "This session is running as administrator.\n\nWindows blocks dragging files from a normal File Explorer window into elevated apps. Launch MD Translator Viewer normally to use drag and drop.",
            "MD Translator Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static bool HasSupportedMarkdownDrop(IDataObject dataObject)
    {
        return TryGetDroppedPaths(dataObject, out var droppedPaths) &&
               HasSupportedMarkdownDrop(droppedPaths);
    }

    private static bool HasSupportedMarkdownDrop(IEnumerable<string> droppedPaths)
    {
        foreach (var path in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            if (IsMarkdownFile(path) || Directory.Exists(path))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetDroppedMarkdownPaths(IDataObject dataObject)
    {
        if (!TryGetDroppedPaths(dataObject, out var droppedPaths))
        {
            return [];
        }

        return CollectMarkdownPathsFromPaths(droppedPaths);
    }

    private static IReadOnlyList<string> CollectMarkdownPathsFromPaths(IEnumerable<string> droppedPaths)
    {
        var markdownPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var droppedPath in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(droppedPath))
            {
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(droppedPath);
            }
            catch
            {
                continue;
            }

            if (File.Exists(fullPath))
            {
                if (IsMarkdownFile(fullPath) && seenPaths.Add(fullPath))
                {
                    markdownPaths.Add(fullPath);
                }

                continue;
            }

            if (!Directory.Exists(fullPath))
            {
                continue;
            }

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(fullPath))
                {
                    if (!IsMarkdownFile(filePath))
                    {
                        continue;
                    }

                    var markdownPath = Path.GetFullPath(filePath);
                    if (seenPaths.Add(markdownPath))
                    {
                        markdownPaths.Add(markdownPath);
                    }
                }
            }
            catch
            {
            }
        }

        return markdownPaths;
    }

    private static bool TryGetDroppedPaths(IDataObject? dataObject, out string[] droppedPaths)
    {
        droppedPaths = [];

        if (dataObject?.GetDataPresent(DataFormats.FileDrop) != true)
        {
            return false;
        }

        if (dataObject.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return false;
        }

        droppedPaths = paths;
        return true;
    }

    private static bool IsRunningElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void WebView_OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!e.IsUserInitiated || IsDocumentShellUri(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        StatusText.Text = "Blocked in-app navigation";
    }

    private void WebView_OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        StatusText.Text = "Blocked popup";
    }

    private async void WebView_OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;

            if (!IsTrustedWebMessage(root, e.Source))
            {
                return;
            }

            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            switch (typeElement.GetString())
            {
                case "open-link":
                {
                    if (!root.TryGetProperty("href", out var hrefElement))
                    {
                        return;
                    }

                    var href = hrefElement.GetString();
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        return;
                    }

                    await OpenLinkAsync(href);
                    return;
                }

                case "open-dropped-files":
                {
                    var markdownPaths = GetDroppedMarkdownPathsFromWebMessage(e, root);
                    if (markdownPaths.Count == 0)
                    {
                        StatusText.Text = "No Markdown files found in dropped items";
                        return;
                    }

                    foreach (var markdownPath in markdownPaths)
                    {
                        await OpenDocumentAsync(markdownPath);
                    }

                    return;
                }

                case "navigate-history":
                {
                    if (!root.TryGetProperty("direction", out var directionElement))
                    {
                        return;
                    }

                    var direction = directionElement.GetInt32();
                    if (direction is not -1 and not 1)
                    {
                        return;
                    }

                    await NavigateDocumentSelectionHistoryAsync(direction);
                    return;
                }

                case "translate-code-block":
                {
                    if (!root.TryGetProperty("codeBlockId", out var codeBlockIdElement) ||
                        !root.TryGetProperty("text", out var textElement))
                    {
                        return;
                    }

                    var codeBlockId = codeBlockIdElement.GetString();
                    var text = textElement.GetString();
                    var documentPath = root.TryGetProperty("documentPath", out var documentPathElement)
                        ? documentPathElement.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(codeBlockId) || string.IsNullOrWhiteSpace(text))
                    {
                        return;
                    }

                    await TranslateCodeBlockAsync(codeBlockId, text, documentPath);
                    return;
                }

                case "set-code-block-translation-enabled":
                {
                    if (!root.TryGetProperty("codeBlockId", out var codeBlockIdElement) ||
                        !root.TryGetProperty("text", out var textElement) ||
                        !root.TryGetProperty("isEnabled", out var isEnabledElement))
                    {
                        return;
                    }

                    var codeBlockId = codeBlockIdElement.GetString();
                    var text = textElement.GetString();
                    var documentPath = root.TryGetProperty("documentPath", out var documentPathElement)
                        ? documentPathElement.GetString()
                        : null;
                    var isEnabled = isEnabledElement.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => (bool?)null,
                    };

                    if (string.IsNullOrWhiteSpace(codeBlockId) ||
                        string.IsNullOrWhiteSpace(text) ||
                        string.IsNullOrWhiteSpace(documentPath) ||
                        isEnabled is null)
                    {
                        return;
                    }

                    SetCodeBlockTranslationEnabled(documentPath, codeBlockId, text, isEnabled.Value);
                    return;
                }
            }
        }
        catch
        {
        }
    }

    private bool IsTrustedWebMessage(JsonElement root, string? source)
    {
        if (!IsDocumentShellUri(source) ||
            !root.TryGetProperty("token", out var tokenElement))
        {
            return false;
        }

        return string.Equals(tokenElement.GetString(), _webViewMessageToken, StringComparison.Ordinal);
    }

    private async Task TranslateCodeBlockAsync(string codeBlockId, string text, string? documentPath)
    {
        var document = FindOpenDocument(documentPath);
        if (document is null)
        {
            return;
        }

        document.SetCodeBlockTranslationEnabled(codeBlockId, text, isEnabled: true);
        var announceStatus = ReferenceEquals(SelectedDocument, document);
        if (announceStatus)
        {
            StatusText.Text = "Translating code block...";
        }

        try
        {
            var translatedText = await _translationService.TranslatePlainTextAsync(
                text,
                TranslationLanguage,
                CancellationToken.None);

            document.StoreCodeBlockTranslation(codeBlockId, text, translatedText);
            await ApplyCodeBlockTranslationResultAsync(codeBlockId, translatedText, null, document.FilePath, isEnabled: true);
            if (ReferenceEquals(SelectedDocument, document))
            {
                StatusText.Text = "Translated code block";
            }
        }
        catch (Exception ex)
        {
            document.MarkCodeBlockTranslationFailed(codeBlockId, text);
            await ApplyCodeBlockTranslationResultAsync(codeBlockId, null, ex.Message, document.FilePath, isEnabled: false);
            if (ReferenceEquals(SelectedDocument, document))
            {
                StatusText.Text = "Code block translation failed";
            }
        }
    }

    private void SetCodeBlockTranslationEnabled(string documentPath, string codeBlockId, string text, bool isEnabled)
    {
        var document = FindOpenDocument(documentPath);
        document?.SetCodeBlockTranslationEnabled(codeBlockId, text, isEnabled);
    }

    private DocumentTab? FindOpenDocument(string? documentPath)
    {
        if (string.IsNullOrWhiteSpace(documentPath))
        {
            return null;
        }

        return _openDocuments.FirstOrDefault(document =>
            string.Equals(document.FilePath, documentPath, StringComparison.OrdinalIgnoreCase));
    }

    private async Task ApplyCodeBlockTranslationResultAsync(
        string codeBlockId,
        string? translatedText,
        string? error,
        string? documentPath,
        bool isEnabled)
    {
        if (!_documentWebViewReady ||
            DocumentWebView.CoreWebView2 is null ||
            !string.Equals(SelectedDocument?.FilePath, documentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var payloadJson = JsonSerializer.Serialize(
            new
            {
                CodeBlockId = codeBlockId,
                TranslatedText = translatedText,
                Error = error,
                IsEnabled = isEnabled,
            },
            DocumentPayloadJsonOptions);

        var script = $$"""
            (() => {
                const payload = {{payloadJson}};
                if (typeof window.mdvApplyCodeBlockTranslation !== "function") {
                    return false;
                }
                window.mdvApplyCodeBlockTranslation(payload);
                return true;
            })();
            """;

        try
        {
            await DocumentWebView.ExecuteScriptAsync(script);
        }
        catch
        {
        }
    }

    private async Task OpenLinkAsync(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            StatusText.Text = "Blocked invalid link";
            return;
        }

        if (uri.IsFile)
        {
            await OpenLocalLinkAsync(uri.LocalPath);
            return;
        }

        if (!AllowedExternalSchemes.Contains(uri.Scheme))
        {
            StatusText.Text = "Blocked unsafe link";
            return;
        }

        TryLaunchExternalTarget(uri.AbsoluteUri, "Opened external link");
    }

    private async Task OpenLocalLinkAsync(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            StatusText.Text = "Blocked invalid local link";
            return;
        }

        if (IsMarkdownFile(localPath))
        {
            if (!File.Exists(localPath))
            {
                StatusText.Text = "Linked Markdown file not found";
                return;
            }

            await OpenDocumentAsync(localPath);
            return;
        }

        if (IsBlockedLocalLink(localPath))
        {
            StatusText.Text = "Blocked unsafe local link";
            return;
        }

        if (!File.Exists(localPath) && !Directory.Exists(localPath))
        {
            StatusText.Text = "Linked file not found";
            return;
        }

        TryLaunchExternalTarget(localPath, "Opened linked file");
    }

    private void TryLaunchExternalTarget(string target, string successStatus)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });
            StatusText.Text = successStatus;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to open link";
            MessageBox.Show(
                this,
                $"Failed to open the linked target.\n\n{ex.Message}",
                "MD Translator Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static bool IsBlockedLocalLink(string localPath)
    {
        var extension = Path.GetExtension(localPath);
        return !string.IsNullOrWhiteSpace(extension) &&
               BlockedLocalLinkExtensions.Contains(extension);
    }

    private static bool IsDocumentShellUri(string? uri)
    {
        return !string.IsNullOrWhiteSpace(uri) &&
               uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetDroppedMarkdownPathsFromWebMessage(
        CoreWebView2WebMessageReceivedEventArgs e,
        JsonElement root)
    {
        var droppedPaths = new List<string>();

        foreach (var additionalObject in e.AdditionalObjects)
        {
            if (additionalObject is CoreWebView2File file &&
                !string.IsNullOrWhiteSpace(file.Path))
            {
                droppedPaths.Add(file.Path);
            }
        }

        if (root.TryGetProperty("uriList", out var uriListElement))
        {
            droppedPaths.AddRange(ParseDroppedTextPaths(uriListElement.GetString()));
        }

        if (root.TryGetProperty("plainText", out var plainTextElement))
        {
            droppedPaths.AddRange(ParseDroppedTextPaths(plainTextElement.GetString()));
        }

        return CollectMarkdownPathsFromPaths(droppedPaths);
    }

    private static IEnumerable<string> ParseDroppedTextPaths(string? droppedText)
    {
        if (string.IsNullOrWhiteSpace(droppedText))
        {
            yield break;
        }

        var separators = new[] { "\r\n", "\n", "\r" };
        foreach (var rawEntry in droppedText.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(rawEntry) || rawEntry.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (Uri.TryCreate(rawEntry, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                yield return uri.LocalPath;
                continue;
            }

            yield return rawEntry;
        }
    }

    private async void DocumentTabsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTabSelection || _restoringOpenTabs)
        {
            return;
        }

        if (sender is not ListBox listBox || listBox.SelectedItem is not DocumentTab selectedDocument)
        {
            if (_openDocuments.Count == 0)
            {
                SelectedDocument = null;
                await RenderSelectedDocumentAsync();
            }

            return;
        }

        await SelectDocumentAsync(selectedDocument, updateListSelection: false);
    }

    private async void CloseTabButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not DocumentTab document)
        {
            return;
        }

        await CloseDocumentTabAsync(document);
    }

    private async void DocumentTabItem_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: DocumentTab document })
        {
            return;
        }

        if (!ReferenceEquals(SelectedDocument, document))
        {
            await SelectDocumentAsync(document);
        }
    }

    private void DocumentTabItem_OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (!UsesTitleSizedTabs ||
            sender is not ListBoxItem ||
            e.TargetObject is not DependencyObject target)
        {
            return;
        }

        if (FindVisualParent<ListBoxItem>(target) is not null)
        {
            e.Handled = true;
        }
    }

    private void DocumentTabItem_OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem { ContextMenu: { } contextMenu })
        {
            return;
        }

        if (!ReferenceEquals(contextMenu.Tag, DocumentTabsList))
        {
            contextMenu.AddHandler(MenuItem.ClickEvent, new RoutedEventHandler(TabContextMenuItem_OnClick));
            contextMenu.Tag = DocumentTabsList;
        }
    }

    private async void TabContextMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: DocumentTab document } ||
            e.OriginalSource is not MenuItem menuItem ||
            menuItem.Tag is not string action)
        {
            return;
        }

        switch (action)
        {
            case "close-tab":
                await CloseDocumentTabAsync(document);
                break;
            case "copy-path":
                CopyDocumentPath(document);
                break;
            case "reveal-in-file-explorer":
                RevealDocumentInFileExplorer(document);
                break;
        }
    }

    private async void DocumentTabsList_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle ||
            e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var tabItem = FindVisualParent<ListBoxItem>(source);
        if (tabItem?.DataContext is not DocumentTab document)
        {
            return;
        }

        e.Handled = true;
        await CloseDocumentTabAsync(document);
    }

    private void DocumentTabsList_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            _tabDragCandidate = null;
            _tabSelectionPointerDown = false;
            return;
        }

        if (FindVisualParent<ButtonBase>(source) is not null)
        {
            _tabDragCandidate = null;
            _tabSelectionPointerDown = false;
            return;
        }

        _tabDragCandidate = FindVisualParent<ListBoxItem>(source)?.DataContext as DocumentTab;
        _tabSelectionPointerDown = _tabDragCandidate is not null;
        _tabDragStartPoint = e.GetPosition(this);
    }

    private void DocumentTabsList_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _tabDragCandidate = null;
        var hadPendingPointerSelection = _tabSelectionPointerDown;
        _tabSelectionPointerDown = false;

        if (hadPendingPointerSelection)
        {
            BringSelectedTabIntoView(SelectedDocument);
        }
    }

    private void DocumentTabsList_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isTabDragInProgress ||
            _tabDragCandidate is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - _tabDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - _tabDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _isTabDragInProgress = true;

        try
        {
            DragDrop.DoDragDrop(DocumentTabsList, new DataObject(typeof(DocumentTab), _tabDragCandidate), DragDropEffects.Move);
        }
        finally
        {
            _isTabDragInProgress = false;
            _tabDragCandidate = null;
            _tabSelectionPointerDown = false;
        }
    }

    private void DocumentTabsList_OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(DocumentTab)) ||
            e.Data.GetData(typeof(DocumentTab)) is not DocumentTab draggedDocument)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        var targetIndex = GetTabInsertIndex(e.GetPosition(DocumentTabsList), draggedDocument);
        if (targetIndex < 0)
        {
            return;
        }

        MoveTabDocument(draggedDocument, targetIndex);
    }

    private async void DocumentTabsList_OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(DocumentTab)) ||
            e.Data.GetData(typeof(DocumentTab)) is not DocumentTab draggedDocument)
        {
            return;
        }

        var targetIndex = GetTabInsertIndex(e.GetPosition(DocumentTabsList), draggedDocument);
        if (targetIndex >= 0)
        {
            MoveTabDocument(draggedDocument, targetIndex);
        }

        await SelectDocumentAsync(draggedDocument);
    }

    private void TabsScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5)
        {
            return;
        }

        RequestTabLayoutUpdate();
    }

    private void TabsInteractionHost_OnMouseEnter(object sender, MouseEventArgs e)
    {
        UpdateTabScrollIndicatorState(revealIfEligible: true);
    }

    private void TabsInteractionHost_OnMouseLeave(object sender, MouseEventArgs e)
    {
        HideTabScrollIndicator();
    }

    private void TabsScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.HorizontalChange) < 0.5 &&
            Math.Abs(e.ViewportWidthChange) < 0.5 &&
            Math.Abs(e.ExtentWidthChange) < 0.5)
        {
            return;
        }

        UpdateTabScrollIndicatorState(revealIfEligible: TabsInteractionHost?.IsMouseOver == true);
    }

    private void TabsScrollIndicator_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTabScrollIndicator || TabsScrollViewer is null || !CanShowTabScrollIndicator())
        {
            return;
        }

        var targetOffset = Math.Clamp(e.NewValue, 0, TabsScrollViewer.ScrollableWidth);
        if (Math.Abs(targetOffset - TabsScrollViewer.HorizontalOffset) < 0.5)
        {
            return;
        }

        TabsScrollViewer.ScrollToHorizontalOffset(targetOffset);
        if (TabsInteractionHost?.IsMouseOver == true)
        {
            ShowTabScrollIndicator();
        }
    }

    private void TabsScrollIndicatorHost_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabsScrollViewer_OnPreviewMouseWheel(TabsScrollViewer, e);
    }

    private void TabsScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!UsesTitleSizedTabs || TabsScrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        var offsetDelta = e.Delta > 0
            ? -MouseWheelTabScrollStep
            : MouseWheelTabScrollStep;
        var targetOffset = Math.Clamp(
            TabsScrollViewer.HorizontalOffset + offsetDelta,
            0,
            TabsScrollViewer.ScrollableWidth);

        if (Math.Abs(targetOffset - TabsScrollViewer.HorizontalOffset) < 0.5)
        {
            return;
        }

        TabsScrollViewer.ScrollToHorizontalOffset(targetOffset);
        UpdateTabScrollIndicatorState(revealIfEligible: true);
        e.Handled = true;
    }

    private void TitleBar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var pointerPosition = e.GetPosition(this);
        if (IsInteractiveTitleBarSource(source) || IsPointerInResizeBorder(pointerPosition))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        _titleBarDragPending = true;
        _titleBarDragStartPoint = e.GetPosition(this);

        if (WindowState == WindowState.Maximized)
        {
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
            _titleBarDragPending = false;
            e.Handled = true;
        }
        catch
        {
            _titleBarDragPending = false;
        }
    }

    private void TitleBar_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_titleBarDragPending || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        if (Math.Abs(currentPoint.X - _titleBarDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPoint.Y - _titleBarDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (WindowState != WindowState.Maximized)
        {
            return;
        }

        BeginDragFromMaximized(currentPoint);
        e.Handled = true;
    }

    private void TitleBar_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _titleBarDragPending = false;
    }

    private void OverflowTabsButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshTabOverflowMenu();
        TabOverflowMenu.PlacementTarget = OverflowTabsButton;
        TabOverflowMenu.Placement = PlacementMode.Bottom;
        TabOverflowMenu.IsOpen = true;
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseWindowButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        UpdateWindowControls();
    }

    private void UpdateWindowControls()
    {
        if (MaximizeRestoreButton is null)
        {
            return;
        }

        MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void UpdateWindowChromeLayout()
    {
        if (WindowContentHost is null || WindowFrameBorder is null || MaximizedDragStrip is null)
        {
            return;
        }

        var cornerRadius = WindowState == WindowState.Maximized
            ? new CornerRadius(0)
            : new CornerRadius(NormalWindowCornerRadius);
        var contentMargin = WindowState == WindowState.Maximized
            ? GetMaximizedWindowContentMargin()
            : new Thickness(0);
        var dragStripHeight = WindowState == WindowState.Maximized
            ? Math.Max(0, contentMargin.Top)
            : 0;
        var windowChrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);

        if (windowChrome is not null)
        {
            windowChrome.CornerRadius = cornerRadius;
        }

        WindowContentHost.CornerRadius = cornerRadius;
        WindowContentHost.Margin = contentMargin;
        WindowFrameBorder.CornerRadius = cornerRadius;
        WindowFrameBorder.Margin = contentMargin;
        WindowFrameBorder.BorderThickness = WindowState == WindowState.Maximized
            ? new Thickness(0)
            : new Thickness(1);
        MaximizedDragStrip.Height = dragStripHeight;
        MaximizedDragStrip.Visibility = dragStripHeight > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        MaximizedDragStrip.IsHitTestVisible = dragStripHeight > 0;
    }

    private Thickness GetMaximizedWindowContentMargin()
    {
        var windowChrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        var resizeBorder = windowChrome?.ResizeBorderThickness ?? new Thickness(
            SystemParameters.ResizeFrameVerticalBorderWidth,
            SystemParameters.ResizeFrameHorizontalBorderHeight,
            SystemParameters.ResizeFrameVerticalBorderWidth,
            SystemParameters.ResizeFrameHorizontalBorderHeight);

        return new Thickness(
            Math.Max(0, resizeBorder.Left),
            Math.Max(0, resizeBorder.Top),
            Math.Max(0, resizeBorder.Right),
            Math.Max(0, resizeBorder.Bottom));
    }

    private void BeginDragFromMaximized(Point currentPoint)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
        {
            return;
        }

        var screenPoint = PointToScreen(currentPoint);
        var screenDip = source.CompositionTarget.TransformFromDevice.Transform(screenPoint);
        var restoreBounds = RestoreBounds;
        var horizontalRatio = ActualWidth > 0
            ? Math.Clamp(_titleBarDragStartPoint.X / ActualWidth, 0.1, 0.9)
            : 0.5;

        WindowState = WindowState.Normal;
        Left = screenDip.X - (restoreBounds.Width * horizontalRatio);
        Top = Math.Max(SystemParameters.VirtualScreenTop, screenDip.Y - 14);
        _titleBarDragPending = false;

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private async void CloseAllTabsMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        foreach (var document in _openDocuments)
        {
            document.Dispose();
        }

        _openDocuments.Clear();
        _documentSelectionHistory.Clear();
        _documentSelectionHistoryIndex = -1;
        NotifyDocumentNavigationAvailabilityChanged();
        SelectedDocument = null;
        DocumentTabsList.SelectedItem = null;
        UpdateTabsState();
        await RenderSelectedDocumentAsync();
    }

    private async void OverflowTabMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem || menuItem.Tag is not DocumentTab document)
        {
            return;
        }

        await SelectDocumentAsync(document);
    }

    private async Task SelectDocumentAsync(
        DocumentTab document,
        bool updateListSelection = true,
        bool recordSelectionHistory = true)
    {
        var selectionRequestVersion = unchecked(++_selectDocumentRequestVersion);
        var previousDocument = SelectedDocument;

        if (!ReferenceEquals(previousDocument, document))
        {
            await SaveDocumentScrollSnapshotAsync(previousDocument);
            if (selectionRequestVersion != _selectDocumentRequestVersion)
            {
                return;
            }
        }

        SelectedDocument = document;

        if (updateListSelection && !ReferenceEquals(DocumentTabsList.SelectedItem, document))
        {
            _syncingTabSelection = true;
            try
            {
                DocumentTabsList.SelectedItem = document;
            }
            finally
            {
                _syncingTabSelection = false;
            }
        }

        if (recordSelectionHistory && !_navigatingDocumentSelectionHistory)
        {
            RecordDocumentSelection(document);
        }

        if (selectionRequestVersion != _selectDocumentRequestVersion)
        {
            return;
        }

        BringSelectedTabIntoView(document);
        RefreshTabOverflowMenu();

        if (await ReloadDocumentIfStaleAsync(document, preserveScroll: false))
        {
            if (selectionRequestVersion == _selectDocumentRequestVersion &&
                ReferenceEquals(SelectedDocument, document))
            {
                PersistSessionState();
            }

            return;
        }

        if (selectionRequestVersion != _selectDocumentRequestVersion ||
            !ReferenceEquals(SelectedDocument, document))
        {
            return;
        }

        await RenderSelectedDocumentAsync(
            preserveScroll: false,
            scrollSnapshotOverride: document.LastScrollSnapshot);

        if (selectionRequestVersion != _selectDocumentRequestVersion ||
            !ReferenceEquals(SelectedDocument, document))
        {
            return;
        }

        PersistSessionState();
    }

    private async Task CloseDocumentTabAsync(DocumentTab document)
    {
        var documentIndex = _openDocuments.IndexOf(document);
        if (documentIndex < 0)
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedDocument, document);
        RemoveDocumentFromSelectionHistory(document);
        document.Dispose();
        _openDocuments.Remove(document);
        UpdateTabsState();

        if (_openDocuments.Count == 0)
        {
            SelectedDocument = null;
            DocumentTabsList.SelectedItem = null;
            await RenderSelectedDocumentAsync();
            PersistSessionState();
            return;
        }

        if (!wasSelected)
        {
            RefreshTabOverflowMenu();
            PersistSessionState();
            return;
        }

        var nextIndex = Math.Clamp(documentIndex, 0, _openDocuments.Count - 1);
        await SelectDocumentAsync(_openDocuments[nextIndex]);
        PersistSessionState();
    }

    private void ComponentDispatcher_OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (handled ||
            !TryGetPointerNavigationDirection(msg, out var direction) ||
            !IsWindowMessageForThisWindow(msg.hwnd))
        {
            return;
        }

        handled = true;
        if (IsDuplicatePointerNavigationMessage(msg.time, direction))
        {
            return;
        }

        HandlePointerNavigationAsync(direction);
    }

    private async void HandlePointerNavigationAsync(int direction)
    {
        try
        {
            await NavigateDocumentSelectionHistoryAsync(direction);
        }
        catch
        {
        }
    }

    private async Task NavigateDocumentSelectionHistoryAsync(int direction)
    {
        var historyIndex = GetNavigableDocumentSelectionHistoryIndex(direction);
        if (historyIndex < 0)
        {
            return;
        }

        var document = _documentSelectionHistory[historyIndex];
        _documentSelectionHistoryIndex = historyIndex;
        NotifyDocumentNavigationAvailabilityChanged();

        if (ReferenceEquals(SelectedDocument, document))
        {
            return;
        }

        _navigatingDocumentSelectionHistory = true;
        try
        {
            await SelectDocumentAsync(document, recordSelectionHistory: false);
        }
        finally
        {
            _navigatingDocumentSelectionHistory = false;
        }
    }

    private void RecordDocumentSelection(DocumentTab document)
    {
        if (_documentSelectionHistoryIndex >= 0 &&
            _documentSelectionHistoryIndex < _documentSelectionHistory.Count &&
            ReferenceEquals(_documentSelectionHistory[_documentSelectionHistoryIndex], document))
        {
            return;
        }

        if (_documentSelectionHistoryIndex < _documentSelectionHistory.Count - 1)
        {
            _documentSelectionHistory.RemoveRange(
                _documentSelectionHistoryIndex + 1,
                _documentSelectionHistory.Count - _documentSelectionHistoryIndex - 1);
        }

        _documentSelectionHistory.Add(document);

        if (_documentSelectionHistory.Count > MaximumDocumentSelectionHistoryEntries)
        {
            var overflowCount = _documentSelectionHistory.Count - MaximumDocumentSelectionHistoryEntries;
            _documentSelectionHistory.RemoveRange(0, overflowCount);
            _documentSelectionHistoryIndex = Math.Max(_documentSelectionHistoryIndex - overflowCount, -1);
        }

        _documentSelectionHistoryIndex = _documentSelectionHistory.Count - 1;
        NotifyDocumentNavigationAvailabilityChanged();
    }

    private void RemoveDocumentFromSelectionHistory(DocumentTab document)
    {
        if (_documentSelectionHistory.Count == 0)
        {
            return;
        }

        for (var index = _documentSelectionHistory.Count - 1; index >= 0; index--)
        {
            if (!ReferenceEquals(_documentSelectionHistory[index], document))
            {
                continue;
            }

            _documentSelectionHistory.RemoveAt(index);
            if (index < _documentSelectionHistoryIndex)
            {
                _documentSelectionHistoryIndex--;
            }
            else if (index == _documentSelectionHistoryIndex)
            {
                _documentSelectionHistoryIndex = Math.Min(_documentSelectionHistoryIndex, _documentSelectionHistory.Count - 1);
            }
        }

        if (_documentSelectionHistory.Count == 0)
        {
            _documentSelectionHistoryIndex = -1;
            NotifyDocumentNavigationAvailabilityChanged();
            return;
        }

        _documentSelectionHistoryIndex = Math.Clamp(
            _documentSelectionHistoryIndex,
            0,
            _documentSelectionHistory.Count - 1);
        NotifyDocumentNavigationAvailabilityChanged();
    }

    private int GetNavigableDocumentSelectionHistoryIndex(int direction)
    {
        if (direction == 0 ||
            _documentSelectionHistory.Count == 0 ||
            _documentSelectionHistoryIndex < 0)
        {
            return -1;
        }

        var historyIndex = _documentSelectionHistoryIndex;
        while (true)
        {
            historyIndex += direction;
            if (historyIndex < 0 || historyIndex >= _documentSelectionHistory.Count)
            {
                return -1;
            }

            if (_openDocuments.Contains(_documentSelectionHistory[historyIndex]))
            {
                return historyIndex;
            }
        }
    }

    private void NotifyDocumentNavigationAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanNavigateBack));
        OnPropertyChanged(nameof(CanNavigateForward));
    }

    private bool IsWindowMessageForThisWindow(IntPtr hwnd)
    {
        return hwnd != IntPtr.Zero &&
               _windowHandle != IntPtr.Zero &&
               (hwnd == _windowHandle || NativeMethods.GetAncestor(hwnd, NativeMethods.GetAncestorRoot) == _windowHandle);
    }

    private bool IsDuplicatePointerNavigationMessage(int messageTime, int direction)
    {
        if (_lastPointerNavigationMessageTime == messageTime &&
            _lastPointerNavigationDirection == direction)
        {
            return true;
        }

        _lastPointerNavigationMessageTime = messageTime;
        _lastPointerNavigationDirection = direction;
        return false;
    }

    private static bool TryGetPointerNavigationDirection(MSG msg, out int direction)
    {
        switch ((int)msg.message)
        {
            case WindowMessageXButtonUp:
            {
                var button = GetHighWord(msg.wParam);
                if (button == XButton1)
                {
                    direction = -1;
                    return true;
                }

                if (button == XButton2)
                {
                    direction = 1;
                    return true;
                }

                break;
            }
            case WindowMessageAppCommand:
            {
                var appCommand = GetHighWord(msg.lParam) & ~AppCommandDeviceMask;
                if (appCommand == AppCommandBrowserBackward)
                {
                    direction = -1;
                    return true;
                }

                if (appCommand == AppCommandBrowserForward)
                {
                    direction = 1;
                    return true;
                }

                break;
            }
        }

        direction = 0;
        return false;
    }

    private static int GetHighWord(nint value)
    {
        return (int)((value.ToInt64() >> 16) & 0xFFFF);
    }

    private void BringSelectedTabIntoView(DocumentTab? document)
    {
        if (document is null ||
            _tabSelectionPointerDown ||
            !UsesTitleSizedTabs ||
            TabsScrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (DocumentTabsList.ItemContainerGenerator.ContainerFromItem(document) is not FrameworkElement element)
            {
                return;
            }

            var bounds = element.TransformToAncestor(DocumentTabsList)
                .TransformBounds(new Rect(new Point(), element.RenderSize));
            var viewportLeft = TabsScrollViewer.HorizontalOffset;
            var viewportRight = viewportLeft + TabsScrollViewer.ViewportWidth;
            const double viewportMargin = 12;
            double? targetOffset = null;

            if (bounds.Left < viewportLeft + viewportMargin)
            {
                targetOffset = Math.Max(0, bounds.Left - viewportMargin);
            }
            else if (bounds.Right > viewportRight - viewportMargin)
            {
                targetOffset = Math.Min(
                    TabsScrollViewer.ScrollableWidth,
                    bounds.Right - TabsScrollViewer.ViewportWidth + viewportMargin);
            }

            if (targetOffset is double offset &&
                Math.Abs(offset - TabsScrollViewer.HorizontalOffset) >= 0.5)
            {
                TabsScrollViewer.ScrollToHorizontalOffset(offset);
            }
        }, DispatcherPriority.Background);
    }

    private int GetTabInsertIndex(Point position, DocumentTab draggedDocument)
    {
        for (var index = 0; index < _openDocuments.Count; index++)
        {
            var document = _openDocuments[index];
            if (DocumentTabsList.ItemContainerGenerator.ContainerFromItem(document) is not FrameworkElement item)
            {
                continue;
            }

            var bounds = item.TransformToAncestor(DocumentTabsList)
                .TransformBounds(new Rect(new Point(), item.RenderSize));

            if (position.X <= bounds.Left + (bounds.Width / 2))
            {
                return index;
            }

            if (position.X <= bounds.Right)
            {
                return index + 1;
            }
        }

        return _openDocuments.Count;
    }

    private void MoveTabDocument(DocumentTab document, int targetIndex)
    {
        var currentIndex = _openDocuments.IndexOf(document);
        if (currentIndex < 0)
        {
            return;
        }

        targetIndex = Math.Clamp(targetIndex, 0, _openDocuments.Count);
        if (targetIndex > currentIndex)
        {
            targetIndex--;
        }

        if (currentIndex == targetIndex)
        {
            return;
        }

        _openDocuments.Move(currentIndex, targetIndex);
        RefreshTabOverflowMenu();
        BringSelectedTabIntoView(document);
    }

    private static bool IsInteractiveTitleBarSource(DependencyObject source)
    {
        return FindVisualParent<ButtonBase>(source) is not null ||
               FindVisualParent<ListBoxItem>(source) is not null ||
               FindVisualParent<ScrollBar>(source) is not null;
    }

    private bool IsPointerInResizeBorder(Point position)
    {
        if (WindowState != WindowState.Normal)
        {
            return false;
        }

        var windowChrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
        var resizeBorder = windowChrome?.ResizeBorderThickness ?? new Thickness(
            SystemParameters.ResizeFrameVerticalBorderWidth,
            SystemParameters.ResizeFrameHorizontalBorderHeight,
            SystemParameters.ResizeFrameVerticalBorderWidth,
            SystemParameters.ResizeFrameHorizontalBorderHeight);

        return position.X <= resizeBorder.Left ||
               position.X >= ActualWidth - resizeBorder.Right ||
               position.Y <= resizeBorder.Top;
    }

    private void CopyDocumentPath(DocumentTab document)
    {
        try
        {
            Clipboard.SetText(document.FilePath);
            StatusText.Text = "Path copied";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Failed to copy the path.\n\n{ex.Message}",
                "MD Translator Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RevealDocumentInFileExplorer(DocumentTab document)
    {
        if (!File.Exists(document.FilePath))
        {
            MessageBox.Show(
                this,
                $"File not found:\n{document.FilePath}",
                "MD Translator Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{document.FilePath}\"")
            {
                UseShellExecute = true,
            });
            StatusText.Text = "Opened in File Explorer";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Failed to open File Explorer.\n\n{ex.Message}",
                "MD Translator Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }
            child = VisualTreeHelper.GetParent(child);
        }

        return null;
    }

    private static bool IsMarkdownFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".md" or ".markdown" or ".mdx" or ".mkd";
    }

    private async Task SaveDocumentScrollSnapshotAsync(DocumentTab? document)
    {
        if (document is null ||
            !_documentWebViewReady ||
            DocumentWebView.CoreWebView2 is null)
        {
            return;
        }

        var snapshot = await CaptureScrollSnapshotAsync();
        if (!ReferenceEquals(SelectedDocument, document) ||
            !_openDocuments.Contains(document))
        {
            return;
        }

        document.LastScrollSnapshot = snapshot;
    }

    private async Task<ScrollSnapshot?> CaptureScrollSnapshotAsync()
    {
        if (!_documentWebViewReady || DocumentWebView.CoreWebView2 is null)
        {
            return null;
        }

        try
        {
            var result = await DocumentWebView.ExecuteScriptAsync("""
                (() => {
                    const assignBlockIndices = () => {
                        const blocks = Array.from(
                            document.querySelectorAll(
                                "main.page :is(h1, h2, h3, h4, h5, h6, p, li, pre, table, blockquote, hr)"
                            )
                        );

                        blocks.forEach((block, index) => {
                            block.dataset.mdvBlockIndex = String(index);
                        });

                        return blocks;
                    };

                    const doc = document.documentElement;
                    const body = document.body;
                    const scrollTop = window.scrollY || doc.scrollTop || body.scrollTop || 0;
                    const scrollHeight = Math.max(doc.scrollHeight || 0, body.scrollHeight || 0);
                    const clientHeight = window.innerHeight || doc.clientHeight || 0;
                    const blocks = assignBlockIndices();

                    let anchorBlockIndex = null;
                    let anchorOffset = 0;
                    for (let index = 0; index < blocks.length; index += 1) {
                        const rect = blocks[index].getBoundingClientRect();
                        if (rect.bottom > 0) {
                            anchorBlockIndex = index;
                            anchorOffset = rect.top;
                            break;
                        }
                    }

                    return {
                        ScrollTop: scrollTop,
                        ScrollHeight: scrollHeight,
                        ClientHeight: clientHeight,
                        AnchorBlockIndex: anchorBlockIndex,
                        AnchorOffset: anchorOffset
                    };
                })();
                """);

            var snapshot = JsonSerializer.Deserialize<ScrollSnapshot>(result, ScrollSnapshotJsonOptions);
            if (snapshot is null)
            {
                return null;
            }

            var maxScroll = Math.Max(snapshot.ScrollHeight - snapshot.ClientHeight, 1d);
            return snapshot with
            {
                ScrollRatio = snapshot.ScrollTop / maxScroll,
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task RestoreScrollSnapshotAsync(ScrollSnapshot? snapshot)
    {
        if (snapshot is null || DocumentWebView.CoreWebView2 is null)
        {
            return;
        }

        var ratio = Math.Clamp(snapshot.ScrollRatio, 0d, 1d);
        var ratioText = ratio.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var anchorBlockIndexText = snapshot.AnchorBlockIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        var anchorOffsetText = snapshot.AnchorOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var script = $$"""
            (() => new Promise(resolve => {
                const restore = () => {
                    const blocks = Array.from(
                        document.querySelectorAll(
                            "main.page :is(h1, h2, h3, h4, h5, h6, p, li, pre, table, blockquote, hr)"
                        )
                    );

                    blocks.forEach((block, index) => {
                        block.dataset.mdvBlockIndex = String(index);
                    });

                    const anchorBlockIndex = {{anchorBlockIndexText}};
                    const anchorOffset = {{anchorOffsetText}};
                    if (anchorBlockIndex !== null) {
                        const anchor = blocks[anchorBlockIndex];
                        if (anchor) {
                            const rect = anchor.getBoundingClientRect();
                            window.scrollTo(0, window.scrollY + rect.top - anchorOffset);
                            resolve(true);
                            return;
                        }
                    }

                    const doc = document.documentElement;
                    const body = document.body;
                    const maxScroll = Math.max(
                        0,
                        Math.max(doc.scrollHeight || 0, body.scrollHeight || 0) -
                        (window.innerHeight || doc.clientHeight || 0)
                    );

                    window.scrollTo(0, maxScroll * {{ratioText}});
                    resolve(true);
                };

                requestAnimationFrame(() => requestAnimationFrame(restore));
            }))();
            """;

        try
        {
            await DocumentWebView.ExecuteScriptAsync(script);
        }
        catch
        {
        }
    }

    private async Task WaitForDocumentShellReadyAsync()
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await IsDocumentShellReadyAsync())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException("The document shell did not finish initializing.");
    }

    private async Task<bool> IsDocumentShellReadyAsync()
    {
        if (!_documentWebViewReady || DocumentWebView.CoreWebView2 is null)
        {
            return false;
        }

        try
        {
            var result = await DocumentWebView.ExecuteScriptAsync("""
                (() => {
                    return typeof window.mdvRenderDocument === "function" &&
                        document.getElementById("mdv-content") !== null &&
                        document.getElementById("mdv-base") !== null;
                })();
                """);

            return string.Equals(result, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task HandleMissingDocumentAsync(DocumentTab document)
    {
        var documentPath = document.FilePath;

        if (_openDocuments.Contains(document))
        {
            await CloseDocumentTabAsync(document);
        }
        else if (ReferenceEquals(SelectedDocument, document))
        {
            SelectedDocument = null;
            await RenderSelectedDocumentAsync();
            PersistSessionState();
        }

        MessageBox.Show(
            this,
            $"The document is no longer available and was removed from the viewer.\n\n{documentPath}",
            "MD Translator Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void QueueSelectedDocumentReload(string statusText)
    {
        if (SelectedDocument is null || AutoReloadCheckBox.IsChecked != true)
        {
            return;
        }

        _reloadTimer.Stop();
        _reloadTimer.Start();
        StatusText.Text = statusText;
    }

    private async void MainWindow_OnActivated(object? sender, EventArgs e)
    {
        await ReloadDocumentIfStaleAsync(SelectedDocument, preserveScroll: true);
    }

    private async Task<bool> ReloadDocumentIfStaleAsync(DocumentTab? document, bool preserveScroll)
    {
        if (document is null ||
            document.Markdown is null ||
            !document.HasChangedOnDisk(TryGetFileContentVersion(document.FilePath)))
        {
            return false;
        }

        await LoadDocumentIntoTabAsync(document, preserveScroll);
        return true;
    }

    private static FileContentVersion? TryGetFileContentVersion(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return null;
            }

            return new FileContentVersion(
                fileInfo.LastWriteTimeUtc.Ticks,
                fileInfo.Length);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> IsDocumentStillMissingAsync(string path, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= FileReadRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                return false;
            }

            if (attempt < FileReadRetryCount)
            {
                await Task.Delay(FileReadRetryDelay, cancellationToken);
            }
        }

        return !File.Exists(path);
    }

    private static async Task<FileRenameOutcome> ResolveRenameOutcomeAsync(
        string originalPath,
        string renamedPath,
        CancellationToken cancellationToken = default)
    {
        var latestOutcome = FileRenameOutcome.Missing;

        for (var attempt = 1; attempt <= FileReadRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(originalPath))
            {
                return FileRenameOutcome.ReplacedInPlace;
            }

            if (File.Exists(renamedPath))
            {
                latestOutcome = FileRenameOutcome.Renamed;
            }

            if (attempt < FileReadRetryCount)
            {
                await Task.Delay(FileReadRetryDelay, cancellationToken);
            }
        }

        return latestOutcome;
    }

    private void ShowFileTrackingWarning(string message, Exception ex)
    {
        StatusText.Text = "File watch failed";
        MessageBox.Show(
            this,
            $"{message}\n\n{ex.Message}",
            "MD Translator Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static async Task<string> ReadMarkdownFileAsync(string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= FileReadRetryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    options: FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
                return await reader.ReadToEndAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException &&
                attempt < FileReadRetryCount)
            {
                await Task.Delay(FileReadRetryDelay, cancellationToken);
            }
        }

        throw new IOException($"Failed to read the document after {FileReadRetryCount} attempts.");
    }

    private void PersistSessionState()
    {
        if (!IsLoaded || TranslateToggleButton is null)
        {
            return;
        }

        SaveAppState();
    }

    private static string? TryGetExistingFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath)
                ? fullPath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void CleanupStaleWebViewUserData()
    {
        try
        {
            var sessionsRoot = Path.GetDirectoryName(_webViewUserDataFolder);
            if (string.IsNullOrWhiteSpace(sessionsRoot))
            {
                return;
            }

            Directory.CreateDirectory(sessionsRoot);
            var staleBefore = DateTime.UtcNow.AddDays(-2);
            foreach (var directory in Directory.EnumerateDirectories(sessionsRoot))
            {
                try
                {
                    var info = new DirectoryInfo(directory);
                    if (string.Equals(info.FullName, _webViewUserDataFolder, StringComparison.OrdinalIgnoreCase) ||
                        info.LastWriteTimeUtc >= staleBefore)
                    {
                        continue;
                    }

                    info.Delete(recursive: true);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private void TryDeleteWebViewUserData()
    {
        try
        {
            if (Directory.Exists(_webViewUserDataFolder))
            {
                Directory.Delete(_webViewUserDataFolder, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static Task WaitForNavigationAsync(Microsoft.Web.WebView2.Wpf.WebView2 webView)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            webView.NavigationCompleted -= Handler;
            tcs.TrySetResult();
        }

        webView.NavigationCompleted += Handler;
        return tcs.Task;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal static class NativeMethods
{
    internal const uint GetAncestorRoot = 2;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}

internal sealed record ScrollSnapshot
{
    public double ScrollTop { get; init; }

    public double ScrollHeight { get; init; }

    public double ClientHeight { get; init; }

    public double ScrollRatio { get; init; }

    public int? AnchorBlockIndex { get; init; }

    public double AnchorOffset { get; init; }
}

internal enum FileRenameOutcome
{
    Missing,
    ReplacedInPlace,
    Renamed,
}

public sealed class DocumentTab(string filePath) : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource? _loadCts;
    private readonly Dictionary<string, CodeBlockTranslationMemory> _codeBlockTranslations = new(StringComparer.Ordinal);
    private string _filePath = Path.GetFullPath(filePath);
    private string _displayName = Path.GetFileName(filePath);
    private FileContentVersion? _loadedFileContentVersion;
    private string? _markdown;
    private string? _translatedMarkdown;
    private string _statusMessage = "Ready";
    private bool _translationFailed;
    private bool _translationInProgress;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FilePath
    {
        get => _filePath;
        private set
        {
            if (string.Equals(_filePath, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _filePath = value;
            OnPropertyChanged();
        }
    }

    public string DisplayName
    {
        get => _displayName;
        private set
        {
            if (_displayName == value)
            {
                return;
            }

            _displayName = value;
            OnPropertyChanged();
        }
    }

    public string? Markdown
    {
        get => _markdown;
        private set
        {
            if (_markdown == value)
            {
                return;
            }

            _markdown = value;
            OnPropertyChanged();
        }
    }

    public string? TranslatedMarkdown
    {
        get => _translatedMarkdown;
        private set
        {
            if (_translatedMarkdown == value)
            {
                return;
            }

            _translatedMarkdown = value;
            OnPropertyChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public bool TranslationFailed
    {
        get => _translationFailed;
        private set
        {
            if (_translationFailed == value)
            {
                return;
            }

            _translationFailed = value;
            OnPropertyChanged();
        }
    }

    public int LoadVersion { get; private set; }

    internal ScrollSnapshot? LastScrollSnapshot { get; set; }

    public (int LoadVersion, CancellationToken CancellationToken) BeginLoad()
    {
        CancelPendingLoad();
        _loadCts = new CancellationTokenSource();
        LoadVersion++;
        StatusMessage = "Loading document...";
        TranslationFailed = false;
        TranslatedMarkdown = null;
        _translationInProgress = false;
        return (LoadVersion, _loadCts.Token);
    }

    public bool TryBeginTranslation(
        bool updateStatus,
        out int loadVersion,
        out CancellationToken cancellationToken,
        out string? markdown)
    {
        loadVersion = LoadVersion;
        cancellationToken = _loadCts?.Token ?? CancellationToken.None;
        markdown = Markdown;

        if (string.IsNullOrWhiteSpace(markdown) ||
            _loadCts is null ||
            _translationInProgress ||
            TranslatedMarkdown is not null)
        {
            return false;
        }

        _translationInProgress = true;
        TranslationFailed = false;
        if (updateStatus)
        {
            StatusMessage = $"Loaded {DisplayName}. Translating...";
        }

        return true;
    }

    public bool IsCurrentLoad(int version)
    {
        return version == LoadVersion;
    }

    internal void MarkLoaded(string markdown, FileContentVersion? fileContentVersion)
    {
        Markdown = markdown;
        _loadedFileContentVersion = fileContentVersion;
        TranslationFailed = false;
        TranslatedMarkdown = null;
        _translationInProgress = false;
    }

    internal bool HasChangedOnDisk(FileContentVersion? currentFileContentVersion)
    {
        if (Markdown is null)
        {
            return true;
        }

        return _loadedFileContentVersion != currentFileContentVersion;
    }

    public void MarkTranslated(string translatedMarkdown, bool updateStatus = true)
    {
        TranslatedMarkdown = translatedMarkdown;
        TranslationFailed = false;
        _translationInProgress = false;

        if (updateStatus)
        {
            StatusMessage = $"Loaded {DisplayName} (translated)";
        }
    }

    public void MarkTranslationFailed(bool updateStatus = true)
    {
        TranslationFailed = true;
        _translationInProgress = false;

        if (updateStatus)
        {
            StatusMessage = "Translation failed";
        }
    }

    public void FinishTranslationSkipped()
    {
        _translationInProgress = false;
    }

    public void MarkLoadFailed()
    {
        StatusMessage = "Load failed";
        _translationInProgress = false;
    }

    public void SetStatus(string statusMessage)
    {
        StatusMessage = statusMessage;
    }

    public void UpdatePath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        FilePath = fullPath;
        DisplayName = Path.GetFileName(fullPath);
        _loadedFileContentVersion = null;
    }

    internal IReadOnlyList<CodeBlockTranslationPayload> GetCodeBlockTranslationPayloads()
    {
        if (_codeBlockTranslations.Count == 0)
        {
            return Array.Empty<CodeBlockTranslationPayload>();
        }

        return _codeBlockTranslations
            .Select(static entry => new CodeBlockTranslationPayload
            {
                CodeBlockId = entry.Key,
                OriginalText = entry.Value.OriginalText,
                TranslatedText = entry.Value.TranslatedText,
                IsEnabled = entry.Value.IsEnabled,
            })
            .ToArray();
    }

    internal void SetCodeBlockTranslationEnabled(string codeBlockId, string originalText, bool isEnabled)
    {
        if (string.IsNullOrWhiteSpace(codeBlockId) || string.IsNullOrWhiteSpace(originalText))
        {
            return;
        }

        var state = GetOrCreateCodeBlockTranslationMemory(codeBlockId, originalText);
        state.IsEnabled = isEnabled;
    }

    internal void StoreCodeBlockTranslation(string codeBlockId, string originalText, string translatedText)
    {
        if (string.IsNullOrWhiteSpace(codeBlockId) ||
            string.IsNullOrWhiteSpace(originalText) ||
            translatedText is null)
        {
            return;
        }

        var state = GetOrCreateCodeBlockTranslationMemory(codeBlockId, originalText);
        state.TranslatedText = NormalizeCodeBlockText(translatedText);
        state.IsEnabled = true;
    }

    internal void MarkCodeBlockTranslationFailed(string codeBlockId, string originalText)
    {
        if (string.IsNullOrWhiteSpace(codeBlockId) || string.IsNullOrWhiteSpace(originalText))
        {
            return;
        }

        var state = GetOrCreateCodeBlockTranslationMemory(codeBlockId, originalText);
        state.IsEnabled = false;
    }

    public void Dispose()
    {
        CancelPendingLoad();
    }

    private void CancelPendingLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private CodeBlockTranslationMemory GetOrCreateCodeBlockTranslationMemory(string codeBlockId, string originalText)
    {
        var normalizedOriginalText = NormalizeCodeBlockText(originalText);
        if (!_codeBlockTranslations.TryGetValue(codeBlockId, out var state) ||
            !string.Equals(state.OriginalText, normalizedOriginalText, StringComparison.Ordinal))
        {
            state = new CodeBlockTranslationMemory
            {
                OriginalText = normalizedOriginalText,
            };
            _codeBlockTranslations[codeBlockId] = state;
        }

        return state;
    }

    private static string NormalizeCodeBlockText(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class CodeBlockTranslationMemory
{
    public string OriginalText { get; init; } = string.Empty;

    public string? TranslatedText { get; set; }

    public bool IsEnabled { get; set; }
}

internal readonly record struct FileContentVersion(long LastWriteTimeUtcTicks, long Length);
