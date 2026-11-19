using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Integrations;

public class RetainerTalkPolicyTests
{
    private const string Reminder = "Your retainer is qualified to be trained in more advanced traditions. Select Assign retainer a job to proceed.";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void RecognizesSpeakerlessReminder(string? speaker) =>
        Assert.Equal(RetainerTalkKind.JobReminder, RetainerTalkPolicy.Classify("Siateior", speaker, Reminder, Reminder));

    [Fact]
    public void AcceptsDisplayedLineBreaks() =>
        Assert.Equal(RetainerTalkKind.JobReminder, RetainerTalkPolicy.Classify("Siateior", null,
            Reminder.Replace("Select Assign", "Select\nAssign"), Reminder));

    [Theory]
    [InlineData("Du kannst deinem Gehilfen nun über das Menü einen Job zuweisen.")]
    [InlineData("L'option “Job du servant” a été ajoutée au menu. Sélectionnez-la pour voir comment elle fonctionne.")]
    [InlineData("リテイナーのメニューに、\n「リテイナーのジョブ設定」が追加されました。\n「リテイナーのジョブ設定」を選んで内容を確認してみましょう。")]
    public void UsesLocalizedGameText(string reminder) =>
        Assert.Equal(RetainerTalkKind.JobReminder, RetainerTalkPolicy.Classify("Siateior", "", reminder, reminder));

    [Fact]
    public void RetainerGreetingAndFarewellStillAdvance() =>
        Assert.Equal(RetainerTalkKind.Retainer, RetainerTalkPolicy.Classify("Siateior", " Siateior ", "Welcome back!", null));

    [Theory]
    [InlineData("Siateior", "Another NPC", Reminder, Reminder)]
    [InlineData("Siateior", null, "An unrelated system message.", Reminder)]
    [InlineData("Siateior", null, Reminder, null)]
    [InlineData("Siateior", null, "", "")]
    [InlineData("Siateior", null, Reminder + " Another action is required.", Reminder)]
    [InlineData("", null, Reminder, Reminder)]
    [InlineData("Siateior", null, null, Reminder)]
    public void RejectsUnrecognizedDialogue(string name, string? speaker, string? body, string? reminder) =>
        Assert.Equal(RetainerTalkKind.None, RetainerTalkPolicy.Classify(name, speaker, body, reminder));
}
