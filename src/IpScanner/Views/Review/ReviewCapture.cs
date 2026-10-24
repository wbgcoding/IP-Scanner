#if DEBUG
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IpScanner.Views.Review;

/// <summary>One text or bounds problem found in a live surface.</summary>
internal sealed record ClipFinding(string Surface, string Locale, string Theme, string Kind, string Path,
    string Text, double NeededWidth, double AvailableWidth);

internal static class ReviewCapture
{
    // Render of a laid-out element at its device resolution, taken from the live visual tree.
    internal static BitmapSource Render(FrameworkElement root)
    {
        root.UpdateLayout();
        // One pixel per device-independent unit, so the picture shows what a 100 percent display shows.
        int w = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        int h = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(visual);
        return bmp;
    }

    // Crops a rendered window to the rectangle of one of its elements.
    internal static BitmapSource Crop(BitmapSource full, FrameworkElement window, FrameworkElement part)
    {
        var rect = part.TransformToVisual(window).TransformBounds(new Rect(part.RenderSize));
        int x = Math.Clamp((int)Math.Floor(rect.X), 0, full.PixelWidth - 1);
        int y = Math.Clamp((int)Math.Floor(rect.Y), 0, full.PixelHeight - 1);
        int w = Math.Clamp((int)Math.Ceiling(rect.Width), 1, full.PixelWidth - x);
        int h = Math.Clamp((int)Math.Ceiling(rect.Height), 1, full.PixelHeight - y);
        return new CroppedBitmap(full, new Int32Rect(x, y, w, h));
    }

