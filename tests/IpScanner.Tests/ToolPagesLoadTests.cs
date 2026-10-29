using System.Linq;
using System.Threading.Tasks;
using IpScanner.ViewModels.Tools;
using IpScanner.Views.Tools;
using Xunit;

namespace IpScanner.Tests;

/// <summary>Opening a tool page must not throw while its XAML loads (a two-way binding on a
/// read-only view-model property inside a Run does).</summary>
public class ToolPagesLoadTests
{
    [Fact]
    public async Task PingRouteAndCalculatorPages_LoadWithoutException()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var ping = new PingRouteTool(new IpScanner.Core.Models.ScanConfig(), new PingRouteToolViewModel());
            var calculator = new CalculatorTool(new CalculatorToolViewModel());
            var mac = new MacTool(new MacToolViewModel());
            Assert.NotNull(ping);
            Assert.NotNull(calculator);
            Assert.NotNull(mac);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DnsAndCertificatePages_LoadWithoutException()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var dns = new DnsLookupTool(new DnsLookupToolViewModel());
            var certificate = new CertificateTool(new CertificateToolViewModel());
            Assert.NotNull(dns);
            Assert.NotNull(certificate);
            return Task.CompletedTask;
        });
    }
}

/// <summary>The option lists of the scan tool pages are rebuilt on a language switch; the chosen
/// timing, port scope and format must survive that.</summary>
public class ScanToolSelectionTests
{
    [Fact]
    public async Task ReloadLabels_KeepsTimingScopeAndFormat()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var vm = new ServiceDetectionToolViewModel { Timing = 5, Scope = ScanToolViewModel.ScopeAll, Format = IpScanner.Core.Export.ReportFormat.Json };
            var timing = new System.Windows.Controls.ComboBox { SelectedValuePath = nameof(TimingOption.Template) };
            var scope = new System.Windows.Controls.ComboBox { SelectedValuePath = nameof(PortScopeOption.Id) };
            var format = new System.Windows.Controls.ComboBox { SelectedValuePath = nameof(FormatOption.Format) };
            timing.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(vm.TimingOptions)) { Source = vm });
            timing.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new System.Windows.Data.Binding(nameof(vm.Timing)) { Source = vm });
            scope.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(vm.ScopeOptions)) { Source = vm });
            scope.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new System.Windows.Data.Binding(nameof(vm.Scope)) { Source = vm });
            format.SetBinding(System.Windows.Controls.ItemsControl.ItemsSourceProperty, new System.Windows.Data.Binding(nameof(vm.FormatOptions)) { Source = vm });
            format.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new System.Windows.Data.Binding(nameof(vm.Format)) { Source = vm });
            timing.DataContext = scope.DataContext = format.DataContext = vm;
            Assert.Equal(6, timing.Items.Count);
            Assert.Equal(5, vm.Timing);
            Assert.Equal(5, timing.SelectedValue);

            vm.ReloadLabels();

            Assert.Equal(5, vm.Timing);
            Assert.Equal(ScanToolViewModel.ScopeAll, vm.Scope);
            Assert.Equal(IpScanner.Core.Export.ReportFormat.Json, vm.Format);
            Assert.Equal(5, timing.SelectedValue);
            Assert.Equal(ScanToolViewModel.ScopeAll, scope.SelectedValue);
            Assert.Equal(IpScanner.Core.Export.ReportFormat.Json, format.SelectedValue);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task DnsPage_FitsTheNarrowestContentAreaInEveryMode()
    {
        await WpfTestHost.RunAsync(() =>
        {
            var vm = new DnsLookupToolViewModel
            {
                SystemServerProvider = () => new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3", "10.0.0.4", "10.0.0.5", "10.0.0.6" }
                    .Select(System.Net.IPAddress.Parse).ToList(),
            };
            var page = new DnsLookupTool(vm);
            vm.CompareMode = true;
            Measure(page);

            vm.CompareMode = false;
            vm.Server = "not an address";
            Measure(page);
            return Task.CompletedTask;
        });

        static void Measure(DnsLookupTool page)
        {
            // Tools window minimum width (820) minus navigation and margins.
            page.Measure(new System.Windows.Size(560, double.PositiveInfinity));
            Assert.True(page.DesiredSize.Width <= 560.5, $"page needs {page.DesiredSize.Width} px");
        }
    }
}
