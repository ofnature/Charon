using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Charon.Services.Game;

/// <summary>
/// Promotes a party member to party leader via the native <c>/leader</c> text command. Thin unsafe
/// adapter — the decision is <see cref="Features.Fleet.PartyLeaderPolicy"/>.
///
/// <c>/leader</c> takes PLACEHOLDERS ONLY — its own description reads "Promotes the specified PC to
/// party leader. Promotes current target when no PC is specified", and the accepted arguments are
/// <c>&lt;t&gt;</c>, <c>&lt;target&gt;</c> and <c>&lt;1&gt;</c>…<c>&lt;8&gt;</c>. A raw character
/// name is REJECTED with "The command /leader &lt;name&gt; is unavailable at this time" — verified
/// in-game. This is the same trap /ridepillion sets; see CLAUDE.md.
///
/// Addressing by party slot also removes the injection surface entirely: the only thing
/// interpolated is an integer we bounds-check, never a name.
///
/// It has to go through the chat box rather than Dalamud's ICommandManager, which only dispatches
/// PLUGIN commands — same approach SealBreaker uses in production.
/// </summary>
public static unsafe class PartyLeaderHelper
{
    /// <summary>Promote the member in the given 1-based party slot. False when refused.</summary>
    public static bool TryPromote(int partySlot, IPluginLog log)
    {
        if (partySlot is < 1 or > 8)
        {
            log.Warning("Refusing to promote party slot {0} — outside 1-8", partySlot);
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
            message->SetString($"/leader <{partySlot}>");
            uiModule->ProcessChatBoxEntry(message, nint.Zero, false);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "/leader failed for party slot {0}", partySlot);
            return false;
        }
    }
}
