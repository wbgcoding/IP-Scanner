using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace IpScanner.Tests;

/// <summary>A long check box label wraps inside the available width instead of running past it.</summary>
public class CheckBoxWrapTests
{
    [Fact]
    public async Task LongLabel_WrapsWithinWidth()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var box = new CheckBox { Content = string.Join(" ", Enumerable.Repeat("word", 40)), Width = 300 };
            box.Measure(new Size(300, double.PositiveInfinity));
            box.Arrange(new Rect(0, 0, 300, box.DesiredSize.Height));
            box.UpdateLayout();

            var single = new CheckBox { Content = "word", Width = 300 };
            single.Measure(new Size(300, double.PositiveInfinity));

            Assert.True(box.DesiredSize.Width <= 300, $"width {box.DesiredSize.Width}");
            Assert.True(box.DesiredSize.Height > single.DesiredSize.Height * 1.5, $"height {box.DesiredSize.Height} vs {single.DesiredSize.Height}");
            return Task.CompletedTask;
        });
    }
}
