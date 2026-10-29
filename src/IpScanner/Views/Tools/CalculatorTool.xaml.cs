using System.Windows.Controls;
using IpScanner.ViewModels.Tools;

namespace IpScanner.Views.Tools;

public partial class CalculatorTool : UserControl
{
    public CalculatorTool(CalculatorToolViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
    }
}
