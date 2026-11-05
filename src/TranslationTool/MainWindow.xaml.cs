using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TranslationTool.Services;

namespace TranslationTool;

public partial class MainWindow : Window
{
    private static readonly Brush ReadyBrush = CreateBrush(0x2B, 0xB6, 0x73);
    private static readonly Brush WarnBrush = CreateBrush(0xE0, 0x94, 0x1F);
    private static readonly Brush ErrorBrush = CreateBrush(0xD9, 0x53, 0x4F);
    private static readonly Brush IdleBrush = CreateBrush(0x9A, 0xA4, 0xB8);
    private static readonly Brush SecondaryBrush = CreateBrush(0x7A, 0x84, 0x9B);

    private readonly DispatcherTimer _autoTimer;
    private readonly DispatcherTimer _autoClearTimer;

    private TrayIconManager? _tray;
    private MiniWindow? _miniWindow;
    private CancellationTokenSource? _translateCts;
    private CancellationTokenSource? _downloadCts;
    private bool _translating;
    private bool _downloading;
    private bool _pendingAuto;
    private bool _trayHintShown;
    private bool _applyingSettings;
    private string? _lastTranslatedSource;

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            Icon = TrayIconManager.CreateWpfIconSource();
        }
        catch
        {
            // 图标生成失败不影响主功能
        }

        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _autoTimer.Tick += OnAutoTimerTick;

        _autoClearTimer = new DispatcherTimer();
        _autoClearTimer.Tick += OnAutoClearTick;

        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
        StateChanged += OnStateChanged;

        RefreshModelStatus();
    }

    private TranslationDirection SelectedDirection =>
        DirectionZhEn.IsChecked == true ? TranslationDirection.ZhToEn :
        DirectionEnZh.IsChecked == true ? TranslationDirection.EnToZh :
        TranslationDirection.Auto;

    private static Brush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        EnsureOnScreen();
        RefreshModelStatus();
        InitializeTray();

        _applyingSettings = true;
        AutoStartCheck.IsChecked = AutoStartManager.IsEnabled();
        MinimizeTrayCheck.IsChecked = App.Settings.MinimizeToTray;
        AutoClearCheck.IsChecked = App.Settings.AutoClearEnabled;
        AutoClearIntervalCombo.IsEnabled = App.Settings.AutoClearEnabled;
        SelectAutoClearInterval(App.Settings.AutoClearSeconds);
        _applyingSettings = false;

        bool anyReady = ModelRepository.IsReady(ModelRepository.ZhEnPair)
                        || ModelRepository.IsReady(ModelRepository.EnZhPair);

        if (anyReady)
        {
            await WarmUpAsync();
        }
        else
        {
            SetHeaderStatus("模型未下载", WarnBrush);
            SetDetail("首次使用请点击“下载模型”（约 190 MB），下载完成后可完全离线使用", isError: true);
        }
    }

    private async Task WarmUpAsync()
    {
        SetHeaderStatus("加载模型…", IdleBrush);
        SetDetail("正在加载本地模型…");

        try
        {
            if (ModelRepository.IsReady(ModelRepository.EnZhPair)
                && !App.SharedService.IsLoaded(ModelRepository.EnZhPair))
            {
                await App.SharedService.LoadAsync(ModelRepository.EnZhPair);
            }

            if (ModelRepository.IsReady(ModelRepository.ZhEnPair)
                && !App.SharedService.IsLoaded(ModelRepository.ZhEnPair))
            {
                SetDetail("正在加载中译英模型…");
                await App.SharedService.LoadAsync(ModelRepository.ZhEnPair);
            }

            RefreshModelStatus();
            SetDetail("模型已加载 · 翻译过程完全离线");
        }
        catch (Exception ex)
        {
            SetHeaderStatus("模型加载失败", ErrorBrush);
            SetDetail("模型加载失败：" + ex.Message, isError: true);
        }
    }

    /// <summary>多屏/缩放比例异常时，避免窗口出现在可视区域之外。</summary>
    private void EnsureOnScreen()
    {
        Rect area = SystemParameters.WorkArea;
        bool outside = Left < area.Left - 32
                       || Top < area.Top - 32
                       || Left + ActualWidth > area.Right + 32
                       || Top + ActualHeight > area.Bottom + 32;

        if (!outside) return;

        Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
        Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
    }

    private void InitializeTray()
    {
        if (_tray is not null) return;

        _tray = new TrayIconManager();
        _tray.ShowMainRequested += OnTrayShowMain;
        _tray.ToggleMiniRequested += OnTrayToggleMini;
        _tray.ExitRequested += OnTrayExit;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        if (MinimizeTrayCheck.IsChecked != true || _tray is null) return;

        Hide();
        WindowState = WindowState.Normal;

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip("离线翻译", "已最小化到系统托盘，双击托盘图标可恢复窗口。");
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingSettings) return;

        bool enabled = AutoStartCheck.IsChecked == true;
        if (AutoStartManager.SetEnabled(enabled))
        {
            SetDetail(enabled ? "已开启开机自动启动" : "已关闭开机自动启动");
        }
        else
        {
            SetDetail("修改开机自启失败", isError: true);
            _applyingSettings = true;
            AutoStartCheck.IsChecked = AutoStartManager.IsEnabled();
            _applyingSettings = false;
        }
    }

    private void OnMinimizeTrayChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingSettings) return;

        App.Settings.MinimizeToTray = MinimizeTrayCheck.IsChecked == true;
        App.Settings.Save();
        SetDetail(App.Settings.MinimizeToTray ? "最小化时将隐藏到系统托盘" : "最小化时显示在任务栏");
    }

    private void OnAutoClearChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingSettings) return;

        App.Settings.AutoClearEnabled = AutoClearCheck.IsChecked == true;
        App.Settings.Save();
        AutoClearIntervalCombo.IsEnabled = App.Settings.AutoClearEnabled;

        if (App.Settings.AutoClearEnabled)
        {
            SetDetail($"翻译后 {DescribeInterval(App.Settings.AutoClearSeconds)}内容未变将自动清空");
        }
        else
        {
            _autoClearTimer.Stop();
            SetDetail("已关闭自动清空");
        }
    }

    private void OnAutoClearIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _applyingSettings) return;

        if (AutoClearIntervalCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && int.TryParse(tag, out int seconds) && seconds > 0)
        {
            App.Settings.AutoClearSeconds = seconds;
            App.Settings.Save();
            _lastTranslatedSource = null; // 间隔变化后重新计时
            SetDetail($"自动清空间隔已设为 {DescribeInterval(seconds)}");
        }
    }

    private void SelectAutoClearInterval(int seconds)
    {
        foreach (var item in AutoClearIntervalCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag && int.TryParse(tag, out int value) && value == seconds)
            {
                AutoClearIntervalCombo.SelectedItem = item;
                return;
            }
        }

        AutoClearIntervalCombo.SelectedIndex = 2; // 默认 5 分钟
    }

    private static string DescribeInterval(int seconds) => seconds switch
    {
        < 60 => $"{seconds} 秒",
        < 3600 when seconds % 60 == 0 => $"{seconds / 60} 分钟",
        _ => $"{seconds} 秒",
    };

    /// <summary>翻译成功后启动自动清空倒计时；再次翻译或修改内容会重置。</summary>
    private void RestartAutoClearTimer(string translatedSource)
    {
        _autoClearTimer.Stop();
        _lastTranslatedSource = translatedSource;

        if (!App.Settings.AutoClearEnabled || App.Settings.AutoClearSeconds <= 0) return;

        _autoClearTimer.Interval = TimeSpan.FromSeconds(App.Settings.AutoClearSeconds);
        _autoClearTimer.Start();
    }

    private void OnAutoClearTick(object? sender, EventArgs e)
    {
        _autoClearTimer.Stop();

        if (AutoClearCheck.IsChecked != true || _lastTranslatedSource is null) return;
        if (InputBox.Text.Trim() != _lastTranslatedSource) return; // 内容已变化，不再清空

        PerformClear("已自动清空");
    }

    private void OnMiniModeClick(object sender, RoutedEventArgs e) => EnterMiniMode();

    private void OnTrayShowMain(object? sender, EventArgs e) => Dispatcher.Invoke(ShowMainWindow);

    private void OnTrayToggleMini(object? sender, EventArgs e) => Dispatcher.Invoke(EnterMiniMode);

    private void OnTrayExit(object? sender, EventArgs e) => Dispatcher.Invoke(() =>
    {
        App.IsShuttingDown = true;
        Application.Current.Shutdown();
    });

    private void ShowMainWindow()
    {
        _miniWindow?.Close();
        _miniWindow = null;

        Show();
        WindowState = WindowState.Normal;
        Activate();
        InputBox.Focus();
    }

    private void EnterMiniMode()
    {
        if (_miniWindow is not null)
        {
            _miniWindow.Activate();
            return;
        }

        _autoTimer.Stop();
        var mini = new MiniWindow(this);
        _miniWindow = mini;
        Hide();
        mini.Show();
        mini.Activate();
    }

    /// <summary>由迷你窗口返回完整界面。</summary>
    internal void RestoreFromMini()
    {
        _miniWindow = null;

        if (App.IsShuttingDown || !IsLoaded) return;

        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void RefreshModelStatus()
    {
        bool zhEnReady = ModelRepository.IsReady(ModelRepository.ZhEnPair);
        bool enZhReady = ModelRepository.IsReady(ModelRepository.EnZhPair);

        if (zhEnReady && enZhReady)
        {
            SetHeaderStatus("就绪 · 本地离线", ReadyBrush);
        }
        else if (zhEnReady || enZhReady)
        {
            SetHeaderStatus("部分模型可用", WarnBrush);
        }
        else
        {
            SetHeaderStatus("模型未下载", WarnBrush);
        }

        DownloadButton.Visibility = zhEnReady && enZhReady ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetHeaderStatus(string text, Brush dotBrush)
    {
        StatusText.Text = text;
        StatusDot.Fill = dotBrush;
    }

    private void SetDetail(string text, bool isError = false)
    {
        StatusDetail.Text = text;
        StatusDetail.Foreground = isError ? ErrorBrush : SecondaryBrush;
    }

    private async Task TranslateAsync()
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            OutputBox.Text = string.Empty;
            OutputPlaceholder.Visibility = Visibility.Visible;
            SetDetail("请输入需要翻译的文本");
            return;
        }

        string pair = TranslationService.ResolvePair(text, SelectedDirection);
        if (!ModelRepository.IsReady(pair))
        {
            SetDetail($"缺少「{ModelRepository.PairDisplayName(pair)}」模型，请先点击“下载模型”", isError: true);
            return;
        }

        _translateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _translateCts = cts;

        _translating = true;
        _pendingAuto = false;
        TranslateButton.IsEnabled = false;
        SetDetail("翻译中…");

        try
        {
            var result = await App.SharedService.TranslateAsync(text, SelectedDirection, cts.Token);
            if (cts.IsCancellationRequested) return;

            OutputBox.Text = result.Text;
            OutputPlaceholder.Visibility = string.IsNullOrEmpty(result.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            SetDetail($"{ModelRepository.PairDisplayName(result.Pair)} · {result.Elapsed.TotalMilliseconds:F0} ms · 本地推理");
            RestartAutoClearTimer(text);
        }
        catch (OperationCanceledException)
        {
            // 用户输入变更导致的取消，忽略
        }
        catch (Exception ex)
        {
            SetDetail("翻译失败：" + ex.Message, isError: true);
        }
        finally
        {
            if (ReferenceEquals(_translateCts, cts))
            {
                _translating = false;
                TranslateButton.IsEnabled = true;

                // 翻译期间用户继续输入，则在结束后补一次翻译
                if (_pendingAuto && AutoTranslateCheck.IsChecked == true) RestartAutoTimer();
            }
        }
    }

    private void OnTranslateClick(object sender, RoutedEventArgs e) => _ = TranslateAsync();

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        bool isEmpty = string.IsNullOrEmpty(InputBox.Text);
        Placeholder.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        CharCountText.Text = $"{InputBox.Text.Length} 字";

        // 内容变化即取消"内容未变则清空"的倒计时，下次翻译后重新计时
        _autoClearTimer.Stop();
        _lastTranslatedSource = null;

        if (AutoTranslateCheck.IsChecked != true) return;
        _pendingAuto = true;
        RestartAutoTimer();
    }

    private void OnAutoTranslateChanged(object sender, RoutedEventArgs e)
    {
        if (AutoTranslateCheck.IsChecked == true && !_translating) RestartAutoTimer();
    }

    private void OnDirectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (AutoTranslateCheck.IsChecked == true && !_translating) RestartAutoTimer();
    }

    private void RestartAutoTimer()
    {
        _autoTimer.Stop();
        if (string.IsNullOrWhiteSpace(InputBox.Text)) return;
        _autoTimer.Start();
    }

    private void OnAutoTimerTick(object? sender, EventArgs e)
    {
        _autoTimer.Stop();
        if (_translating || _downloading) return;
        _ = TranslateAsync();
    }

    private async void OnSwapClick(object sender, RoutedEventArgs e)
    {
        if (DirectionZhEn.IsChecked == true) DirectionEnZh.IsChecked = true;
        else if (DirectionEnZh.IsChecked == true) DirectionZhEn.IsChecked = true;
        else DirectionZhEn.IsChecked = true;

        if (!string.IsNullOrWhiteSpace(InputBox.Text) && !_downloading) await TranslateAsync();
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => PerformClear("已清空");

    private void PerformClear(string statusMessage)
    {
        _autoClearTimer.Stop();
        _lastTranslatedSource = null;
        _translateCts?.Cancel();
        InputBox.Clear();
        OutputBox.Text = string.Empty;
        OutputPlaceholder.Visibility = Visibility.Visible;
        Placeholder.Visibility = Visibility.Visible;
        InputBox.Focus();
        SetDetail(statusMessage);
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        string text = OutputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetDetail("没有可复制的译文");
            return;
        }

        try
        {
            Clipboard.SetText(text);
            SetDetail("译文已复制到剪贴板");
        }
        catch (Exception ex)
        {
            SetDetail("复制失败：" + ex.Message, isError: true);
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;

        _downloading = true;
        DownloadButton.IsEnabled = false;
        TranslateButton.IsEnabled = false;
        DownloadProgressBar.Value = 0;
        DownloadProgressBar.Visibility = Visibility.Visible;
        SetHeaderStatus("下载模型…", WarnBrush);

        using var cts = new CancellationTokenSource();
        _downloadCts = cts;

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            DownloadProgressBar.Value = Math.Clamp(p.OverallFraction * 100, 0, 100);
            SetDetail($"正在下载 {p.FileName} · {p.Message}");
        });

        try
        {
            foreach (string pair in new[] { ModelRepository.ZhEnPair, ModelRepository.EnZhPair })
            {
                if (ModelRepository.IsReady(pair)) continue;
                SetDetail($"准备下载「{ModelRepository.PairDisplayName(pair)}」模型…");
                await ModelRepository.EnsureAsync(pair, progress, cts.Token);
            }

            DownloadProgressBar.Value = 100;
            SetDetail("模型下载完成，正在加载…");
            await WarmUpAsync();
        }
        catch (OperationCanceledException)
        {
            SetDetail("已取消模型下载", isError: true);
        }
        catch (Exception ex)
        {
            SetDetail("模型下载失败：" + ex.Message, isError: true);
        }
        finally
        {
            _downloadCts = null;
            _downloading = false;
            DownloadButton.IsEnabled = true;
            TranslateButton.IsEnabled = true;
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            RefreshModelStatus();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            if (!_downloading) _ = TranslateAsync();
        }
        else if (e.Key == Key.Escape)
        {
            _autoTimer.Stop();
            e.Handled = true;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _autoTimer.Stop();
        _downloadCts?.Cancel();
        _translateCts?.Cancel();
        _tray?.Dispose();
    }
}
