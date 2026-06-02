using System.Windows;
using System.Windows.Media;

namespace IpScanner.Views.Controls;

/// <summary>
/// A horizontal bar split into up to three colored segments (fractions 0..1).
/// Derives from FrameworkElement (pure custom draw) so OnRender always runs
/// without needing a theme style/template. Used for the device
/// (online/offline/unknown) and ping (success/fail/skipped) header bars.
/// </summary>
public sealed class SegmentedProgressBar : FrameworkElement
{
    public static readonly DependencyProperty Fraction1Property =
        DependencyProperty.Register(nameof(Fraction1), typeof(double), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Fraction2Property =
        DependencyProperty.Register(nameof(Fraction2), typeof(double), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Fraction3Property =
        DependencyProperty.Register(nameof(Fraction3), typeof(double), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Color1Property =
        DependencyProperty.Register(nameof(Color1), typeof(Brush), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(Brushes.Green, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Color2Property =
        DependencyProperty.Register(nameof(Color2), typeof(Brush), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(Brushes.Red, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty Color3Property =
        DependencyProperty.Register(nameof(Color3), typeof(Brush), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackProperty =
        DependencyProperty.Register(nameof(Track), typeof(Brush), typeof(SegmentedProgressBar),
            new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction1 { get => (double)GetValue(Fraction1Property); set => SetValue(Fraction1Property, value); }
    public double Fraction2 { get => (double)GetValue(Fraction2Property); set => SetValue(Fraction2Property, value); }
    public double Fraction3 { get => (double)GetValue(Fraction3Property); set => SetValue(Fraction3Property, value); }
    public Brush Color1 { get => (Brush)GetValue(Color1Property); set => SetValue(Color1Property, value); }
    public Brush Color2 { get => (Brush)GetValue(Color2Property); set => SetValue(Color2Property, value); }
    public Brush Color3 { get => (Brush)GetValue(Color3Property); set => SetValue(Color3Property, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double radius = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), radius, radius);
        double x = 0;
        foreach (var (frac, brush) in new[]
                 { (Fraction1, Color1), (Fraction2, Color2), (Fraction3, Color3) })
        {
            double segW = Math.Max(0, Math.Min(1, frac)) * w;
            if (segW <= 0) continue;
            dc.DrawRectangle(brush, null, new Rect(x, 0, segW, h));
            x += segW;
        }
    }
}
