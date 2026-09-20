using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Charon.Services.Game;

/// <summary>
/// Answers one question for the follow handoff: is a boss AI driving this character's feet right
/// now? Two providers are understood, because the fleet runs both.
///
/// **Minerva** (the one actually in use) publishes a documented consumer contract
/// (<c>D:\Dev\Minerva\docs\provider-ipc.md</c>):
///   - <c>minerva.MustNotMove</c> — SHARED DATA, a <c>bool[]</c>, true while MOVEMENT would punish
///     the player. The contract explicitly says to read this every frame with no try/catch: it is
///     a plain array read, not a call gate. Both it and MustNotAct are false whenever no boss
///     module is active, and both are cleared on Minerva's dispose, so a stale "stop" is
///     impossible.
///   - <c>Minerva.ActiveModule</c> — the active boss module's type name, or "" for none. This is
///     the direct analogue of BMR's HasActiveModule and is only polled occasionally; which module
///     is running does not change frame to frame.
///
/// **BossMod Reborn** exposes <c>BossMod.HasActiveModule</c> and is kept as a fallback.
///
/// THIS IS WHY "stop follow in boss fights" DID NOTHING: the only source was BMR's endpoint, the
/// box runs Minerva, so every call threw, the catch returned false, and a gate of
/// <c>inCombat &amp;&amp; hasActiveModule</c> could never once be true. Fail-open is right for an
/// absent plugin, but it silently disabled the feature for a present one — hence <see cref="Status"/>,
/// which names the provider that answered so this is visible instead of inferred.
///
/// Fail-open throughout: no provider means no boss module, and follow is never gated.
/// </summary>
public sealed class BossAiClient
{
    /// <summary>Which module is running changes on the scale of fights, not frames.</summary>
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>Minerva's shared-data tag, verbatim from its provider contract.</summary>
    private const string MustNotMoveTag = "minerva.MustNotMove";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICallGateSubscriber<bool> _minervaConnected;
    private readonly ICallGateSubscriber<string> _minervaActiveModule;
    private readonly ICallGateSubscriber<bool> _bmrHasActiveModule;

    private bool[]? _mustNotMove;
    private DateTime _lastProbeUtc = DateTime.MinValue;
    private bool _moduleActive;
    private string _provider = "none";
    private string _module = string.Empty;

    public BossAiClient(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
        _minervaConnected = pluginInterface.GetIpcSubscriber<bool>("Minerva.IsConnected");
        _minervaActiveModule = pluginInterface.GetIpcSubscriber<string>("Minerva.ActiveModule");
        _bmrHasActiveModule = pluginInterface.GetIpcSubscriber<bool>("BossMod.HasActiveModule");
    }

    /// <summary>
    /// True when a boss AI owns movement: a boss module is running, or a mechanic is punishing
    /// movement outright. Either way Charon must not path the character.
    /// </summary>
    public bool MovementHandedOver { get; private set; }

    /// <summary>What answered and what it said — surfaced in Debug so this is never guesswork.</summary>
    public string Status { get; private set; } = "no boss AI detected";

    public void Update(DateTime nowUtc)
    {
        // Shared data first: free to read, and the only flag that is true DURING the mechanic
        // rather than merely during the fight.
        var mustNotMove = ReadMustNotMove();

        if (nowUtc - _lastProbeUtc >= ProbeEvery)
        {
            _lastProbeUtc = nowUtc;
            Probe();
        }

        MovementHandedOver = mustNotMove || _moduleActive;

        Status = _provider switch
        {
            "none" => "no boss AI detected",
            _ when mustNotMove => $"{_provider}: movement would punish — holding",
            _ when _moduleActive => $"{_provider}: module {_module} active",
            _ => $"{_provider}: no module active",
        };
    }

    /// <summary>
    /// Minerva's movement flag. <c>GetOrCreateData</c> hands back the SAME array Minerva writes to,
    /// so this is a one-word read; when Minerva is absent it creates a permanently-false array,
    /// which is exactly the fail-open answer.
    /// </summary>
    private bool ReadMustNotMove()
    {
        try
        {
            _mustNotMove ??= _pluginInterface.GetOrCreateData<bool[]>(MustNotMoveTag, () => [false]);
            return _mustNotMove.Length > 0 && _mustNotMove[0];
        }
        catch
        {
            _mustNotMove = null;
            return false;
        }
    }

    private void Probe()
    {
        try
        {
            _minervaConnected.InvokeFunc();
            _module = _minervaActiveModule.InvokeFunc() ?? string.Empty;
            _moduleActive = _module.Length > 0;
            _provider = "Minerva";
            return;
        }
        catch
        {
            // Minerva absent — fall through to BMR.
        }

        try
        {
            _moduleActive = _bmrHasActiveModule.InvokeFunc();
            _module = _moduleActive ? "(BMR)" : string.Empty;
            _provider = "BossMod Reborn";
        }
        catch
        {
            _moduleActive = false;
            _module = string.Empty;
            _provider = "none";
        }
    }
}
