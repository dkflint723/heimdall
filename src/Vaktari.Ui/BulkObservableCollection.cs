using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Vaktari.Ui;

/// <summary>
/// ObservableCollection raises one CollectionChanged per Add, which at 200k
/// items is the difference between a fast listing and a hung UI. This adds a
/// whole batch against the protected backing list and raises a single Reset.
///
/// Reset is cheap here specifically because the panel is virtualizing — only
/// the realized containers (a screenful) get rebuilt, not the whole collection.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IReadOnlyList<T> items)
    {
        if (items.Count == 0) return;

        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void Reset()
    {
        Items.Clear();
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Swap the whole contents in one notification. Used by sorting.</summary>
    public void ReplaceAll(IReadOnlyList<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
