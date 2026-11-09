namespace AsheTrader.Workflow.Native;

/// <summary>Waits for natural cutscene completion without consuming gameplay deadlines.</summary>
internal sealed class CutsceneWait
{
    private long? started;

    public bool IsWaiting => started.HasValue;
    public void Reset() => started = null;

    // Null means still watching. A value is the elapsed pause to add to existing deadlines once.
    public long? Observe(long now, bool watching)
    {
        if (watching)
        {
            started ??= now;
            return null;
        }
        var elapsed = started.HasValue ? Math.Max(0, now - started.Value) : 0;
        started = null;
        return elapsed;
    }
}

internal static class InnCutscenePolicy
{
    // AutoRetainerAPI.Configuration.CutsceneSkipMode: Never=0, When_Multi_Mode_is_on=1, Always=2.
    public static string? LoginBlockReason(int? mode, bool? multiMode)
    {
        if (mode == 0 || (mode == 1 && multiMode == false)) return null;
        if (mode == 2 || (mode == 1 && multiMode == true))
            return "AutoRetainer is configured to skip inn login cutscenes. Set AutoRetainer → Advanced → Expert Settings → Skip Inn Login Cutscene to Never, then resume. Ashe's Trader has not requested the login.";
        return "Could not verify AutoRetainer's inn-cutscene setting. Check AutoRetainer compatibility and set Advanced → Expert Settings → Skip Inn Login Cutscene to Never before resuming. Ashe's Trader has not requested the login.";
    }
}
