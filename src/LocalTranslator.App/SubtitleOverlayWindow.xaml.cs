using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LocalTranslator.Core.Models;

namespace LocalTranslator.App;

public partial class SubtitleOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WmHotkey = 0x0312;
    private const int ToggleClickThroughHotkeyId = 0x4C54;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkF8 = 0x77;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const double CollapsedHeight = 150;
    private const double ExpandedHeight = 465;

    private double _bottomOffset;
    private bool _interactionEnabled;
    private bool _showSourceText = true;
    private bool _expanded;
    private int _fontPreset = 1;
    private IntPtr _windowHandle;
    private HwndSource? _windowSource;
    private bool _isResizing;
    private double _sourceFontSize;
    private double _translationFontSize;
    private double _maxTextWidth = 820;
    private bool _isFullyTransparent;
    private readonly Effect? _subtitlePanelEffect;
    private SupportedLanguage _targetLanguage = SupportedLanguage.ChineseSimplified;
    private string _targetLanguageLabel = SupportedLanguage.ChineseSimplified.ToDisplayName();
    private readonly ObservableCollection<OverlaySubtitleLine> _subtitleLines = [];

    public SubtitleOverlayWindow(
        double sourceFontSize,
        double translationFontSize,
        double bottomOffset,
        double overlayWidth,
        double overlayHeight,
        double? overlayLeft,
        double? overlayTop,
        bool interactionEnabled,
        double backgroundOpacity,
        SupportedLanguage targetLanguage)
    {
        InitializeComponent();
        _subtitlePanelEffect = SubtitlePanel.Effect;
        Height = Math.Clamp(overlayHeight, MinHeight, MaxHeight);
        _expanded = Height > 240;
        _interactionEnabled = interactionEnabled;
        SetTargetLanguage(targetLanguage);
        ShowInTaskbar = false;
        SubtitleItemsControl.ItemsSource = _subtitleLines;
        RefreshEmptyState();
        ApplyLayout(sourceFontSize, translationFontSize, bottomOffset, overlayWidth, false);
        ApplyBackgroundOpacity(backgroundOpacity);
        RefreshToolbarText();

        SourceInitialized += Window_SourceInitialized;
        Loaded += (_, _) =>
        {
            if (overlayLeft is not null && overlayTop is not null)
            {
                Left = Clamp(overlayLeft.Value, SystemParameters.VirtualScreenLeft,
                    SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth);
                Top = Clamp(overlayTop.Value, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - ActualHeight);
            }
            else
            {
                UpdateDefaultPosition();
            }

            ApplyClickThrough();
        };
        Closed += Window_Closed;
    }

    public event EventHandler<SubtitleOverlayPlacement>? PlacementChanged;
    public event EventHandler<bool>? InteractionModeChanged;
    public event EventHandler<SubtitleFontSizeChanged>? FontSizeChanged;
    public event EventHandler<SupportedLanguage>? TargetLanguageChanged;
    public event EventHandler? CloseRequested;

    public bool IsClickThrough => !_interactionEnabled;

    public void ApplyBackgroundOpacity(double opacity)
    {
        var value = Math.Clamp(opacity, 0, 0.92);
        _isFullyTransparent = value <= 0.001;
        SubtitlePanelTopStop.Color = Color.FromArgb(
            (byte)Math.Round(value * 255), 0, 0, 0);
        SubtitlePanelBottomStop.Color = Color.FromArgb(
            (byte)Math.Round(Math.Max(0, value - 0.12) * 255), 0, 0, 0);

        // A transparent subtitle window is still an interactive subtitle window.
        // Mouse pass-through remains a separate explicit mode (Ctrl+Shift+F8).
        if (_isFullyTransparent && !_interactionEnabled)
        {
            _interactionEnabled = true;
            ApplyClickThrough();
            InteractionModeChanged?.Invoke(this, true);
        }

        ApplyTransparentChromeMode();
    }

    private void ApplyTransparentChromeMode()
    {
        var chromeVisibility = _isFullyTransparent ? Visibility.Collapsed : Visibility.Visible;
        // A one-alpha hit-test layer is visually indistinguishable from full transparency,
        // but prevents the layered HWND from discarding mouse input on empty pixels.
        InteractionSurface.Background = _isFullyTransparent
            ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0))
            : Brushes.Transparent;
        ToolbarPanel.Visibility = chromeVisibility;
        ToolbarDivider.Visibility = chromeVisibility;
        InteractionHint.Visibility = chromeVisibility;
        ResizeThumb.Visibility = chromeVisibility;
        SubtitlePanel.Effect = _isFullyTransparent ? null : _subtitlePanelEffect;
        SubtitleScrollViewer.VerticalScrollBarVisibility = _isFullyTransparent
            ? ScrollBarVisibility.Hidden
            : ScrollBarVisibility.Auto;
        RefreshResizeInteraction();
    }

    public void ApplyLayout(
        double sourceFontSize,
        double translationFontSize,
        double bottomOffset,
        double overlayWidth,
        bool resetToBottom)
    {
        _sourceFontSize = Math.Clamp(sourceFontSize, 12, 32);
        _translationFontSize = Math.Clamp(translationFontSize, 16, 42);
        Width = Math.Clamp(overlayWidth, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        _maxTextWidth = Math.Max(240, Width - 42);
        RefreshLineStyles();
        _bottomOffset = Math.Clamp(bottomOffset, 0, Math.Max(0, SystemParameters.WorkArea.Height - 120));
        if (resetToBottom) UpdateDefaultPosition();
    }

    public void SetInteractionEnabled(bool enabled)
    {
        if (_interactionEnabled == enabled) return;
        _interactionEnabled = enabled;
        ApplyClickThrough();
        InteractionModeChanged?.Invoke(this, enabled);
    }

    public void ToggleClickThrough() => SetInteractionEnabled(!_interactionEnabled);

    public void ResetPosition()
    {
        UpdateDefaultPosition();
        RaisePlacementChanged();
    }

    public void SetTargetLanguage(SupportedLanguage targetLanguage)
    {
        _targetLanguage = targetLanguage;
        _targetLanguageLabel = targetLanguage.ToDisplayName();
        if (IsInitialized) RefreshToolbarText();
    }

    public void ShowSource(SubtitleSegment segment, bool bilingual)
    {
        AppendOrUpdateSource(segment, bilingual);
        UpdateTaskbarTitle(segment.SourceText);
    }

    public void UpdateTranslationStream(string partialTranslation)
    {
        var line = _subtitleLines.LastOrDefault();
        if (line is not null)
        {
            line.TranslationText = string.IsNullOrWhiteSpace(partialTranslation)
                ? "正在翻译…"
                : SubtitleTextFormatter.FormatForDisplay(partialTranslation);
            line.IsPending = false;
            ApplyLineStyle(line, false);
        }
        UpdateTaskbarTitle(partialTranslation);
        ScrollToLatestSubtitle();
    }

    public void ShowSegment(SubtitleSegment segment, bool bilingual)
    {
        var translation = string.IsNullOrWhiteSpace(segment.TranslatedText)
            ? "正在翻译…"
            : SubtitleTextFormatter.FormatForDisplay(segment.TranslatedText);
        AppendOrUpdateTranslation(segment, translation, bilingual);
        UpdateTaskbarTitle(translation);
        ScrollToLatestSubtitle();
    }

    private void AppendOrUpdateSource(SubtitleSegment segment, bool bilingual)
    {
        var sourceText = segment.SourceText;
        var normalizedSource = NormalizeText(sourceText);
        var line = segment.Sequence > 0
            ? _subtitleLines.LastOrDefault(item => item.Sequence == segment.Sequence)
            : _subtitleLines.LastOrDefault(item =>
                NormalizeText(item.RawSourceText).Equals(normalizedSource, StringComparison.OrdinalIgnoreCase));
        var isNewLine = line is null;
        if (line is null)
        {
            line = new OverlaySubtitleLine { Sequence = segment.Sequence };
            _subtitleLines.Add(line);
        }

        line.RawSourceText = sourceText;
        var existingTranslationDuplicatesSource =
            !string.IsNullOrWhiteSpace(line.TranslationText) &&
            TranslationOutputValidator.AreEquivalent(sourceText, line.TranslationText);
        var showSource = bilingual && _showSourceText && !existingTranslationDuplicatesSource;
        line.SourceText = showSource ? SubtitleTextFormatter.FormatForDisplay(sourceText) : string.Empty;
        line.SourceVisibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        // Keep the latest stable preview while the same ASR sentence grows. Clearing it
        // on every partial result caused a screen full of duplicated "正在翻译…" rows.
        if (isNewLine || string.IsNullOrWhiteSpace(line.TranslationText))
            line.TranslationText = "正在翻译…";
        line.IsPending = true;
        ApplyLineStyle(line, true);
        TrimSubtitleLines();
        RefreshEmptyState();
        ScrollToLatestSubtitle();
    }

    private void AppendOrUpdateTranslation(SubtitleSegment segment, string translation, bool bilingual)
    {
        var sourceText = segment.SourceText;
        var normalizedSource = NormalizeText(sourceText);
        var line = segment.Sequence > 0
            ? _subtitleLines.LastOrDefault(item => item.Sequence == segment.Sequence)
            : _subtitleLines.LastOrDefault(item =>
                  NormalizeText(item.RawSourceText).Equals(normalizedSource, StringComparison.OrdinalIgnoreCase))
              ?? _subtitleLines.LastOrDefault(item => item.IsPending);
        if (line is null)
        {
            line = new OverlaySubtitleLine { Sequence = segment.Sequence };
            _subtitleLines.Add(line);
        }

        line.RawSourceText = sourceText;
        var showSource = bilingual && _showSourceText &&
                         !TranslationOutputValidator.AreEquivalent(sourceText, translation);
        line.SourceText = showSource ? SubtitleTextFormatter.FormatForDisplay(sourceText) : string.Empty;
        line.SourceVisibility = showSource ? Visibility.Visible : Visibility.Collapsed;
        line.TranslationText = SubtitleTextFormatter.FormatForDisplay(translation);
        line.IsPending = false;
        ApplyLineStyle(line, false);
        TrimSubtitleLines();
        RefreshEmptyState();
        ScrollToLatestSubtitle();
    }

    private void TrimSubtitleLines()
    {
        var maximumLines = _expanded ? 24 : 6;
        while (_subtitleLines.Count > maximumLines)
            _subtitleLines.RemoveAt(0);
        RefreshLineStyles();
        RefreshEmptyState();
    }

    private void RefreshEmptyState()
    {
        if (!IsInitialized) return;
        EmptyStateText.Visibility = _subtitleLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyLineStyle(OverlaySubtitleLine line, bool pending)
    {
        var isLatest = ReferenceEquals(line, _subtitleLines.LastOrDefault());
        line.SourceFontSize = isLatest ? _sourceFontSize : Math.Max(12, _sourceFontSize - 1);
        line.TranslationFontSize = isLatest ? _translationFontSize : Math.Max(16, _translationFontSize - 2);
        line.SourceLineHeight = line.SourceFontSize + 6;
        line.TranslationLineHeight = line.TranslationFontSize + 8;
        line.TranslationOpacity = pending ? 0.66 : isLatest ? 1 : 0.72;
        line.SourceOpacity = isLatest ? 0.74 : 0.52;
        line.TranslationFontWeight = isLatest ? FontWeights.SemiBold : FontWeights.Normal;
        line.MaxTextWidth = _maxTextWidth;
    }

    private void RefreshLineStyles()
    {
        foreach (var line in _subtitleLines)
            ApplyLineStyle(line, line.IsPending);
    }

    private static string NormalizeText(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(_windowHandle);
        _windowSource?.AddHook(WindowMessageHook);
        RegisterHotKey(_windowHandle, ToggleClickThroughHotkeyId, ModControl | ModShift, VkF8);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_windowHandle != IntPtr.Zero)
            UnregisterHotKey(_windowHandle, ToggleClickThroughHotkeyId);
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && wParam.ToInt32() == ToggleClickThroughHotkeyId)
        {
            ToggleClickThrough();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void UpdateDefaultPosition()
    {
        Left = (SystemParameters.WorkArea.Width - ActualWidth) / 2 + SystemParameters.WorkArea.Left;
        Top = SystemParameters.WorkArea.Bottom - ActualHeight - _bottomOffset;
        ClampToWorkArea();
    }

    private void SubtitlePanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_interactionEnabled || e.LeftButton != MouseButtonState.Pressed) return;
        e.Handled = true;
        MoveWindow();
    }

    private void InteractionSurface_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_interactionEnabled || e.LeftButton != MouseButtonState.Pressed) return;
        if (e.OriginalSource is DependencyObject source && IsInsideInteractiveChrome(source)) return;
        e.Handled = true;
        MoveWindow();
    }

    private void MoveWindow()
    {
        try
        {
            DragMove();
            ClampToWorkArea();
            RaisePlacementChanged();
        }
        catch (InvalidOperationException)
        {
            // The mouse button can be released while WPF enters DragMove.
        }
    }

    private static bool IsInsideInteractiveChrome(DependencyObject source)
    {
        for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or Thumb or System.Windows.Controls.ContextMenu or MenuItem) return true;
        }

        return false;
    }

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (!_interactionEnabled) return;
        _isResizing = true;
        e.Handled = true;
    }

    private void TopResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isResizing) return;
        ResizeBy(0, e.VerticalChange, 0, 0);
        e.Handled = true;
    }

    private void BottomResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isResizing) return;
        ResizeBy(0, 0, 0, e.VerticalChange);
        e.Handled = true;
    }

    private void LeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isResizing) return;
        ResizeBy(e.HorizontalChange, 0, 0, 0);
        e.Handled = true;
    }

    private void RightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isResizing) return;
        ResizeBy(0, 0, e.HorizontalChange, 0);
        e.Handled = true;
    }

    private void BottomRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_isResizing) return;
        ResizeBy(0, 0, e.HorizontalChange, e.VerticalChange);
        e.Handled = true;
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_isResizing) return;
        CompleteResize();
        e.Handled = true;
    }

    private void CompleteResize()
    {
        _isResizing = false;
        RaisePlacementChanged();
    }

    private void ResizeBy(double leftDelta, double topDelta, double rightDelta, double bottomDelta)
    {
        var workArea = SystemParameters.WorkArea;
        var maximumWidth = Math.Max(MinWidth, workArea.Width);
        var requestedLeft = Left + leftDelta;
        var requestedTop = Top + topDelta;
        var requestedWidth = ActualWidth - leftDelta + rightDelta;
        var requestedHeight = ActualHeight - topDelta + bottomDelta;

        var width = Math.Clamp(requestedWidth, MinWidth, maximumWidth);
        var height = Math.Clamp(requestedHeight, MinHeight, Math.Min(MaxHeight, workArea.Height));
        if (leftDelta != 0)
        {
            var appliedLeftDelta = ActualWidth - width;
            Left = Clamp(Left + appliedLeftDelta, workArea.Left, workArea.Right - width);
        }

        if (topDelta != 0)
        {
            var appliedTopDelta = ActualHeight - height;
            Top = Clamp(Top + appliedTopDelta, workArea.Top, workArea.Bottom - height);
        }
        else
        {
            Top = Clamp(requestedTop, workArea.Top, workArea.Bottom - height);
        }

        if (leftDelta == 0)
        {
            Left = Clamp(requestedLeft, workArea.Left, workArea.Right - width);
        }

        Width = width;
        Height = height;
        _expanded = Height > 240;
        RefreshToolbarText();
        _maxTextWidth = Math.Max(240, Width - 42);
        RefreshLineStyles();
    }

    private void CloseSubtitleButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void SubtitlePanel_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ClickThroughMenuItem.IsChecked = !_interactionEnabled;
    }

    private void IncreaseSubtitleFont_Click(object sender, RoutedEventArgs e) => ChangeFontSize(2);

    private void DecreaseSubtitleFont_Click(object sender, RoutedEventArgs e) => ChangeFontSize(-2);

    private void ChangeFontSize(double delta)
    {
        _sourceFontSize = Math.Clamp(_sourceFontSize + delta, 12, 32);
        _translationFontSize = Math.Clamp(_translationFontSize + delta, 16, 42);
        RefreshLineStyles();
        FontSizeChanged?.Invoke(this,
            new SubtitleFontSizeChanged(_sourceFontSize, _translationFontSize));
    }

    private void ClickThroughMenuItem_Click(object sender, RoutedEventArgs e) =>
        SetInteractionEnabled(!ClickThroughMenuItem.IsChecked);

    private void ToggleSource_Click(object sender, RoutedEventArgs e)
    {
        _showSourceText = !_showSourceText;
        foreach (var line in _subtitleLines)
        {
            if (_showSourceText && string.IsNullOrWhiteSpace(line.SourceText))
                line.SourceText = line.RawSourceText;
            line.SourceVisibility = _showSourceText && !string.IsNullOrWhiteSpace(line.SourceText)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        RefreshToolbarText();
    }

    private void CycleFontSize_Click(object sender, RoutedEventArgs e)
    {
        if (!_interactionEnabled) return;

        var menu = CreateOverlayDropDown(FontSizeButton);
        AddFontSizeMenuItem(menu, "小号字体", 0);
        AddFontSizeMenuItem(menu, "中号字体", 1);
        AddFontSizeMenuItem(menu, "大号字体", 2);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddFontSizeMenuItem(ContextMenu menu, string label, int preset)
    {
        var item = new MenuItem
        {
            Header = label,
            IsCheckable = true,
            IsChecked = _fontPreset == preset
        };
        item.Click += (_, _) => ApplyFontPreset(preset);
        menu.Items.Add(item);
    }

    private void ApplyFontPreset(int preset)
    {
        _fontPreset = preset;
        (_sourceFontSize, _translationFontSize) = _fontPreset switch
        {
            0 => (15, 20),
            2 => (20, 30),
            _ => (17, 24)
        };
        RefreshLineStyles();
        RefreshToolbarText();
        FontSizeChanged?.Invoke(this,
            new SubtitleFontSizeChanged(_sourceFontSize, _translationFontSize));
    }

    private void ToggleExpanded_Click(object sender, RoutedEventArgs e)
    {
        var currentHeight = ActualHeight > 0 ? ActualHeight : Height;
        var bottom = Top + currentHeight;
        _expanded = !_expanded;
        Height = _expanded ? ExpandedHeight : CollapsedHeight;
        UpdateLayout();
        var newHeight = ActualHeight > 0 ? ActualHeight : Height;
        Top = bottom - newHeight;
        ClampToWorkArea();
        RefreshToolbarText();
        TrimSubtitleLines();
        ScrollToLatestSubtitle();
        RaisePlacementChanged();
    }

    private void RefreshToolbarText()
    {
        TargetLanguageButton.Content = $"🌐  翻译为：{_targetLanguageLabel} ▼";
        ToggleSourceButton.Content = _showSourceText ? "▣  关闭原文" : "▣  显示原文";
        ToggleSourceMenuItem.Header = _showSourceText ? "关闭原文" : "显示原文";
        ToggleExpandedButton.Content = _expanded ? "↙  收起字幕" : "↗  展开字幕";
        ToggleExpandedMenuItem.Header = _expanded ? "收起字幕" : "展开字幕";
        FontSizeButton.Content = _fontPreset switch
        {
            0 => "Aₐ  小号字体 ▼",
            2 => "Aₐ  大号字体 ▼",
            _ => "Aₐ  中号字体 ▼"
        };
    }

    private void TargetLanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_interactionEnabled) return;

        var menu = CreateOverlayDropDown(TargetLanguageButton);

        foreach (var item in LanguageItem.Targets)
        {
            var menuItem = new MenuItem
            {
                Header = item.DisplayName,
                Tag = item.Value,
                IsCheckable = true,
                IsChecked = item.Value == _targetLanguage
            };
            menuItem.Click += (_, _) =>
            {
                if (menuItem.Tag is not SupportedLanguage language) return;
                SetTargetLanguage(language);
                TargetLanguageChanged?.Invoke(this, language);
            };
            menu.Items.Add(menuItem);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private static ContextMenu CreateOverlayDropDown(FrameworkElement target)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            Background = new SolidColorBrush(Color.FromRgb(52, 52, 52)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6)
        };
        var itemStyle = new Style(typeof(MenuItem));
        itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 8, 14, 8)));
        itemStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13d));
        itemStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        menu.ItemContainerStyle = itemStyle;
        return menu;
    }

    private void ResetPositionMenuItem_Click(object sender, RoutedEventArgs e) => ResetPosition();

    private void CloseSubtitleMenuItem_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateTaskbarTitle(string? text)
    {
        var value = string.IsNullOrWhiteSpace(text) ? "Local Translator 字幕" : text.Trim();
        Title = value.Length <= 48 ? value : $"{value[..47]}…";
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_interactionEnabled || _isFullyTransparent) return;
        InteractionHint.Opacity = 1;
        ResizeThumb.Opacity = 0.9;
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        InteractionHint.Opacity = 0;
        ResizeThumb.Opacity = 0;
    }

    private void ScrollToLatestSubtitle() =>
        Dispatcher.BeginInvoke(() => SubtitleScrollViewer.ScrollToEnd());

    private void ApplyClickThrough()
    {
        if (_windowHandle == IntPtr.Zero) return;

        var extendedStyle = GetWindowLong(_windowHandle, GwlExStyle);
        extendedStyle = _interactionEnabled
            ? extendedStyle & ~WsExTransparent
            : extendedStyle | WsExTransparent;
        SetWindowLong(_windowHandle, GwlExStyle, extendedStyle);
        SetWindowPos(_windowHandle, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        SubtitlePanel.Cursor = _interactionEnabled ? Cursors.SizeAll : Cursors.Arrow;
        RefreshResizeInteraction();
        if (!_interactionEnabled)
        {
            InteractionHint.Opacity = 0;
            ResizeThumb.Opacity = 0;
        }
    }

    private void RefreshResizeInteraction()
    {
        // In pure-text mode every point in the subtitle area should drag the window.
        // Invisible resize thumbs otherwise steal mouse-down events along the edges.
        var canResize = _interactionEnabled && !_isFullyTransparent;
        TopResizeThumb.IsHitTestVisible = canResize;
        BottomResizeThumb.IsHitTestVisible = canResize;
        LeftResizeThumb.IsHitTestVisible = canResize;
        RightResizeThumb.IsHitTestVisible = canResize;
        ResizeThumb.IsHitTestVisible = canResize;
    }

    private void RaisePlacementChanged() => PlacementChanged?.Invoke(this,
        new SubtitleOverlayPlacement(Left, Top, Width, Height));

    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = Clamp(Left, workArea.Left, workArea.Right - ActualWidth);
        Top = Clamp(Top, workArea.Top, workArea.Bottom - ActualHeight);
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        Math.Min(Math.Max(value, minimum), Math.Max(minimum, maximum));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

}

