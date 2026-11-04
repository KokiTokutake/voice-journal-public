using System.Windows.Controls;

namespace VoiceJournal.App.Views;

public partial class RecordReviewView : UserControl
{
    public RecordReviewView() => InitializeComponent();

    // Evidence chips select a segment that may be far down the list: bring it into view.
    private void OnSegmentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && list.SelectedItem is { } item) list.ScrollIntoView(item);
    }
}