    internal static void SavePng(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ── clip analysis ──

    internal static List<ClipFinding> Analyze(string surface, string locale, string theme, DependencyObject root)
    {
        var found = new List<ClipFinding>();
        Walk(root, root, found, surface, locale, theme);
        return found;
    }

    private static void Walk(DependencyObject node, DependencyObject root, List<ClipFinding> found,
        string surface, string locale, string theme)
    {
        if (node is UIElement ui && ui.Visibility != Visibility.Visible) return;
        // A scroll bar draws its own parts (arrow buttons, track, thumb) flush against its edges.
        if (node is ScrollBar) return;
        // A zero-width or zero-height element (the collapsed sidebar) shows none of its subtree.
        if (!ReferenceEquals(node, root) && node is FrameworkElement { ActualWidth: <= 0 } or FrameworkElement { ActualHeight: <= 0 }) return;
        if (node is FrameworkElement fe)
        {
            CheckText(fe, found, surface, locale, theme);
            CheckBounds(fe, root, found, surface, locale, theme);
        }
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i), root, found, surface, locale, theme);
    }

    private static void CheckText(FrameworkElement fe, List<ClipFinding> found, string surface, string locale, string theme)
    {
        string text; TextWrapping wrap; TextTrimming trim; double size; FontFamily family; FontStyle style;
        FontWeight weight; FontStretch stretch; FlowDirection flow;
        switch (fe)
        {
            case TextBlock tb:
                text = tb.Text; wrap = tb.TextWrapping; trim = tb.TextTrimming; size = tb.FontSize; family = tb.FontFamily;
                style = tb.FontStyle; weight = tb.FontWeight; stretch = tb.FontStretch; flow = tb.FlowDirection; break;
            case AccessText at:
                text = at.Text; wrap = at.TextWrapping; trim = at.TextTrimming; size = at.FontSize; family = at.FontFamily;
                style = at.FontStyle; weight = at.FontWeight; stretch = at.FontStretch; flow = at.FlowDirection; break;
            default: return;
        }
        if (string.IsNullOrWhiteSpace(text) || fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return;
        if (trim != TextTrimming.None) return;

        var slot = LayoutInformation.GetLayoutSlot(fe);
        double availW = slot.Width - fe.Margin.Left - fe.Margin.Right;
        double availH = slot.Height - fe.Margin.Top - fe.Margin.Bottom;
        var dpi = VisualTreeHelper.GetDpi(fe).PixelsPerDip;
        var ft = new FormattedText(text.Replace("_", ""), CultureInfo.CurrentUICulture, flow,
            new Typeface(family, style, weight, stretch), size, Brushes.Black, dpi);
        if (wrap == TextWrapping.NoWrap && ft.WidthIncludingTrailingWhitespace > availW + 1.0)
            found.Add(new ClipFinding(surface, locale, theme, "text-width", PathOf(fe), text,
                Math.Round(ft.WidthIncludingTrailingWhitespace, 1), Math.Round(availW, 1)));
        else if (fe.ActualHeight > availH + 1.0)
            found.Add(new ClipFinding(surface, locale, theme, "text-height", PathOf(fe), text,
                Math.Round(fe.ActualHeight, 1), Math.Round(availH, 1)));
    }

    private static void CheckBounds(FrameworkElement fe, DependencyObject root, List<ClipFinding> found,
        string surface, string locale, string theme)
    {
        if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0) return;
        if (VisualTreeHelper.GetParent(fe) is not FrameworkElement parent) return;
        if (ReferenceEquals(fe, root)) return;
        if (parent is Canvas or ScrollContentPresenter or Popup or Adorner or AdornerLayer or AdornerDecorator) return;
        if (fe is Popup or Adorner or ScrollBar or Track or Thumb) return;
        if (fe.RenderTransform is not null && !fe.RenderTransform.Value.IsIdentity) return;
        if (InsideScrollContent(fe)) return;
        if (fe.Opacity <= 0) return;
        Rect r;
        try { r = fe.TransformToVisual(parent).TransformBounds(new Rect(fe.RenderSize)); }
        catch { return; }
        var (canH, canV) = ScrollableDirections(fe);
        double over = Overflow(r, parent.ActualWidth, parent.ActualHeight, canH, canV);
        if (over > 1.5)
            found.Add(new ClipFinding(surface, locale, theme, "bounds", PathOf(fe), (fe as TextBlock)?.Text ?? "",
                Math.Round(r.Width, 1), Math.Round(parent.ActualWidth, 1)));
    }

    // Largest overhang of a child rectangle past its parent. A direction the content can be
    // scrolled in is not counted: running past the edge there is what scrolling is for.
    internal static double Overflow(Rect child, double parentWidth, double parentHeight, bool scrollsH, bool scrollsV)
    {
        double h = scrollsH ? 0 : Math.Max(-child.Left, child.Right - parentWidth);
        double v = scrollsV ? 0 : Math.Max(-child.Top, child.Bottom - parentHeight);
        return Math.Max(h, v);
    }

    // Whether a scroll viewer around the element (also the one inside a data grid) has room to scroll.
    private static (bool Horizontal, bool Vertical) ScrollableDirections(DependencyObject d)
    {
        bool h = false, v = false;
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer sv)
            {
                h |= sv.ScrollableWidth > 0.5;
                v |= sv.ScrollableHeight > 0.5;
                // A data grid without rows has nothing to scroll yet, but its headers and cells
                // scroll as soon as rows arrive, so an automatic or visible bar counts as room.
                if (sv.TemplatedParent is DataGrid dg)
                {
                    h |= dg.HorizontalScrollBarVisibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible;
                    v |= dg.VerticalScrollBarVisibility is ScrollBarVisibility.Auto or ScrollBarVisibility.Visible;
                }
            }
        return (h, v);
    }

    private static bool InsideScrollContent(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollContentPresenter) return true;
        return false;
    }

    private static string PathOf(DependencyObject d)
    {
        var parts = new List<string>();
        for (var p = d; p is not null && parts.Count < 8; p = VisualTreeHelper.GetParent(p))
        {
            var name = (p as FrameworkElement)?.Name;
            parts.Add(string.IsNullOrEmpty(name) ? p.GetType().Name : $"{p.GetType().Name}[{name}]");
            if (p is Window) break;
        }
        parts.Reverse();
        return string.Join(" > ", parts);
    }
}
#endif
