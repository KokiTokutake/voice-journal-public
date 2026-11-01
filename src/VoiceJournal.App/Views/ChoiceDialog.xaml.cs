using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace VoiceJournal.App.Views;

/// <summary>A message with labelled choices (MessageBox cannot label its buttons). Esc picks the default.</summary>
public partial class ChoiceDialog : Window
{
    private int _choice;

    private ChoiceDialog(string title, string message, IReadOnlyList<string> choices, int defaultIndex)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        _choice = defaultIndex;
        for (var i = 0; i < choices.Count; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = choices[i],
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(16, 6, 16, 6),
                MinWidth = 110,
                IsDefault = i == defaultIndex,
                IsCancel = i == defaultIndex,
            };
            AutomationProperties.SetName(button, choices[i]);
            button.Click += (_, _) =>
            {
                _choice = index;
                DialogResult = true;
            };
            Buttons.Children.Add(button);
        }
    }

    /// <returns>The index of the chosen button; <paramref name="defaultIndex"/> when the dialog is dismissed.</returns>
    public static int Ask(string title, string message, IReadOnlyList<string> choices, int defaultIndex)
    {
        var dialog = new ChoiceDialog(title, message, choices, defaultIndex) { Owner = Application.Current.MainWindow };
        dialog.ShowDialog();
        return dialog._choice;
    }
}
