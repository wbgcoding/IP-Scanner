using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace IpScanner.Core.Localization;

/// <summary>Bindable localization source. XAML binds via {loc:L Key};
/// Refresh() re-evaluates every bound text after a language change.</summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Instance { get; } = new();

    public string this[string key] =>
        typeof(Loc).GetProperty(key)?.GetValue(null) as string ?? key;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>{loc:L Key} — live-updating replacement for {x:Static loc:Loc.Key}.</summary>
public sealed class LExtension : MarkupExtension
{
    public string Key { get; set; }

    public LExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = LocSource.Instance, Mode = BindingMode.OneWay }
            .ProvideValue(serviceProvider);
}
