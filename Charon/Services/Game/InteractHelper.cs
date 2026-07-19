using System;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace Charon.Services.Game;

/// <summary>
/// Clicks a world object the way the player would (<c>TargetSystem.InteractWithObject</c>) —
/// used by Fleet Follow to take the same raid portal / lift the leader just used. Thin unsafe
/// adapter, fail-open, framework thread only.
/// </summary>
public sealed unsafe class InteractHelper
{
    private readonly IPluginLog _log;

    public InteractHelper(IPluginLog log) => _log = log;

    public bool TryInteract(IGameObject target)
    {
        try
        {
            if (target.Address == nint.Zero)
                return false;

            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return false;

            targetSystem->InteractWithObject(
                (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address, false);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Interact failed on {0}", target.Name.TextValue);
            return false;
        }
    }
}
