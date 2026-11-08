using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace VoiceJournal.App.Services;

/// <summary>
/// AT-25 (状態が読み上げ可能): WPF does not raise LiveRegionChanged by itself when the text of an element marked with
/// <see cref="AutomationProperties.LiveSettingProperty"/> changes, so Narrator stays silent. With
/// <c>services:LiveText.Announce="True"</c> a text block sets its live setting to polite and raises the event whenever
/// its text changes to something non-empty. Not for text that changes every second (elapsed time, progress).
/// </summary>
public static class LiveText
{
    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    public static readonly DependencyProperty AnnounceProperty = DependencyProperty.RegisterAttached(
        "Announce", typeof(bool), typeof(LiveText), new PropertyMetadata(false, OnAnnounceChanged));

    public static bool GetAnnounce(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(AnnounceProperty);

    public static void SetAnnounce(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(AnnounceProperty, value);

    /// <summary>Raised after an announcement is requested (for tests).</summary>
    public static event EventHandler<string>? Announced;

    private static void OnAnnounceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text) return;
        TextDescriptor.RemoveValueChanged(text, OnTextChanged);
        if (e.NewValue is not true) return;
        if (AutomationProperties.GetLiveSetting(text) == AutomationLiveSetting.Off) AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
        TextDescriptor.AddValueChanged(text, OnTextChanged);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock text || string.IsNullOrWhiteSpace(text.Text)) return;
        var peer = UIElementAutomationPeer.FromElement(text) ?? UIElementAutomationPeer.CreatePeerForElement(text);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        Announced?.Invoke(text, text.Text);
    }
}
