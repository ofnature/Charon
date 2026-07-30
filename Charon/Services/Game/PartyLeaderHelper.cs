using System;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Charon.Services.Game;

/// <summary>
/// Promotes a party member to party leader via the native <c>/leader</c> command. Thin unsafe
/// adapter — the decision is <see cref="Features.Fleet.PartyLeaderPolicy"/>.
///
/// The usage is TARGET THE PC, THEN BARE <c>/leader</c> — its own description reads "Promotes the
/// specified PC to party leader. Promotes current target when no PC is specified", and the user
/// confirmed in-game that targeting plus a bare command is all it takes. Do NOT pass anything:
/// - A raw character NAME is rejected outright ("unavailable at this time") — shipped as a bug in
///   0.1.17, the same trap /ridepillion sets.
/// - A party-slot placeholder is worse than useless: Dalamud's party-list index does NOT match the
///   game's &lt;1&gt;…&lt;8&gt; numbering — verified in-game, the leader at in-game slot 2 came back
///   as index 0, so <c>/leader &lt;1&gt;</c> addressed the wrong member (the box itself, which the
///   game then refuses).
/// Targeting the actual character object has no ordering or name parsing to get wrong.
///
/// Goes through the chat box rather than Dalamud's ICommandManager, which only dispatches PLUGIN
/// commands — same approach SealBreaker uses in production.
/// </summary>
public static unsafe class PartyLeaderHelper
{
    /// <summary>
    /// Target <paramref name="leader"/> and promote them. The caller is responsible for restoring
    /// the previous target — done a tick later, so the queued command still sees our target.
    /// Returns the target that was replaced (null when there was none).
    /// </summary>
    public static bool TryPromote(
        IGameObject leader,
        ITargetManager targets,
        IPluginLog log,
        out IGameObject? previousTarget)
    {
        previousTarget = null;

        try
        {
            previousTarget = targets.Target;
            targets.Target = leader;

            // Read it straight back. The target IS the argument here, so a setter that silently did
            // nothing (or a rotation plugin retargeting on the same frame) would make the command
            // promote the wrong PC or nobody — and that is indistinguishable from a bad command
            // without this line.
            var actual = targets.Target;
            var stuck = actual != null && actual.GameObjectId == leader.GameObjectId;
            log.Debug("Promote target: requested {0} (0x{1:X}) · actual {2} · stuck={3}",
                leader.Name.TextValue, leader.GameObjectId,
                actual?.Name.TextValue ?? "(none)", stuck);

            if (!stuck)
            {
                log.Warning("Target did not take — not sending /leader (it would promote the wrong PC)");
                return false;
            }

            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                log.Warning("UIModule unavailable — cannot send /leader");
                return false;
            }

            // Construct the Utf8String before use: stackalloc only zeroes the memory. This is
            // correctness hygiene, NOT a fix for a known bug — the promote demonstrably worked
            // before it was added, so the uninitialised form happened to be harmless here.
            var message = stackalloc Utf8String[1];
            message->Ctor();
            try
            {
                message->SetString("/leader");

                // Read it back: an empty payload here is the difference between "the game refused
                // the command" and "we never actually sent one".
                var payload = message->ToString();
                log.Debug("Chat entry: '{0}' (length {1})", payload, message->Length);
                if (payload.Length == 0)
                {
                    log.Warning("Chat payload came out EMPTY — /leader was not sent");
                    return false;
                }

                uiModule->ProcessChatBoxEntry(message, nint.Zero, false);
                return true;
            }
            finally
            {
                message->Dtor();
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "/leader failed for {0}", leader.Name.TextValue);
            return false;
        }
    }
}
