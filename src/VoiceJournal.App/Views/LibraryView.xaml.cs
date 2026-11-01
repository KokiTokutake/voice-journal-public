using System.Windows.Controls;
using System.Windows.Input;
using VoiceJournal.App.ViewModels;

namespace VoiceJournal.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

    /// <summary>Double-clicking a transcribed session opens its transcript (same as the button).</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is LibraryViewModel vm && vm.OpenTranscriptCommand.CanExecute(null)) vm.OpenTranscriptCommand.Execute(null);
    }
}
