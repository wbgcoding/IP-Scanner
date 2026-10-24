#if DEBUG
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IpScanner.Core.Localization;

namespace IpScanner.Views.Review;

/// <summary>The debug-only surface tool. <c>IP-Scanner.exe --render|--shot|--review|--ids
/// &lt;id|group|all&gt; [WxH] [locale,...|all] [dark|light|both] [--out dir] [--scale percent]</c>.
/// <c>render</c> draws each surface with the window offscreen, <c>shot</c> with the window on screen;
/// both write one PNG per surface, locale and theme plus clip-report.json. A launch without
/// arguments reads the same line from a file named after the executable plus ".review.args".
/// Compiled out of Release builds.</summary>
internal static class ReviewTool
{
    internal static bool Active { get; private set; }

    private static readonly string[] AllLocales = { "en", "de", "fr", "es", "pt", "ru", "ar", "zh", "ja", "hi" };

    private sealed record Options(string Mode, string Selector, int Width, int Height, string[] Locales,
        string[] Themes, string OutDir, int Scale);

    internal static bool TryStart(Application app, string[] args)
    {
        if (args.Length == 0)
        {
            var sidecar = Path.ChangeExtension(Environment.ProcessPath ?? "", ".review.args");
            if (!File.Exists(sidecar)) return false;
            args = SplitArgs(File.ReadAllText(sidecar));
        }
        var options = Parse(args);
        if (options is null) return false;

        Active = true;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // The startup window still opens once this returns; give it the sample config and the theme
        // so it builds without touching real data, and close it before the first surface.
        var temp = Path.Combine(Path.GetTempPath(), "ipscanner-review-" + Environment.ProcessId);
        Directory.CreateDirectory(temp);
        ThemeManager.Apply(app, options.Themes[0]);
        var bootContext = new ReviewContext("en", options.Themes[0], options.Width, options.Height, options.Scale, temp, false);
        MainWindow.ReviewSetSealedHome(temp);
        App.ReviewSetBootConfig(ReviewSurfaces.SampleConfig(bootContext));
        app.Dispatcher.InvokeAsync(async () =>
        {
            int code = 1;
            foreach (Window w in app.Windows.Cast<Window>().ToList()) w.Close();
            try { code = await RunAsync(app, options, temp); }
            catch (Exception ex) { Log(options.OutDir, ex.ToString()); }
            finally
            {
                try { File.WriteAllText(Path.Combine(options.OutDir, "review.done"), code.ToString(CultureInfo.InvariantCulture)); }
                catch { /* the exit code still carries the result */ }
                if (options.Mode != "review") app.Shutdown(code);
            }
        });
        return true;
    }

    private static string[] SplitArgs(string line) =>
        Regex.Matches(line, "\"([^\"]*)\"|(\\S+)").Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();

