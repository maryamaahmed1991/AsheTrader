namespace AsheTrader.Workflow.Native;

// Tracks that the window loaded these native containers in this character/territory session.
// Item counts are never cached: every inspection still reads the current native slots.
internal sealed class SaddlebagReadSession
{
    private ulong character;
    private uint territory;
    private bool premium;
    public bool CanReadSilently { get; private set; }

    public void Observe(ulong currentCharacter, uint currentTerritory, bool hasPremium, bool loaded)
    {
        if (currentCharacter == 0 || character != currentCharacter || territory != currentTerritory
            || premium != hasPremium || !loaded)
            CanReadSilently = false;
        character = currentCharacter;
        territory = currentTerritory;
        premium = hasPremium;
    }

    public void ConfirmLoaded() => CanReadSilently = character != 0;
}
