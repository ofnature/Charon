using System;
using Charon.Features.GrandCompany;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Charon.Ipc;

/// <summary>
/// The Grand Company request list, for a crafter to work from — built for Hephaestus, whose material sourcing
/// reaches for "vendors, FC chest, retainers" and has no way to know what the company is ASKING for.
///
/// | Charon.GrandCompany.GetRequestsJson | Func&lt;string&gt; | the day's request list: per item id, name, requested, shortfall, kind (Supply/Provisioning), job, plus capturedUtc and rolloverUtc |
/// | Charon.GrandCompany.RequestsStatus  | Func&lt;string&gt; | one line: how many items, how old the snapshot is, and whether it is from a previous day                                              |
///
/// TWO THINGS CALLERS MUST DESIGN AROUND. First, this is a SNAPSHOT of the delivery board: nothing is captured
/// until the board has been open, so `known` is false rather than a list of zero items when nobody has looked —
/// "the company wants nothing today" and "nobody has checked" are different answers, and only one of them means
/// there is nothing to make. Second, the list is DAILY: a row disappears as that item is handed in and the whole
/// list is replaced at the rollover, so `stale` says whether this is still today's list.
///
/// Read-only on purpose: this gate has no operations, because a snapshot is taken by the player (the button on
/// the Grand Company Dailies page, or /charon snapshot). Nothing here moves an item or spends anything.
///
/// Contract rule: EXTEND-ONLY, the same rule as the retainer gate and Hephaestus's own IPC. Callers fail open —
/// with this gate absent, a caller simply has no Grand Company materials, exactly as before.
/// </summary>
public sealed class GcRequestsIpc : IDisposable
{
    private readonly ICallGateProvider<string> _getRequests;
    private readonly ICallGateProvider<string> _status;

    private readonly Func<GcRequestSnapshot?> _snapshot;
    private readonly Func<uint, int> _held;
    private readonly IPluginLog _log;

    public GcRequestsIpc(
        IDalamudPluginInterface pluginInterface,
        Func<GcRequestSnapshot?> snapshot,
        Func<uint, int> held,
        IPluginLog log)
    {
        _snapshot = snapshot;
        _held = held;
        _log = log;

        _getRequests = pluginInterface.GetIpcProvider<string>("Charon.GrandCompany.GetRequestsJson");
        _getRequests.RegisterFunc(() =>
        {
            try
            {
                return GcRequests.ToJson(_snapshot(), _held, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // Fail open, and say why: a caller gets "unknown" and a reason rather than an exception thrown
                // across a plugin boundary.
                _log.Debug("[GC IPC] request list failed to build: {0}", ex.Message);
                return "{\"known\":false,\"note\":\"the request list could not be read this time\"}";
            }
        });

        _status = pluginInterface.GetIpcProvider<string>("Charon.GrandCompany.RequestsStatus");
        _status.RegisterFunc(() => GcRequests.Describe(_snapshot(), DateTime.UtcNow));
    }

    public void Dispose()
    {
        _getRequests.UnregisterFunc();
        _status.UnregisterFunc();
    }
}
