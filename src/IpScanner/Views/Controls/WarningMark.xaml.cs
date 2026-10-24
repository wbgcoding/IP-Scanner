using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace IpScanner.Views.Controls;

/// <summary>Small warning icon placed next to an option that is noisy or intrusive on the network.
/// Its tooltip (set at the usage site) says what the option triggers.</summary>
public partial class WarningMark : UserControl
{
    public WarningMark()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (ToolTip is string text) AutomationProperties.SetName(this, text);
        };
    }
}
