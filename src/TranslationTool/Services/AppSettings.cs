using System.IO;
using System.Text.Json;

namespace TranslationTool.Services;

/// <summary>应用设置（%APPDATA%\OfflineTranslator\settings.json）。</summary>
public sealed class AppSettings
{
    /// <summary>最小化时隐藏到系统托盘（否则显示在任务栏）。</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>翻译完成后，内容在设定时间内未变化则自动清空。</summary>
    public bool AutoClearEnabled { get; set; }

    /// <summary>自动清空的等待秒数（默认 5 分钟）。</summary>
    public int AutoClearSeconds { get; set; } = 300;

    /// <summary>启用 ASCII 鲨鱼动态海洋背景。</summary>
    public bool OceanBackground { get; set; }

    /// <summary>停止输入后自动翻译。</summary>
    public bool AutoTranslate { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OfflineTranslator",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 设置写失败不影响主功能
        }
    }
}
