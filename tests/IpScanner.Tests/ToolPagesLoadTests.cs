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
            var ping = new PingRouteTool(new PingRouteToolViewModel());
            var calculator = new CalculatorTool(new CalculatorToolViewModel());
            Assert.NotNull(ping);
            Assert.NotNull(calculator);
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
}
