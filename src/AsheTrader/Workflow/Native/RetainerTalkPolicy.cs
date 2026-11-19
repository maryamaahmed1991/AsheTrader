namespace AsheTrader.Workflow.Native;

internal enum RetainerTalkKind { None, Retainer, JobReminder }

/// <summary>Only the selected retainer's dialogue or its exact localized job reminder may advance.</summary>
internal static class RetainerTalkPolicy
{
    public const string DialogueSheet = "custom/000/CmnDefRetainerCall_00010";
    public const uint JobReminderRow = 174;

    public static RetainerTalkKind Classify(string expectedName, string? speaker, string? body, string? jobReminder)
    {
        if (string.IsNullOrWhiteSpace(expectedName)) return RetainerTalkKind.None;
        if (string.Equals(speaker?.Trim(), expectedName.Trim(), StringComparison.Ordinal))
            return RetainerTalkKind.Retainer;
        // System reminders have no speaker. Never treat arbitrary unnamed dialogue as ours.
        if (!string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(jobReminder))
            return RetainerTalkKind.None;
        return Normalize(body).Equals(Normalize(jobReminder), StringComparison.Ordinal)
            ? RetainerTalkKind.JobReminder : RetainerTalkKind.None;
    }

    private static string Normalize(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
