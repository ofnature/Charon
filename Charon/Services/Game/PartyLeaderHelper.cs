using System;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;

namespace Charon.Services.Game;

/// <summary>
/// Promotes a party member to party leader via the native <c>/leader</c> text command. Thin unsafe
/// adapter — the decision is <see cref="Features.Fleet.PartyLeaderPolicy"/>.
///
/// <c>/leader</c> is a real game command (verified against the TextCommand sheet; there is no
/// /promote). It has to go through the chat box rather than Dalamud's ICommandManager, which only
/// dispatches PLUGIN commands — same approach SealBreaker uses in production.
/// </summary>
public static unsafe class PartyLeaderHelper
{
    /// <summary>
    /// Character names only ever contain letters, spaces, apostrophes and hyphens. This is a
    /// SECURITY check, not politeness: the name is interpolated into a chat command, so anything
    /// carrying a newline or a leading slash could run arbitrary commands. Anything unexpected is
    /// refused rather than sanitised, because a mangled name would promote the wrong toon anyway.
    /// </summary>
    public static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 32
        && name.All(c => char.IsLetter(c) || c == ' ' || c == '\'' || c == '-');

    /// <summary>Send /leader for the named character. False when refused or unavailable.</summary>
    public static bool TryPromote(string characterName, IPluginLog log)
    {
        if (!IsSafeName(characterName))
        {
            log.Warning("Refusing to promote '{0}' — not a plausible character name", characterName);
            return false;
        }

        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                log.Warning("UIModule unavailable — cannot send /leader");
                return false;
            }

            var message = stackalloc Utf8String[1];
            message->SetString($"/leader {characterName}");
            uiModule->ProcessChatBoxEntry(message, nint.Zero, false);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "/leader failed for {0}", characterName);
            return false;
        }
    }
}
