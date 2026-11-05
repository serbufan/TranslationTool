using System.Windows;
using TranslationTool.Services;

namespace TranslationTool;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    /// <summary>主窗口与迷你窗口共享的翻译引擎。</summary>
    public static TranslationService SharedService { get; } = new();

    public static AppSettings Settings { get; private set; } = new();

    public static bool IsShuttingDown { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = AppSettings.Load();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsShuttingDown = true;
        SharedService.Dispose();
        base.OnExit(e);
    }
}
