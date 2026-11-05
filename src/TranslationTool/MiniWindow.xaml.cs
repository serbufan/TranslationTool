using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TranslationTool.Services;

namespace TranslationTool;

/// <summary>
/// 迷你模式：置顶小窗，上方为输入框，得到译文后下方自动展开显示。
/// </summary>
public partial class MiniWindow : Window
{
    private static readonly System.Windows.Media.Brush IdleBrush = CreateBrush(0x9A, 0xA4, 0xB8);
    private static readonly System.Windows.Media.Brush ErrorBrush = CreateBrush(0xD9, 0x53, 0x4F);

    private readonly MainWindow _mainWindow;
    private readonly DispatcherTimer _autoTimer;
    private readonly DispatcherTimer _autoClearTimer;
    private CancellationTokenSource? _translateCts;
    private TranslationDirection _direction = TranslationDirection.Auto;
    private string? _lastTranslatedSource;

    public MiniWindow(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;

        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _autoTimer.Tick += OnAutoTimerTick;

        _autoClearTimer = new DispatcherTimer();
        _autoClearTimer.Tick += OnAutoClearTick;

        Closed += OnMiniClosed;
        PositionAtBottomRight();
    }

    private static System.Windows.Media.Brush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void PositionAtBottomRight()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Right - Width - 28;
        Top = area.Bottom - 320;
    }

    private void OnMiniContentRendered(object sender, EventArgs e)
    {
        Rect area = SystemParameters.WorkArea;
        Top = Math.Max(area.Top, area.Bottom - ActualHeight - 28);
        InputBox.Focus();
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.Foreground = Topmost
            ? CreateBrush(0x2F, 0x6F, 0xED)
            : CreateBrush(0x7A, 0x84, 0x9B);
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) => Close();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnDirectionClick(object sender, RoutedEventArgs e)
    {
        _direction = _direction switch
        {
            TranslationDirection.Auto => TranslationDirection.ZhToEn,
            TranslationDirection.ZhToEn => TranslationDirection.EnToZh,
            _ => TranslationDirection.Auto,
        };

        DirectionButton.Content = _direction switch
        {
            TranslationDirection.ZhToEn => "中→英",
            TranslationDirection.EnToZh => "英→中",
            _ => "自动",
        };

        if (InputBox.Text.Trim().Length > 0) _ = TranslateAsync();
    }

    private void OnMiniPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _autoTimer.Stop();
            _ = TranslateAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 内容变化即取消自动清空倒计时，下次翻译后重新计时
        _autoClearTimer.Stop();
        _lastTranslatedSource = null;

        if (InputBox.Text.Trim().Length == 0)
        {
            _autoTimer.Stop();
            OutputCard.Visibility = Visibility.Collapsed;
            OutputBox.Clear();
            SetStatus(string.Empty);
            return;
        }

        _autoTimer.Stop();
        _autoTimer.Start();
    }

    private void OnAutoTimerTick(object? sender, EventArgs e)
    {
        _autoTimer.Stop();
        _ = TranslateAsync();
    }

    private async Task TranslateAsync()
    {
        string text = InputBox.Text.Trim();
        if (text.Length == 0) return;

        string pair = TranslationService.ResolvePair(text, _direction);
        if (!ModelRepository.IsReady(pair))
        {
            SetStatus($"缺少「{ModelRepository.PairDisplayName(pair)}」模型，请在主窗口下载", isError: true);
            return;
        }

        _translateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _translateCts = cts;

        SetStatus("翻译中…");
        try
        {
            var result = await App.SharedService.TranslateAsync(text, _direction, cts.Token);
            if (cts.IsCancellationRequested) return;

            OutputBox.Text = result.Text;
            OutputCard.Visibility = Visibility.Visible;
            SetStatus($"{ModelRepository.PairDisplayName(result.Pair)} · {result.Elapsed.TotalMilliseconds:F0} ms");
            RestartAutoClearTimer(text);
        }
        catch (OperationCanceledException)
        {
            // 输入继续变化导致的取消
        }
        catch (Exception ex)
        {
            SetStatus("翻译失败：" + ex.Message, isError: true);
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputBox.Text)) return;
        try
        {
            Clipboard.SetText(OutputBox.Text);
            SetStatus("已复制");
        }
        catch (Exception ex)
        {
            SetStatus("复制失败：" + ex.Message, isError: true);
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => PerformClear("已清空");

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

        if (!App.Settings.AutoClearEnabled || _lastTranslatedSource is null) return;
        if (InputBox.Text.Trim() != _lastTranslatedSource) return; // 内容已变化，不再清空

        PerformClear("已自动清空");
    }

    private void PerformClear(string statusMessage)
    {
        _autoClearTimer.Stop();
        _lastTranslatedSource = null;
        _translateCts?.Cancel();
        InputBox.Clear();
        OutputBox.Clear();
        OutputCard.Visibility = Visibility.Collapsed;
        Placeholder.Visibility = Visibility.Visible;
        InputBox.Focus();
        SetStatus(statusMessage);
    }

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? ErrorBrush : IdleBrush;
    }

    private void OnMiniClosed(object? sender, EventArgs e)
    {
        _autoTimer.Stop();
        _autoClearTimer.Stop();
        _translateCts?.Cancel();

        if (App.IsShuttingDown) return;

        try
        {
            _mainWindow.RestoreFromMini();
        }
        catch
        {
            // 系统关机等场景下窗口已不可用
        }
    }
}
