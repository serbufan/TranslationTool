using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TranslationTool.Services.Ocean;

/// <summary>
/// ascii-sharks 的 engine.ts 移植：把世界与渲染器接到 WPF 的 Image 上。
/// 固定步长模拟（1/60s，每帧最多 4 步），通过 CompositionTarget.Rendering
/// 驱动；宿主不可见或窗口最小化时自动暂停。
/// </summary>
public sealed class OceanEngine : IDisposable
{
    private const double SimStep = 1 / 60.0;
    private const int MaxSteps = 4;
    /// <summary>ASCII 动画 30fps 已足够流畅，并可显著降低 CPU 占用。</summary>
    private const double RenderInterval = 1 / 30.0;

    private readonly Image _host;
    private readonly OceanWorld _world = new();
    private readonly OceanRenderer _renderer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _last;
    private double _acc;
    private double _renderAcc;
    private bool _running;
    private bool _disposed;

    public OceanEngine(Image host)
    {
        _host = host;
        _last = _clock.Elapsed;
        CompositionTarget.Rendering += OnRendering;
        _running = true;
    }

    /// <summary>width/height 为 WPF 布局尺寸（DIP）。</summary>
    public void Resize(double width, double height)
    {
        if (_disposed || width < 1 || height < 1) return;

        // 按逻辑像素渲染，Image 用 Stretch=Fill 放大：ASCII 网格在设备像素下
        // 像素量减少 2 倍以上，高 DPI 屏上依然清晰可辨。
        int pxW = (int)Math.Round(width);
        int pxH = (int)Math.Round(height);
        _renderer.Resize(pxW, pxH);
        _host.Source = _renderer.Bitmap;
        _world.Resize(pxW, pxH, _renderer.CellWidth, _renderer.CellHeight);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || !_running) return;
        var now = _clock.Elapsed;
        double dt = (now - _last).TotalSeconds;
        _last = now;
        if (dt > 0.1) dt = 0.1;
        if (dt <= 0) return;

        _acc += dt;
        int steps = 0;
        while (_acc >= SimStep && steps < MaxSteps)
        {
            _world.Step(SimStep);
            _acc -= SimStep;
            steps++;
        }
        if (steps == MaxSteps) _acc = 0;

        // 渲染限帧：模拟仍按 60Hz 推进，画面 30fps。
        _renderAcc += dt;
        if (_renderAcc < RenderInterval) return;
        _renderAcc = 0;
        _renderer.Render(_world, dt);
    }

    public void SetRunning(bool running)
    {
        if (_running == running || _disposed) return;
        _running = running;
        if (running) _last = _clock.Elapsed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionTarget.Rendering -= OnRendering;
        _renderer.Dispose();
        GC.SuppressFinalize(this);
    }
}
