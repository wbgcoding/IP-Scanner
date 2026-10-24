using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace IpScanner.ViewModels;

/// <summary>An observable list that can swap its whole content in one change notification, so a bound view
/// rebuilds once instead of once per item (a Clear plus N adds raises N+1 events).</summary>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces the content with <paramref name="items"/>, raising a single Reset.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        var snapshot = new List<T>(items);   // items may be a query over this very collection
        Items.Clear();
        foreach (var item in snapshot) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
