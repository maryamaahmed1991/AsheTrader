namespace AsheTrader.Core;

public enum FuelQueueResumeAction { Finish, RecoverPurchase, RecoverDeposit, CloseShop, RefreshCredits, SelectCharacter }

/// <summary>Unresolved movement takes precedence over destinations and character selection.</summary>
public static class FuelQueueResumeRouting
{
    public static FuelQueueResumeAction Decide(bool endRequested, bool hasPendingPurchase,
        bool hasRecoverableDeposit, bool continueDeposit, bool refreshCredits)
    {
        if (hasPendingPurchase) return FuelQueueResumeAction.RecoverPurchase;
        if (hasRecoverableDeposit) return FuelQueueResumeAction.RecoverDeposit;
        if (endRequested) return FuelQueueResumeAction.Finish;
        if (continueDeposit) return FuelQueueResumeAction.CloseShop;
        if (refreshCredits) return FuelQueueResumeAction.RefreshCredits;
        return FuelQueueResumeAction.SelectCharacter;
    }
}
