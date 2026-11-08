using System.Windows.Automation;
using System.Windows.Controls;
using VoiceJournal.App.Services;

namespace VoiceJournal.Tests.AppTests;

/// <summary>AT-25: a status text marked to announce raises LiveRegionChanged when it changes (WPF does not by itself).</summary>
public sealed class LiveTextTests
{
    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("STA test failed", error);
    }

    [Fact]
    public void AnAnnouncedText_IsPolite_AndAnnouncesEachNonEmptyChange()
    {
        OnSta(() =>
        {
            var said = new List<string>();
            void Record(object? sender, string text) => said.Add(text);
            LiveText.Announced += Record;
            try
            {
                var block = new TextBlock();
                LiveText.SetAnnounce(block, true);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(block));

                block.Text = "録音中";
                block.Text = string.Empty;
                block.Text = "保存しました";

                Assert.Equal(["録音中", "保存しました"], said);

                LiveText.SetAnnounce(block, false);
                block.Text = "停止";
                Assert.Equal(2, said.Count);
            }
            finally
            {
                LiveText.Announced -= Record;
            }
        });
    }
}
