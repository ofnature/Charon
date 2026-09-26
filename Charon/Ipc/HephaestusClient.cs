using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Charon.Ipc;

/// <summary>
/// The crafter hand-off: Charon consuming Hephaestus's gates, not the other way round.
///
/// Hephaestus already publishes what this needs, so nothing new is asked of it:
///
/// | Hephaestus.AddToQueue(json) → bool | append to the PLAYER'S saved queue; nothing is started. The default. |
/// | Hephaestus.CraftList(json) → bool | the same list, run through the queue engine now — this STARTS work.  |
/// | Hephaestus.IsBusy → bool          | true while it is driving the character.                               |
/// | Hephaestus.GetVersion → string    | who is on the other end, for the status line.                         |
///
/// THE FORMAT IS HEPHAESTUS'S (`Features.Queue.ListRequest`): <c>{ "keepOrder": true, "items": [ { "itemId": 5057,
/// "quantity": 12 } ] }</c> — see <see cref="Features.GrandCompany.CraftHandoff"/>, which builds it and explains
/// why only supply rows go over.
///
/// TWO CONTRACT FACTS THIS SIDE MUST LIVE WITH. First, ALL OR NOTHING: one entry Hephaestus cannot make rejects
/// the entire request, and the only thing the caller gets back is <c>false</c> — the reason goes to Hephaestus's
/// own log, so a refusal is reported as "refused, see /xllog" rather than guessed at. Second, a true return means
/// the work BEGAN, not that it finished; the outcome is judged from the character's own inventory, exactly as it
/// is with Artisan.
///
/// Absent gates are not an error: with Hephaestus unloaded, every call here reports unavailable and Charon goes on
/// working exactly as before.
/// </summary>
public sealed class HephaestusClient : IDisposable
{
    private const string AddToQueueGate = "Hephaestus.AddToQueue";
    private const string CraftListGate = "Hephaestus.CraftList";
    private const string IsBusyGate = "Hephaestus.IsBusy";
    private const string GetVersionGate = "Hephaestus.GetVersion";

    private readonly ICallGateSubscriber<string, bool> _addToQueue;
    private readonly ICallGateSubscriber<string, bool> _craftList;
    private readonly ICallGateSubscriber<bool> _isBusy;
    private readonly ICallGateSubscriber<string> _getVersion;

    public HephaestusClient(IDalamudPluginInterface pluginInterface)
    {
        _addToQueue = pluginInterface.GetIpcSubscriber<string, bool>(AddToQueueGate);
        _craftList = pluginInterface.GetIpcSubscriber<string, bool>(CraftListGate);
        _isBusy = pluginInterface.GetIpcSubscriber<bool>(IsBusyGate);
        _getVersion = pluginInterface.GetIpcSubscriber<string>(GetVersionGate);
    }

    /// <summary>Is the crafter there at all? Checked per call, because it can be loaded or unloaded at any time.</summary>
    public bool Available => _addToQueue.HasFunction;

    public string Version => Safe(() => _getVersion.InvokeFunc(), "unknown");

    public bool Busy => Safe(() => _isBusy.InvokeFunc(), false);

    /// <summary>Append to the player's saved queue. Nothing starts — this is the default hand-off.</summary>
    public bool AddToQueue(string json) => Safe(() => _addToQueue.InvokeFunc(json), false);

    /// <summary>Run the list now. This STARTS crafting, so it is only ever the player's explicit choice.</summary>
    public bool CraftNow(string json) => Safe(() => _craftList.InvokeFunc(json), false);

    /// <summary>
    /// One line for the UI: the result of a hand-off in the caller's words, including the one thing a bool cannot
    /// say — that a refusal's reason is in Hephaestus's log, not here.
    /// </summary>
    public string DescribeResult(bool accepted, bool started) => !Available
        ? "Hephaestus is not loaded — nothing was sent"
        : accepted
            ? started ? "Hephaestus started the list" : "added to the Hephaestus queue"
            : "Hephaestus refused the list — the reason is in /xllog (its request is all-or-nothing)";

    /// <summary>
    /// No gate may throw into Charon's frame: a caller that is loaded, half-loaded, or throwing gets a default
    /// answer instead of taking the window down.
    /// </summary>
    private static T Safe<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    public void Dispose()
    {
        // ICallGateSubscriber holds no unmanaged state for funcs and needs no unsubscription.
    }
}
