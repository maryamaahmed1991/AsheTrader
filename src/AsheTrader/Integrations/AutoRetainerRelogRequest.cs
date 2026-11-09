using System;
using System.Linq;

namespace AsheTrader.Integrations;

/// <summary>
/// Routes a validated AutoRetainer identity to its documented relog command.
/// A true result means the command was dispatched, not that AutoRetainer accepted or completed it.
/// The caller must observe the requested character before treating the login as complete.
/// </summary>
internal static class AutoRetainerRelogRequest
{
    public static bool TryQueue(string canonicalIdentity, bool isLoggedIn, bool playerReady,
        Func<bool> canAutoLogin, Func<string, bool> processCommand, out string error)
    {
        ArgumentNullException.ThrowIfNull(canAutoLogin);
        ArgumentNullException.ThrowIfNull(processCommand);
        if (string.IsNullOrWhiteSpace(canonicalIdentity) || canonicalIdentity.Any(char.IsControl))
        {
            error = "AutoRetainer returned an invalid character identity. Refresh its character list before retrying.";
            return false;
        }

        if (isLoggedIn)
        {
            if (!playerReady)
            {
                error = "Wait for the current character to finish loading before requesting a character switch.";
                return false;
            }
            // CanAutoLogin deliberately returns false in-game: it checks title-screen readiness.
        }
        else if (!canAutoLogin())
        {
            error = "Return to the fully loaded title screen before requesting an AutoRetainer login.";
            return false;
        }

        // Both logged-in switching and title-screen login use this documented command.
        // Preserve the exact registered spelling; AutoRetainer matches the identity case-sensitively.
        if (!processCommand("/autoretainer relog " + canonicalIdentity))
        {
            error = "AutoRetainer's relog command handler is unavailable. Check that AutoRetainer is enabled.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