public sealed record SubtitleOverlayPlacement(double Left, double Top, double Width, double Height);

public sealed record SubtitleFontSizeChanged(double SourceFontSize, double TranslationFontSize);

public sealed class OverlaySubtitleLine : INotifyPropertyChanged
{
    private string _rawSourceText = string.Empty;
    private string _sourceText = string.Empty;
    private string _translationText = string.Empty;
    private double _sourceFontSize;
    private double _translationFontSize;
    private double _sourceLineHeight;
    private double _translationLineHeight;
    private double _sourceOpacity = 0.82;
    private double _translationOpacity = 1;
    private double _maxTextWidth = 820;
    private FontWeight _translationFontWeight = FontWeights.SemiBold;
    private Visibility _sourceVisibility = Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsPending { get; set; }
    public long Sequence { get; init; }

    public string RawSourceText
    {
        get => _rawSourceText;
        set => SetField(ref _rawSourceText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        set => SetField(ref _sourceText, value);
    }

    public string TranslationText
    {
        get => _translationText;
        set => SetField(ref _translationText, value);
    }

    public double SourceFontSize
    {
        get => _sourceFontSize;
        set => SetField(ref _sourceFontSize, value);
    }

    public double TranslationFontSize
    {
        get => _translationFontSize;
        set => SetField(ref _translationFontSize, value);
    }

    public double SourceLineHeight
    {
        get => _sourceLineHeight;
        set => SetField(ref _sourceLineHeight, value);
    }

    public double TranslationLineHeight
    {
        get => _translationLineHeight;
        set => SetField(ref _translationLineHeight, value);
    }

    public double SourceOpacity
    {
        get => _sourceOpacity;
        set => SetField(ref _sourceOpacity, value);
    }

    public double TranslationOpacity
    {
        get => _translationOpacity;
        set => SetField(ref _translationOpacity, value);
    }

    public double MaxTextWidth
    {
        get => _maxTextWidth;
        set => SetField(ref _maxTextWidth, value);
    }

    public FontWeight TranslationFontWeight
    {
        get => _translationFontWeight;
        set => SetField(ref _translationFontWeight, value);
    }

    public Visibility SourceVisibility
    {
        get => _sourceVisibility;
        set => SetField(ref _sourceVisibility, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
