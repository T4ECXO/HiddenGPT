using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DrawingPoint = System.Drawing.Point;

namespace HiddenGPT;

public partial class MainWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    // RegisterHotKey requires application-defined IDs to be in the 0x0000-0xBFFF range.
    private const int HotkeyId = 0x4847;
    private const int CursorHotkeyId = 0x4848;
    private const int ViewOnlyHotkeyId = 0x4849;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const int WmHotkey = 0x0312;
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeMouse = 0;
    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;
    private const uint RidevInputSink = 0x00000100;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExLayered = 0x00080000;
    private const int WmStyleChanging = 0x007C;
    private const string ChatGptUrl = "https://chatgpt.com/";

    private HwndSource? _source;
    private readonly SideButtonShortcut _sideButtonShortcut = new();
    private bool _learningSideButton;
    private bool _rawMouseRegistered;
    private bool _viewOnlyHotkeyRegistered;
    private bool _viewOnly;
    private bool _topmostBeforeViewOnly;
    private bool _captureExclusionEnabled;
    private bool _hotkeyRegistered;
    private bool _cursorHotkeyRegistered;
    private bool _browserReady;
    private bool _browserInitializationStarted;
    private readonly bool _startWithCaptureTest;
    private bool _opacitySettingLoaded;
    private bool _browserVisible;
    private bool _virtualCursorEnabled;
    private bool _virtualCursorActive;
    private bool _virtualLeftDown;
    private bool _virtualRightDown;
    private double _virtualCursorX;
    private double _virtualCursorY;
    private NativePoint _cursorAnchor;
    private MethodInfo? _sendMouseInputMethod;
    private double _zoom = 1.0;
    private double _browserOpacity = 0.85;
    private readonly DispatcherTimer _cursorLockTimer = new(DispatcherPriority.Input);

    private static string ProfileFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HiddenGPT",
        "WebView2Profile");

    private static string LogFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HiddenGPT",
        "HiddenGPT.log");

    private static string SettingsFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HiddenGPT",
        "window-opacity.txt");

    static MainWindow()
    {
        // ToolTipOpening is a direct event: suppress it on every element, not just the root.
        // Tooltips use separate native windows outside the capture-protected HWND.
        var suppressToolTip = new ToolTipEventHandler((_, e) => e.Handled = true);
        EventManager.RegisterClassHandler(typeof(FrameworkElement),
            ToolTipService.ToolTipOpeningEvent, suppressToolTip, true);
        EventManager.RegisterClassHandler(typeof(FrameworkContentElement),
            ToolTipService.ToolTipOpeningEvent, suppressToolTip, true);
    }

    public MainWindow() : this(false) { }

    internal MainWindow(bool startWithCaptureTest)
    {
        _startWithCaptureTest = startWithCaptureTest;
        Log("Constructing main window.");
        InitializeComponent();
        LoadMouseShortcut();
        PreviewMouseDown += SuppressLocalSideButton;
        PreviewMouseUp += SuppressLocalSideButton;
        LoadOpacitySetting();
        _opacitySettingLoaded = true;
        _cursorLockTimer.Interval = TimeSpan.FromMilliseconds(16);
        _cursorLockTimer.Tick += (_, _) => UpdateVirtualCursor();
        CursorLockCheckBox.IsChecked = true;
        TopmostCheckBox.IsChecked = true;
        Log("Main window constructed.");
    }

    internal bool PrepareForFirstShow()
    {
        // Create the HWND without showing it, then protect the entire first frame.
        var handle = new WindowInteropHelper(this).EnsureHandle();
        _captureExclusionEnabled = ApplyAndVerifyCaptureExclusion(handle, out var error);
        Log($"Pre-show capture exclusion result: {_captureExclusionEnabled}; error: {error}.");
        return _captureExclusionEnabled;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        Log("Source initialization started.");
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessageHook);
        var rawMouse = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x02,
            Flags = RidevInputSink,
            Target = handle
        };
        _rawMouseRegistered = RegisterRawInputDevices(new[] { rawMouse }, 1, (uint)Marshal.SizeOf<RawInputDevice>());
        if (!_rawMouseRegistered)
        {
            Log($"Background raw mouse registration failed: {Marshal.GetLastWin32Error()}.");
            LearnMouseButton.IsEnabled = false;
            CursorLockCheckBox.IsChecked = false;
            CursorLockCheckBox.IsEnabled = false;
            CursorLockCheckBox.ToolTip = $"Raw mouse input is unavailable (Windows error {Marshal.GetLastWin32Error()}).";
        }
        _hotkeyRegistered = RegisterHotKey(handle, HotkeyId, ModControl | ModAlt, (uint)'H');
        _cursorHotkeyRegistered = RegisterHotKey(handle, CursorHotkeyId, ModControl | ModAlt, (uint)'M');
        _viewOnlyHotkeyRegistered = RegisterHotKey(handle, ViewOnlyHotkeyId, ModControl | ModAlt | 0x4000, (uint)'P');
        ViewOnlyButton.IsEnabled = _rawMouseRegistered || _viewOnlyHotkeyRegistered;
        Log($"Background raw mouse ready: {_rawMouseRegistered}; shortcut: Mouse {_sideButtonShortcut.Button}.");
        if (!_hotkeyRegistered)
        {
            HideButton.IsEnabled = false;
            HideButton.Content = "Hide unavailable";
            HideButton.ToolTip = $"Ctrl+Alt+H could not be registered (Windows error {Marshal.GetLastWin32Error()}).";
        }

        CaptureStatus.Text = "● Initializing protected browser…";
        Log("Window source initialized; waiting for the composition control to load.");
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_browserInitializationStarted || !_captureExclusionEnabled) return;

        _browserInitializationStarted = true;
        Log("First frame rendered; starting hidden browser initialization.");
        await InitializeBrowserAsync();
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Log($"WebView2 Runtime: {runtimeVersion}.");
            PageStatus.Text = $"WebView2 Runtime {runtimeVersion}";

            Directory.CreateDirectory(ProfileFolder);
            Log("Creating WebView2 environment.");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfileFolder);
            Log("WebView2 environment created; creating controller.");
            await Browser.EnsureCoreWebView2Async(environment);
            Log("WebView2 controller created.");

            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled = false;
            Browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Browser.CoreWebView2.ScriptDialogOpening += (_, _) =>
                PageStatus.Text = "Blocked a browser script dialog.";
            Browser.CoreWebView2.NewWindowRequested += NewWindowRequested;
            Browser.CoreWebView2.NavigationStarting += NavigationStarting;
            Browser.CoreWebView2.PermissionRequested += PermissionRequested;
            Browser.CoreWebView2.DownloadStarting += DownloadStarting;
            Browser.CoreWebView2.HistoryChanged += (_, _) => UpdateNavigationButtons();

            await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("""
                document.addEventListener('click', event => {
                  const input = event.target?.closest?.('input[type=file]');
                  if (input) {
                    event.preventDefault();
                    event.stopImmediatePropagation();
                  }
                }, true);
            """);

            var handle = new WindowInteropHelper(this).Handle;
            _captureExclusionEnabled = ApplyAndVerifyCaptureExclusion(handle, out var captureError);
            Log($"Capture exclusion result: {_captureExclusionEnabled}; error: {captureError}.");
            if (!_captureExclusionEnabled)
            {
                HideProtectedWindow();
                Application.Current.Shutdown(1);
                return;
            }

            CaptureStatus.Text = "● Capture exclusion enabled (Windows API)";
            CaptureStatus.Foreground = Brushes.LightGreen;

            _browserReady = true;
            Log("Browser is ready.");
            UpdateChatGptButtonState();
            if (_startWithCaptureTest) ShowTestPage();
            else OpenChatGpt();
        }
        catch (Exception ex)
        {
            Log($"Browser initialization failed: {ex}");
            StartupTitle.Text = "WebView2 could not start";
            StartupMessage.Text = $"Install or repair the Microsoft Edge WebView2 Runtime, then restart HiddenGPT.\n\n{ex.Message}";
            PageStatus.Text = "WebView2 Runtime unavailable.";
        }
    }

    private void ShowTestPage()
    {
        if (!_browserReady) return;

        var html = """
            <!doctype html><html><head><meta charset="utf-8"><meta name="color-scheme" content="dark">
            <style>
              *{box-sizing:border-box} body{margin:0;min-height:100vh;display:grid;place-items:center;font-family:Segoe UI,Arial,sans-serif;color:#fafafa;background:#111827}
              main{max-width:720px;padding:42px;border:1px solid #374151;border-radius:18px;background:#18181b;box-shadow:0 18px 55px #0008}
              h1{margin:0 0 14px;font-size:34px} p{line-height:1.6;color:#d4d4d8}.marker{margin-top:28px;padding:24px;border-radius:12px;background:repeating-linear-gradient(45deg,#ef4444 0 20px,#facc15 20px 40px);color:#111;font-size:22px;font-weight:800;text-align:center}
              code{color:#86efac}
            </style></head><body><main><h1>Capture exclusion test</h1>
            <p>This harmless page lets you verify capture before signing in. Record the entire monitor and inspect the saved video, or inspect your screen share from another viewer.</p>
            <div class="marker">THIS CONTENT MUST NOT APPEAR IN THE STREAM</div>
            <p>Move, resize, maximize, minimize, hide, restore, and restart the app while recording or sharing. The desktop behind this window should remain visible. When every check passes, tick <code>Recording / screen-share test completed</code> above. Repeat the test when you change the recording app or capture method.</p>
            </main></body></html>
        """;
        StartupPanel.Visibility = Visibility.Visible;
        SetBrowserVisible(false);
        Browser.NavigateToString(html);
        PageStatus.Text = "Loading local capture test…";
    }

    private void Browser_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_captureExclusionEnabled) return;

        StartupPanel.Visibility = Visibility.Collapsed;
        SetBrowserVisible(true);
        PageStatus.Text = e.IsSuccess ? (Browser.Source?.ToString() ?? "Local capture test page") : $"Navigation failed: {e.WebErrorStatus}";
        UpdateNavigationButtons();
    }

    private void NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) &&
            uri.Scheme is not ("http" or "https" or "data" or "about"))
        {
            e.Cancel = true;
            PageStatus.Text = $"Blocked external protocol: {uri.Scheme}";
        }
    }

    private void NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            Browser.CoreWebView2.Navigate(e.Uri);
        else
            PageStatus.Text = "Blocked a page from opening an unprotected window.";
    }

    private void PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (e.PermissionKind == CoreWebView2PermissionKind.Notifications)
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            PageStatus.Text = "Blocked system notifications outside the protected window.";
        }
    }

    private void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true;
        PageStatus.Text = "Download canceled: save dialogs can appear outside the protected window.";
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_browserReady && Browser.CanGoBack) Browser.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_browserReady && Browser.CanGoForward) Browser.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_browserReady) Browser.Reload();
    }

    private void TestPage_Click(object sender, RoutedEventArgs e) => ShowTestPage();

    private void ChatGpt_Click(object sender, RoutedEventArgs e)
    {
        if (!_browserReady)
        {
            UpdateChatGptButtonState();
            return;
        }

        OpenChatGpt();
    }

    private void OpenChatGpt()
    {
        PageStatus.Text = "Opening ChatGPT…";
        Browser.CoreWebView2.Navigate(ChatGptUrl);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom - 0.1);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom + 0.1);

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, 0.5, 2.0);
        Browser.ZoomFactor = _zoom;
        ZoomText.Text = $"{_zoom:P0}";
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _browserOpacity = e.NewValue;
        if (BrowserHost is not null && _browserVisible)
            BrowserHost.Opacity = _browserOpacity;
        if (SettingsBar is not null)
            SettingsBar.Opacity = _browserOpacity;
        if (CaptureStatusBar is not null)
            CaptureStatusBar.Opacity = _browserOpacity;
        if (OpacityText is not null)
            OpacityText.Text = $"{e.NewValue:P0}";

        if (_opacitySettingLoaded)
            SaveOpacitySetting(e.NewValue);
    }

    private void LoadOpacitySetting()
    {
        const double defaultOpacity = 0.85;
        var opacity = defaultOpacity;

        try
        {
            if (File.Exists(SettingsFile) &&
                double.TryParse(File.ReadAllText(SettingsFile), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var savedOpacity))
            {
                opacity = Math.Clamp(savedOpacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
            }
        }
        catch (Exception ex)
        {
            Log($"Could not load window opacity: {ex.Message}");
        }

        OpacitySlider.Value = opacity;
        _browserOpacity = opacity;
        SettingsBar.Opacity = opacity;
        CaptureStatusBar.Opacity = opacity;
        OpacityText.Text = $"{opacity:P0}";
    }

    private static void SaveOpacitySetting(double opacity)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, opacity.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Log($"Could not save window opacity: {ex.Message}");
        }
    }

    private void Topmost_Changed(object sender, RoutedEventArgs e) => Topmost = TopmostCheckBox.IsChecked == true;

    private void WindowHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindInteractiveAncestor(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            DragMove();
    }

    private static bool FindInteractiveAncestor(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or Slider or CheckBox) return true;
            if (current is Border) break;
        }
        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CursorLock_Changed(object sender, RoutedEventArgs e)
    {
        _virtualCursorEnabled = CursorLockCheckBox.IsChecked == true;
        if (_virtualCursorEnabled)
        {
            _cursorLockTimer.Start();
            PageStatus.Text = "Virtual cursor enabled. Move the pointer into HiddenGPT; Ctrl+Alt+M disables it.";
        }
        else
        {
            DeactivateVirtualCursor();
            _cursorLockTimer.Stop();
            PageStatus.Text = "Virtual cursor disabled.";
        }
    }

    private void UpdateVirtualCursor()
    {
        if (_viewOnly || !_virtualCursorEnabled || !IsActive)
        {
            if (_virtualCursorActive) DeactivateVirtualCursor();
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !TryGetClientBounds(handle, out var bounds) || !GetCursorPos(out var cursor)) return;

        if (!_virtualCursorActive)
        {
            if (cursor.X < bounds.Left || cursor.X >= bounds.Right || cursor.Y < bounds.Top || cursor.Y >= bounds.Bottom) return;
            ActivateVirtualCursor(cursor, bounds);
            return;
        }

        UpdateVirtualButtons();
    }

    private void ApplyRawMouseDelta(int deltaX, int deltaY)
    {
        if (!_virtualCursorActive || (deltaX == 0 && deltaY == 0)) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !TryGetClientBounds(handle, out var bounds)) return;

        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var delta = transform.Transform(new Vector(deltaX, deltaY));
        _virtualCursorX += delta.X;
        _virtualCursorY += delta.Y;

        if (_virtualCursorX < 0 || _virtualCursorX >= RootGrid.ActualWidth ||
            _virtualCursorY < 0 || _virtualCursorY >= RootGrid.ActualHeight)
        {
            ReleaseVirtualCursorOutside(bounds);
            return;
        }

        PositionVirtualCursor();
        SendBrowserMouseInput(CoreWebView2MouseEventKind.Move);
    }

    private void ActivateVirtualCursor(NativePoint cursor, NativeRect bounds)
    {
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var initial = transform.Transform(new Point(cursor.X - bounds.Left, cursor.Y - bounds.Top));
        _virtualCursorX = Math.Clamp(initial.X, 0, Math.Max(0, RootGrid.ActualWidth - 1));
        _virtualCursorY = Math.Clamp(initial.Y, 0, Math.Max(0, RootGrid.ActualHeight - 1));

        var leftDistance = cursor.X - bounds.Left;
        var rightDistance = bounds.Right - 1 - cursor.X;
        var topDistance = cursor.Y - bounds.Top;
        var bottomDistance = bounds.Bottom - 1 - cursor.Y;
        var nearest = Math.Min(Math.Min(leftDistance, rightDistance), Math.Min(topDistance, bottomDistance));
        _cursorAnchor = nearest == leftDistance ? new NativePoint(bounds.Left + 1, cursor.Y)
            : nearest == rightDistance ? new NativePoint(bounds.Right - 2, cursor.Y)
            : nearest == topDistance ? new NativePoint(cursor.X, bounds.Top + 1)
            : new NativePoint(cursor.X, bounds.Bottom - 2);

        _virtualCursorActive = true;
        _virtualLeftDown = IsButtonDown(VkLeftButton);
        _virtualRightDown = IsButtonDown(VkRightButton);
        VirtualCursorLayer.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.Arrow;
        PositionVirtualCursor();
        SetCursorPos(_cursorAnchor.X, _cursorAnchor.Y);
        var clip = new NativeRect
        {
            Left = _cursorAnchor.X,
            Top = _cursorAnchor.Y,
            Right = _cursorAnchor.X + 1,
            Bottom = _cursorAnchor.Y + 1
        };
        ClipCursor(ref clip);
        SendBrowserMouseInput(CoreWebView2MouseEventKind.Move);
    }

    private void PositionVirtualCursor()
    {
        Canvas.SetLeft(VirtualCursorVisual, _virtualCursorX);
        Canvas.SetTop(VirtualCursorVisual, _virtualCursorY);

        if (_virtualLeftDown && TryGetSliderAtVirtualPoint(out var slider, out var point))
            SetSliderFromPoint(slider, point);
    }

    private void UpdateVirtualButtons()
    {
        var leftDown = IsButtonDown(VkLeftButton);
        if (leftDown != _virtualLeftDown)
        {
            _virtualLeftDown = leftDown;
            DispatchVirtualButton(leftDown, true);
        }

        var rightDown = IsButtonDown(VkRightButton);
        if (rightDown != _virtualRightDown)
        {
            _virtualRightDown = rightDown;
            DispatchVirtualButton(rightDown, false);
        }
    }

    private void DispatchVirtualButton(bool isDown, bool isLeft)
    {
        if (IsVirtualPointInBrowser(out _))
        {
            if (isDown) Browser.Focus();
            SendBrowserMouseInput(isLeft
                ? isDown ? CoreWebView2MouseEventKind.LeftButtonDown : CoreWebView2MouseEventKind.LeftButtonUp
                : isDown ? CoreWebView2MouseEventKind.RightButtonDown : CoreWebView2MouseEventKind.RightButtonUp);
            return;
        }

        if (isLeft && !isDown)
            InvokeWpfControlAtVirtualPoint();
    }

    private void InvokeWpfControlAtVirtualPoint()
    {
        VirtualCursorLayer.IsHitTestVisible = false;
        var hit = RootGrid.InputHitTest(new Point(_virtualCursorX, _virtualCursorY)) as DependencyObject;
        VirtualCursorLayer.IsHitTestVisible = true;

        for (var current = hit; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is CheckBox checkBox)
            {
                checkBox.IsChecked = checkBox.IsChecked != true;
                return;
            }

            if (current is ButtonBase button)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                return;
            }

            if (current is Slider slider)
            {
                var point = RootGrid.TranslatePoint(new Point(_virtualCursorX, _virtualCursorY), slider);
                SetSliderFromPoint(slider, point);
                return;
            }
        }
    }

    private bool TryGetSliderAtVirtualPoint(out Slider slider, out Point point)
    {
        slider = null!;
        point = default;
        VirtualCursorLayer.IsHitTestVisible = false;
        var hit = RootGrid.InputHitTest(new Point(_virtualCursorX, _virtualCursorY)) as DependencyObject;
        VirtualCursorLayer.IsHitTestVisible = true;
        for (var current = hit; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is not Slider found) continue;
            slider = found;
            point = RootGrid.TranslatePoint(new Point(_virtualCursorX, _virtualCursorY), found);
            return true;
        }
        return false;
    }

    private static void SetSliderFromPoint(Slider slider, Point point)
    {
        if (slider.ActualWidth <= 0) return;
        var ratio = Math.Clamp(point.X / slider.ActualWidth, 0, 1);
        slider.Value = slider.Minimum + ratio * (slider.Maximum - slider.Minimum);
    }

    private void SendBrowserMouseInput(CoreWebView2MouseEventKind kind, uint mouseData = 0)
    {
        if (!IsVirtualPointInBrowser(out var browserPoint)) return;

        _sendMouseInputMethod ??= typeof(WebView2CompositionControl).GetMethod(
            "SendMouseInput",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (_sendMouseInputMethod is null) return;

        var keys = CoreWebView2MouseEventVirtualKeys.None;
        if (_virtualLeftDown) keys |= CoreWebView2MouseEventVirtualKeys.LeftButton;
        if (_virtualRightDown) keys |= CoreWebView2MouseEventVirtualKeys.RightButton;
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) keys |= CoreWebView2MouseEventVirtualKeys.Control;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) keys |= CoreWebView2MouseEventVirtualKeys.Shift;

        try
        {
            _sendMouseInputMethod.Invoke(Browser, new object[] { kind, keys, mouseData, browserPoint });
        }
        catch (Exception ex)
        {
            Log($"Virtual browser mouse input failed: {ex.GetBaseException().Message}");
        }
    }

    private bool IsVirtualPointInBrowser(out DrawingPoint browserPoint)
    {
        browserPoint = default;
        if (!_browserReady || BrowserHost.Opacity == 0) return false;

        var origin = Browser.TranslatePoint(new Point(0, 0), RootGrid);
        var x = _virtualCursorX - origin.X;
        var y = _virtualCursorY - origin.Y;
        if (x < 0 || y < 0 || x >= Browser.ActualWidth || y >= Browser.ActualHeight) return false;

        var dpi = VisualTreeHelper.GetDpi(Browser);
        browserPoint = new DrawingPoint((int)Math.Round(x * dpi.DpiScaleX), (int)Math.Round(y * dpi.DpiScaleY));
        return true;
    }

    private void VirtualCursor_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_virtualCursorActive) return;
        SendBrowserMouseInput(CoreWebView2MouseEventKind.Wheel, unchecked((uint)e.Delta));
        e.Handled = true;
    }

    private void ReleaseVirtualCursorOutside(NativeRect bounds)
    {
        var target = _cursorAnchor;
        if (_virtualCursorX < 0) target.X = bounds.Left - 2;
        else if (_virtualCursorX >= RootGrid.ActualWidth) target.X = bounds.Right + 1;
        else if (_virtualCursorY < 0) target.Y = bounds.Top - 2;
        else target.Y = bounds.Bottom + 1;

        DeactivateVirtualCursor();
        SetCursorPos(target.X, target.Y);
    }

    private void DeactivateVirtualCursor()
    {
        ReleaseCursorClip(IntPtr.Zero);
        _virtualCursorActive = false;
        VirtualCursorLayer.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
    }

    private static bool IsButtonDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool TryGetClientBounds(IntPtr handle, out NativeRect bounds)
    {
        bounds = default;
        if (!GetClientRect(handle, out var client)) return false;
        var origin = new NativePoint(0, 0);
        if (!ClientToScreen(handle, ref origin)) return false;
        bounds = new NativeRect
        {
            Left = origin.X,
            Top = origin.Y,
            Right = origin.X + client.Right,
            Bottom = origin.Y + client.Bottom
        };
        return true;
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (_hotkeyRegistered) HideProtectedWindow();
    }

    private void HideProtectedWindow()
    {
        DeactivateVirtualCursor();
        Hide();
    }

    private void ToggleProtectedWindow()
    {
        if (IsVisible && WindowState != WindowState.Minimized) HideProtectedWindow();
        else ShowAndActivateProtected();
    }

    private void ToggleViewOnly()
    {
        _learningSideButton = false;
        LearnMouseButton.Content = "Set mouse button";
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            ShowAndActivateProtected();
            return;
        }

        if (SetViewOnly(!_viewOnly) && !_viewOnly)
        {
            Activate();
            if (_browserReady) Browser.Focus();
        }
    }

    private void ViewOnly_Click(object sender, RoutedEventArgs e) => ToggleViewOnly();

    private void SuppressLocalSideButton(object sender, MouseButtonEventArgs e)
    {
        var selected = _sideButtonShortcut.Button == 4 ? MouseButton.XButton1 : MouseButton.XButton2;
        if (e.ChangedButton == selected ||
            (_learningSideButton && e.ChangedButton is MouseButton.XButton1 or MouseButton.XButton2))
            e.Handled = true;
    }

    private static string MouseShortcutFile => Path.Combine(Path.GetDirectoryName(SettingsFile)!, "mouse-shortcut.txt");

    private void LoadMouseShortcut()
    {
        try
        {
            if (File.Exists(MouseShortcutFile) && int.TryParse(File.ReadAllText(MouseShortcutFile), out var button)
                && button is 4 or 5) _sideButtonShortcut.SelectButton(button);
        }
        catch (Exception ex) { Log($"Could not load mouse shortcut: {ex.Message}"); }
        UpdateInteractionModeText();
    }

    private void LearnMouse_Click(object sender, RoutedEventArgs e)
    {
        _learningSideButton = !_learningSideButton;
        LearnMouseButton.Content = _learningSideButton ? "Cancel" : "Set mouse button";
        InteractionModeText.Text = _learningSideButton
            ? "Press and release the side button to use"
            : $"Interactive — Mouse {_sideButtonShortcut.Button} to view only";
    }

    private void HandleRawSideButtons(ushort flags)
    {
        var released = SideButtonShortcut.ReleasedButton(flags);
        if (released != 0) Log($"Raw Mouse {released} released; flags: 0x{flags:X4}.");
        if (_learningSideButton && released != 0)
        {
            _sideButtonShortcut.SelectButton(released);
            _learningSideButton = false;
            LearnMouseButton.Content = "Set mouse button";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MouseShortcutFile)!);
                File.WriteAllText(MouseShortcutFile, released.ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex) { Log($"Could not save mouse shortcut: {ex.Message}"); }
            UpdateInteractionModeText();
            return;
        }
        if (_sideButtonShortcut.Process(flags)) ToggleViewOnly();
        else if (released != 0 && released != _sideButtonShortcut.Button && !_viewOnly)
            InteractionModeText.Text = $"Detected Mouse {released} — use Set mouse button to select it";
    }

    private void UpdateInteractionModeText()
    {
        var shortcut = $"Mouse {_sideButtonShortcut.Button}";
        InteractionModeText.Text = _viewOnly ? $"View only — {shortcut} to interact" : $"Interactive — {shortcut} to view only";
        ViewOnlyButton.Content = _viewOnly ? $"Interact ({shortcut})" : $"View only ({shortcut})";
    }

    private bool SetViewOnly(bool enabled)
    {
        if (_viewOnly == enabled) return true;
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        var updatedStyle = enabled
            ? style | WsExTransparent | WsExNoActivate
            : style & ~(WsExTransparent | WsExNoActivate);
        // The style-changing hook must see the requested mode before SetWindowLong.
        var previousMode = _viewOnly;
        _viewOnly = enabled;
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLong(handle, GwlExStyle, updatedStyle) == 0 && Marshal.GetLastWin32Error() != 0)
        {
            _viewOnly = previousMode;
            Log($"Could not change view-only mode: {Marshal.GetLastWin32Error()}.");
            return false;
        }

        // Refresh the native style without moving, resizing, reordering, or activating.
        SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0037);
        RootGrid.IsHitTestVisible = !enabled;
        UpdateInteractionModeText();
        if (enabled)
        {
            _topmostBeforeViewOnly = Topmost;
            Topmost = true;
            DeactivateVirtualCursor();
            Mouse.Capture(null);
            Keyboard.ClearFocus();
            // Let the next visible application receive keyboard input as well.
            if (GetForegroundWindow() == handle)
            {
                for (var next = GetWindow(handle, 2); next != IntPtr.Zero; next = GetWindow(next, 2))
                {
                    GetWindowThreadProcessId(next, out var processId);
                    if (processId == Environment.ProcessId || !IsWindowVisible(next) || !IsWindowEnabled(next)) continue;
                    if (SetForegroundWindow(next)) break;
                }
            }
        }
        else
        {
            Topmost = _topmostBeforeViewOnly;
        }
        var actualStyle = GetWindowLong(handle, GwlExStyle);
        var expectedBits = enabled ? WsExLayered | WsExTransparent | WsExNoActivate : 0;
        var modeBits = WsExTransparent | WsExNoActivate;
        var verified = enabled
            ? (actualStyle & expectedBits) == expectedBits
            : (actualStyle & modeBits) == 0;
        Log($"View-only mode: {enabled}; native style: 0x{actualStyle:X8}; verified: {verified}.");
        if (!verified)
            InteractionModeText.Text = "Mode change failed — press Ctrl+Alt+P to return";
        return true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_viewOnly) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (_viewOnly) e.Handled = true;
        base.OnPreviewTextInput(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        DeactivateVirtualCursor();
        base.OnDeactivated(e);
    }

    private async void ClearSession_Click(object sender, RoutedEventArgs e)
    {
        if (!_browserReady) return;

        try
        {
            SetBrowserVisible(false);
            StartupPanel.Visibility = Visibility.Visible;
            StartupTitle.Text = "Clearing browser session…";
            StartupMessage.Text = "Cookies, local storage, cache, permissions, and browsing history are being removed from the dedicated HiddenGPT profile.";
            await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            StartupTitle.Text = "Preparing protected browser…";
            StartupMessage.Text = "Browser content stays hidden until Windows accepts capture exclusion.";
            ShowTestPage();
            PageStatus.Text = "Dedicated browser session cleared.";
        }
        catch (Exception ex)
        {
            StartupTitle.Text = "Session could not be cleared";
            StartupMessage.Text = ex.Message;
        }
    }

    private void CaptureTest_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        var completed = CaptureTestCheckBox.IsChecked == true;
        UpdateChatGptButtonState();
        PageStatus.Text = completed
            ? "Current recording / screen-share setup marked tested by the user for this session."
            : "Recording / screen-share output is not yet verified.";
    }

    private void UpdateChatGptButtonState()
    {
        var completed = CaptureTestCheckBox.IsChecked == true;
        ChatGptButton.IsEnabled = _browserReady;
        ChatGptButton.ToolTip = !_browserReady
            ? "Wait for the protected browser to finish starting"
            : completed
                ? "Open ChatGPT in the protected window"
                : "Open ChatGPT (recording / screen-share output is not yet verified)";
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _browserReady && Browser.CanGoBack;
        ForwardButton.IsEnabled = _browserReady && Browser.CanGoForward;
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmStyleChanging && wParam.ToInt32() == GwlExStyle && _viewOnly)
        {
            // Retain click-through if WPF refreshes the native window styles.
            var styles = Marshal.PtrToStructure<WindowStyleChange>(lParam);
            styles.NewStyle |= WsExTransparent | WsExNoActivate | WsExLayered;
            Marshal.StructureToPtr(styles, lParam, false);
        }
        if (msg == WmHotkey && wParam.ToInt32() == ViewOnlyHotkeyId)
        {
            Log("Ctrl+Alt+P received; toggling view-only mode.");
            ToggleViewOnly();
            handled = true;
        }
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleProtectedWindow();
            handled = true;
        }
        else if (msg == WmHotkey && wParam.ToInt32() == CursorHotkeyId)
        {
            CursorLockCheckBox.IsChecked = CursorLockCheckBox.IsChecked != true;
            handled = true;
        }
        else if (msg == WmInput && TryReadRawMouse(lParam, out var rawMouse))
        {
            HandleRawSideButtons(rawMouse.ButtonFlags);
            if (_virtualCursorActive) ApplyRawMouseDelta(rawMouse.LastX, rawMouse.LastY);
        }
        return IntPtr.Zero;
    }

    private static bool TryReadRawMouse(IntPtr rawInputHandle, out RawMouse mouse)
    {
        mouse = default;
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
        if (GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize) == uint.MaxValue || size == 0)
            return false;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInputHandle, RidInput, buffer, ref size, headerSize) == uint.MaxValue)
                return false;
            var input = Marshal.PtrToStructure<RawInput>(buffer);
            if (input.Header.Type != RimTypeMouse) return false;
            mouse = input.Mouse;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal void ShowAndActivateProtected()
    {
        // Verify before Show/restore can expose a frame, including second-instance activation.
        var handle = new WindowInteropHelper(this).Handle;
        _captureExclusionEnabled = ApplyAndVerifyCaptureExclusion(handle, out var error);
        Log($"Pre-restore capture exclusion result: {_captureExclusionEnabled}; error: {error}.");
        if (!_captureExclusionEnabled)
        {
            HideProtectedWindow();
            SetBrowserVisible(false);
            Application.Current.Shutdown(1);
            return;
        }

        if (!SetViewOnly(false)) return;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void SetBrowserVisible(bool visible)
    {
        _browserVisible = visible;
        BrowserHost.Opacity = visible ? _browserOpacity : 0;
        BrowserHost.IsHitTestVisible = visible;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero && _hotkeyRegistered) UnregisterHotKey(handle, HotkeyId);
        if (handle != IntPtr.Zero && _cursorHotkeyRegistered) UnregisterHotKey(handle, CursorHotkeyId);
        if (handle != IntPtr.Zero && _viewOnlyHotkeyRegistered) UnregisterHotKey(handle, ViewOnlyHotkeyId);
        _cursorLockTimer.Stop();
        DeactivateVirtualCursor();
        _source?.RemoveHook(WindowMessageHook);
        Browser.Dispose();
        base.OnClosing(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowStyleChange
    {
        public int OldStyle;
        public int NewStyle;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hWnd, out uint dwAffinity);

    private static bool ApplyAndVerifyCaptureExclusion(IntPtr handle, out int error)
    {
        error = 0;
        if (!SetWindowDisplayAffinity(handle, WdaExcludeFromCapture))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        if (!GetWindowDisplayAffinity(handle, out var affinity))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        if (affinity != WdaExcludeFromCapture)
        {
            error = 13; // ERROR_INVALID_DATA
            return false;
        }

        return true;
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never prevent the protected browser from starting.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint numberOfDevices,
        uint sizeOfDevice);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint sizeOfHeader);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCursorClip(IntPtr rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(4)] public uint Buttons;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(6)] public ushort ButtonData;
        [FieldOffset(8)] public uint RawButtons;
        [FieldOffset(12)] public int LastX;
        [FieldOffset(16)] public int LastY;
        [FieldOffset(20)] public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawInputHeader Header;
        public RawMouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
