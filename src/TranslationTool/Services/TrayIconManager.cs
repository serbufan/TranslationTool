using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingBrushes = System.Drawing.Brushes;
using DrawingColor = System.Drawing.Color;
using DrawingFontStyle = System.Drawing.FontStyle;
using WpfImaging = System.Windows.Interop.Imaging;

namespace TranslationTool.Services;

/// <summary>
/// 系统托盘图标：双击显示主窗口，右键菜单提供迷你模式与退出。
/// 图标为运行时绘制的"译"字徽标，不依赖外部资源文件。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private static readonly Lazy<Icon> AppIcon = new(CreateAppIcon);

    private readonly NotifyIcon _notifyIcon;

    /// <summary>单击/双击托盘图标：由主窗口决定恢复主界面还是迷你窗口。</summary>
    public event EventHandler? ActivateRequested;
    public event EventHandler? ShowMainRequested;
    public event EventHandler? ToggleMiniRequested;
    public event EventHandler? ExitRequested;

    public TrayIconManager()
    {
        var menu = new ContextMenuStrip();

        var showItem = new ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowMainRequested?.Invoke(this, EventArgs.Empty);

        var miniItem = new ToolStripMenuItem("迷你模式");
        miniItem.Click += (_, _) => ToggleMiniRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        menu.Items.AddRange(new ToolStripItem[] { showItem, miniItem, new ToolStripSeparator(), exitItem });

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIcon.Value,
            Text = "离线翻译 · Offline Translator",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ActivateRequested?.Invoke(this, EventArgs.Empty);
        };
        _notifyIcon.DoubleClick += (_, _) => ActivateRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>生成 WPF 窗口图标（与托盘同源）。</summary>
    public static ImageSource CreateWpfIconSource()
    {
        return WpfImaging.CreateBitmapSourceFromHIcon(
            AppIcon.Value.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    public void ShowBalloonTip(string title, string text)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(1500);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var background = new SolidBrush(DrawingColor.FromArgb(47, 111, 237));
            using var path = new GraphicsPath();
            int radius = 8;
            path.AddArc(0, 0, radius * 2, radius * 2, 180, 90);
            path.AddArc(32 - radius * 2, 0, radius * 2, radius * 2, 270, 90);
            path.AddArc(32 - radius * 2, 32 - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(0, 32 - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();
            graphics.FillPath(background, path);

            using var font = new Font("Microsoft YaHei UI", 15f, DrawingFontStyle.Bold, GraphicsUnit.Pixel);
            var textSize = graphics.MeasureString("译", font);
            graphics.DrawString(
                "译",
                font,
                DrawingBrushes.White,
                (32 - textSize.Width) / 2f,
                (32 - textSize.Height) / 2f);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
