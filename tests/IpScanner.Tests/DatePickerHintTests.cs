using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using IpScanner.Core.Localization;
using IpScanner.Views;
using Xunit;

namespace IpScanner.Tests;

/// <summary>The archive date pickers show a localized hint while empty, in theme colors.</summary>
public class DatePickerHintTests
{
    private static T? Find<T>(DependencyObject root, System.Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t)) return t;
            if (Find(child, match) is { } deeper) return deeper;
        }
        return null;
    }

    [Fact]
    public async Task EmptyDatePicker_ShowsTheLocalizedHint()
    {
        await WpfTestHost.RunAsync(() =>
        {
            Loc.SetLanguage("en");
            var page = new ArchivePage { Width = 900, Height = 600 };
            page.Measure(new Size(900, 600));
            page.Arrange(new Rect(0, 0, 900, 600));
            page.UpdateLayout();

            var from = (DatePicker)page.FindName("FromDate");
            var box = Find<DatePickerTextBox>(from, _ => true);
            Assert.NotNull(box);
            var hint = Find<TextBlock>(box!, t => t.Name == "Hint");
            Assert.NotNull(hint);
            Assert.Equal("Pick a date", hint!.Text);
            Assert.Equal(Visibility.Visible, hint.Visibility);

            from.SelectedDate = new System.DateTime(2026, 1, 2);
            page.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, hint.Visibility);
            return Task.CompletedTask;
        });
    }
}