    private static Options? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--render" or "--shot" or "--review" or "--ids")) return null;
        string mode = args[0][2..];
        string selector = "all", outDir = Path.Combine(AppContext.BaseDirectory, "review-out");
        int w = 1320, h = 720, scale = 100;
        var locales = new[] { "en", "de" };
        var themes = new[] { "dark", "light" };
        bool haveSelector = false;
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (a == "--scale" && i + 1 < args.Length) scale = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (Regex.IsMatch(a, @"^\d+x\d+$"))
            {
                var p = a.Split('x');
                w = int.Parse(p[0], CultureInfo.InvariantCulture); h = int.Parse(p[1], CultureInfo.InvariantCulture);
            }
            else if (a == "both") themes = new[] { "dark", "light" };
            else if (a.Split(',').All(t => t is "dark" or "light")) themes = a.Split(',');
            else if (haveSelector) locales = a == "all" ? AllLocales : a.Split(',', StringSplitOptions.RemoveEmptyEntries);
            else { selector = a; haveSelector = true; }
        }
        return new Options(mode, selector, w, h, locales, themes, Path.GetFullPath(outDir), scale);
    }

    private static void Log(string outDir, string text)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            File.AppendAllText(Path.Combine(outDir, "review.log"), $"[{DateTime.Now:HH:mm:ss}] {text}\n");
        }
        catch { /* logging is best-effort */ }
    }

    private static bool Selected(Surface s, string selector) =>
        selector.Split(',', StringSplitOptions.RemoveEmptyEntries).Any(sel =>
            sel == "all" || sel == s.Id || sel == s.Group);

    private static async Task<int> RunAsync(Application app, Options o, string temp)
    {
        Directory.CreateDirectory(o.OutDir);
        try
        {
            var surfaces = await ReviewSurfaces.BuildAllAsync(temp);
            if (o.Mode == "ids")
            {
                var sb = new StringBuilder();
                foreach (var g in surfaces.Select(s => s.Group).Distinct()) sb.AppendLine($"{g}\t(group)");
                foreach (var s in surfaces) sb.AppendLine($"{s.Id}\t{s.Group}");
                File.WriteAllText(Path.Combine(o.OutDir, "ids.txt"), sb.ToString());
                return 0;
            }

            var chosen = surfaces.Where(s => Selected(s, o.Selector)).ToList();
            if (chosen.Count == 0) { Log(o.OutDir, $"no surface matches '{o.Selector}'"); return 2; }

            var findings = new List<ClipFinding>();
            var files = new List<string>();
            var review = new List<(string Id, string File)>();
            foreach (var locale in o.Locales)
            {
                Loc.SetLanguage(locale);
                foreach (var theme in o.Themes)
                {
                    Views.ThemeManager.Switch(app, theme);
                    foreach (var surface in chosen)
                    {
                        var ctx = new ReviewContext(locale, theme, o.Width, o.Height, o.Scale, temp, o.Mode != "render");
                        try { await CaptureAsync(surface, ctx, o, findings, files, review); }
                        catch (Exception ex) { Log(o.OutDir, $"{surface.Id} {locale} {theme}: {ex}"); }
                    }
                }
            }

            WriteReport(o, chosen, findings, files);
            if (o.Mode == "review") ShowBoard(app, review);
            return 0;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* temp folder is best-effort */ }
        }
    }

    private static readonly List<string> Sizes = new();

    private static string Stem(Surface s, ReviewContext c, Options o) =>
        $"{s.Id}.{c.Locale}.{c.Theme}.{c.Width}x{c.Height}" + (c.Scale != 100 ? $".s{c.Scale}" : "");

    private static async Task CaptureAsync(Surface surface, ReviewContext ctx, Options o, List<ClipFinding> findings,
        List<string> files, List<(string Id, string File)> review)
    {
        var shown = await surface.Open(ctx);
        try
        {
            await ReviewSurfaces.SettleAsync(o.Mode == "render" ? 120 : 350);
            string stem = Stem(surface, ctx, o);
            FrameworkElement? popup = shown.Popup?.Invoke();
            

            if (shown.Detached)
            {
                var content = (FrameworkElement)shown.Primary.Content;
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                content.Arrange(new Rect(content.DesiredSize));
                content.UpdateLayout();
                findings.AddRange(ReviewCapture.Analyze(surface.Id, ctx.Locale, ctx.Theme, content));
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawRectangle(shown.Primary.Background, null, new Rect(content.DesiredSize));
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(content.DesiredSize.Width), (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
                bmp.Render(dv);
                bmp.Render(content);
                Save(bmp, o, stem + ".png", files, review, surface.Id);
                return;
            }

            findings.AddRange(ReviewCapture.Analyze(surface.Id, ctx.Locale, ctx.Theme, shown.Primary));
            string info = $"{surface.Id} {ctx.Locale} {ctx.Theme}: window {shown.Primary.ActualWidth:0}x{shown.Primary.ActualHeight:0}";
            if (shown.Primary is MainWindow mw && mw.ReviewElement("ScaledContent") is { } sc)
                info += $", content {sc.ActualWidth:0}x{sc.ActualHeight:0}";
            Sizes.Add(info);
            var full = ReviewCapture.Render(shown.Primary);
            if (popup is not null)
            {
                findings.AddRange(ReviewCapture.Analyze(surface.Id + "#popup", ctx.Locale, ctx.Theme, popup));
                Save(ReviewCapture.Render(popup), o, stem + ".png", files, review, surface.Id);
                Save(full, o, stem + ".window.png", files, review, null);
            }
            else
            {
                var image = shown.CropTo is { } crop ? ReviewCapture.Crop(full, shown.Primary, crop) : full;
                Save(image, o, stem + ".png", files, review, surface.Id);
            }
        }
        finally
        {
            ReviewSurfaces.CloseAll(shown);
            await ReviewSurfaces.SettleAsync(30);
        }
    }

    private static void Save(BitmapSource image, Options o, string name, List<string> files,
        List<(string Id, string File)> review, string? reviewId)
    {
        var path = Path.Combine(o.OutDir, name);
        ReviewCapture.SavePng(image, path);
        files.Add(name);
        if (reviewId is not null) review.Add((reviewId, path));
    }

    private static void WriteReport(Options o, List<Surface> chosen, List<ClipFinding> findings, List<string> files)
    {
        bool narrowFlagged = findings.Any(f => f.Surface == "selftest.clip" && f.Text.Contains("narrowed"));
        bool fitsFlagged = findings.Any(f => f.Surface == "selftest.clip" && f.Text.Contains("Fits"));
        var real = findings.Where(f => !f.Surface.StartsWith("selftest.", StringComparison.Ordinal)).ToList();
        var report = new
        {
            mode = o.Mode,
            size = $"{o.Width}x{o.Height}",
            scale = o.Scale,
            locales = o.Locales,
            themes = o.Themes,
            surfaces = chosen.Select(s => s.Id).ToArray(),
            files,
            sizes = Sizes,
            positiveControl = chosen.Any(s => s.Id == "selftest.clip")
                ? new { narrowFlagged, fitsFlagged } : null,
            count = real.Count,
            findings = real.Select(f => new
            {
                surface = f.Surface, locale = f.Locale, theme = f.Theme, kind = f.Kind, path = f.Path,
                text = f.Text, neededWidth = f.NeededWidth, availableWidth = f.AvailableWidth,
            }),
            selftestFindings = findings.Where(f => f.Surface.StartsWith("selftest.", StringComparison.Ordinal)).Select(f => new { f.Surface, f.Kind, f.Text }),
        };
        File.WriteAllText(Path.Combine(o.OutDir, "clip-report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    // Review mode: every captured surface on one scrollable board with a numbered question list.
    private static void ShowBoard(Application app, List<(string Id, string File)> review)
    {
        var panel = new WrapPanel { Margin = new Thickness(12) };
        int n = 0;
        foreach (var (id, file) in review)
        {
            var img = new BitmapImage();
            img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.UriSource = new Uri(file); img.EndInit();
            var tile = new StackPanel { Margin = new Thickness(8), Width = 640 };
            tile.Children.Add(new TextBlock { Text = $"{++n}. {id}", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
            tile.Children.Add(new Image { Source = img, Stretch = Stretch.Uniform, MaxHeight = 420 });
            tile.Children.Add(new TextBlock { Text = "Anything clipped, overlapping or misaligned?", Margin = new Thickness(0, 4, 0, 0) });
            panel.Children.Add(tile);
        }
        var win = new Window { Title = "Surface review", Width = 1400, Height = 900, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        win.Closed += (_, _) => app.Shutdown(0);
        win.Show();
    }
}
#endif
