using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IpScanner.Views.Controls;

/// <summary>
/// Renders the spinning logo on its own composition thread (HostVisual +
/// VisualTarget), so the animation stays smooth no matter how busy the main
/// UI thread is during a scan. The worker is a background STA thread and is
/// shut down via its dispatcher - it can never keep the process alive.
/// </summary>
public sealed class SpinningLogo : FrameworkElement
{
    private const double Size = 42, Center = 21;
    private const double GlowRadius = 5;
    private const double SpinRamp = 2.0;            // approach factor per second
    private const double StopThreshold = 3;         // deg/s - snap to rest below this

    private readonly HostVisual _host = new();
    // UI-side wrapper: content is rendered supersampled (Size*dpi px) on the
    // composition thread, then scaled back down here so WPF rasterizes it at the
    // real screen DPI instead of the VisualTarget's fixed 96 - kills pixelation.
    private readonly ContainerVisual _scaleHost = new();
    private Thread? _thread;
    private volatile Dispatcher? _workerDispatcher;
    private volatile bool _shutdownRequested;
    private double _dpiScale = 1;

    // Written by the UI thread, read by the worker (lock-free via Interlocked).
    private long _targetBits = BitConverter.DoubleToInt64Bits(0);
    private long _refBits = BitConverter.DoubleToInt64Bits(360);

    public SpinningLogo()
    {
        _scaleHost.Children.Add(_host);
        AddVisualChild(_scaleHost);
        Loaded += (_, _) => StartWorker();
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _scaleHost;
    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    /// <summary>Set the spin speed in degrees per second (0 = ease to a stop).</summary>
    public void SetSpeed(double degreesPerSecond)
    {
        Interlocked.Exchange(ref _targetBits, BitConverter.DoubleToInt64Bits(degreesPerSecond));
        if (degreesPerSecond > 0)
            Interlocked.Exchange(ref _refBits, BitConverter.DoubleToInt64Bits(degreesPerSecond));
    }

    /// <summary>Stop the render thread (call when the window closes).</summary>
    public void Shutdown()
    {
        _shutdownRequested = true;
        _workerDispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
    }

    private void StartWorker()
    {
        if (_thread is not null) return;
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_dpiScale <= 0) _dpiScale = 1;
        _scaleHost.Transform = new ScaleTransform(1 / _dpiScale, 1 / _dpiScale);
        _scaleHost.Transform.Freeze();
        var bmp = new BitmapImage(new Uri("pack://application:,,,/Resources/logo.png"));
        bmp.Freeze();
        double dpi = _dpiScale;
        _thread = new Thread(() => RenderLoop(bmp, dpi)) { IsBackground = true, Name = "logo-spin" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void RenderLoop(BitmapSource logo, double dpi)
    {
        _workerDispatcher = Dispatcher.CurrentDispatcher;
        if (_shutdownRequested) return;   // window closed before the loop started

        // Everything is drawn at dpi-supersampled pixels; the UI-side _scaleHost
        // shrinks it back to Size DIP so the final rasterization matches the screen.
        double px = Size * dpi, centerPx = Center * dpi, glowPx = GlowRadius * dpi;

        var target = new VisualTarget(_host);
        var spin = new RotateTransform(0, centerPx, centerPx);
        var image = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        using (var dc = image.RenderOpen())
            dc.DrawImage(logo, new Rect(0, 0, px, px));
        var glow = new DrawingVisual();
        var root = new ContainerVisual { Transform = spin };
        root.Children.Add(image);
        root.Children.Add(glow);
        target.RootVisual = root;

        double angle = 0, speed = 0;
        long last = System.Diagnostics.Stopwatch.GetTimestamp();

        void OnRender(object? s, EventArgs e)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = Math.Min(0.033, (now - last) / (double)System.Diagnostics.Stopwatch.Frequency);
            last = now;

            double targetSpeed = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _targetBits));
            speed += (targetSpeed - speed) * Math.Min(1.0, dt * SpinRamp);
            if (targetSpeed == 0 && Math.Abs(speed) < StopThreshold) speed = 0;
            if (speed == 0 && spin.Angle == angle && glow.ContentBounds.IsEmpty) return;

            angle = (angle + speed * dt) % 360;
            spin.Angle = angle;

            // Centre dot glow: brightness follows speed, pulsing with the rotation.
            double reference = BitConverter.Int64BitsToDouble(Interlocked.Read(ref _refBits));
            double norm = Math.Clamp(speed / Math.Max(1, reference), 0, 1);
            double pulse = 0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(angle * Math.PI / 90));
            double opacity = norm * pulse;
            using var dc = glow.RenderOpen();
            if (opacity > 0.01)
            {
                var brush = new RadialGradientBrush(
                    Color.FromArgb((byte)(opacity * 255), 0xDC, 0xFF, 0xD2),
                    Color.FromArgb(0, 0xA6, 0xE3, 0xA1));
                brush.Freeze();
                dc.DrawEllipse(brush, null, new Point(centerPx, centerPx), glowPx, glowPx);
            }
        }

        CompositionTarget.Rendering += OnRender;
        Dispatcher.Run();   // returns after Shutdown()
        CompositionTarget.Rendering -= OnRender;
        target.Dispose();
    }
}
