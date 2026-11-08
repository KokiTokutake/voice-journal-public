using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace VoiceJournal.App.ViewModels;

/// <summary>
/// An observable list that can be replaced in one notification (NFR-02): adding the 178 segments of a 25-minute meeting
/// one by one sent 178 change notifications to the list on screen.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var item in items) Items.Add(item);
        }
        finally
        {
            _suppress = false;
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppress) base.OnPropertyChanged(e);
    }
}
