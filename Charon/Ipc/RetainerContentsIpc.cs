using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Charon.Services.Game;

namespace Charon.Ipc;

/// <summary>
/// What other plugins can ask about retainer contents — built for Hephaestus, whose "retainers" material
/// source is the one place in its chain that nobody can read from the inside.
///
/// | Charon.Retainers.GetContentsJson | Func&lt;string&gt;                | every known retainer: stacks (HQ its own flag), gil, capturedUtc, and an `unknown` list of retainers nobody has opened |
/// | Charon.Retainers.GetItemJson     | Func&lt;uint, string&gt;          | "who holds this, NQ vs HQ, and how old is that answer"                                                                  |
/// | Charon.Retainers.RequestRefresh  | Func&lt;bool&gt;                  | arm a pass over every retainer; false when nothing needs re-reading                                                     |
/// | Charon.Retainers.RefreshBusy     | Func&lt;bool&gt;                  | true while a refresh pass runs                                                                                          |
/// | Charon.Retainers.RequestFetch    | Func&lt;uint, int, bool, bool&gt;  | itemId, quantity, highQuality → false when refused; the reason is in FetchStatus                                        |
/// | Charon.Retainers.FetchStatus     | Func&lt;string&gt;                | the live reason: waiting for a bell, moving a stack, or why it refused                                                  |
/// | Charon.Retainers.FetchBusy       | Func&lt;bool&gt;                  | true while a fetch runs                                                                                                 |
///
/// TWO THINGS CALLERS MUST DESIGN AROUND. First, an answer is a SNAPSHOT: the client has no retainer
/// inventory until that retainer's window has been opened at a bell, so every payload carries capturedUtc
/// and a retainer missing from `known` is UNKNOWN, not empty. Second, a fetch is not a data query — it is a
/// trip to a bell. RequestFetch only arms the work; the items arrive when the retainer is open, which is why
/// the busy and status gates exist rather than a blocking call.
///
/// Contract rule: EXTEND-ONLY (the same rule as Hephaestus's own IPC and the Daedalus LAN schema). Callers
/// fail open — with these gates absent, a caller simply has no retainer materials, exactly as before.
/// </summary>
public sealed class RetainerContentsIpc : IDisposable
{
    private readonly ICallGateProvider<string> _getContents;
    private readonly ICallGateProvider<uint, string> _getItem;
    private readonly ICallGateProvider<bool> _requestRefresh;
    private readonly ICallGateProvider<bool> _refreshBusy;
    private readonly ICallGateProvider<uint, int, bool, bool> _requestFetch;
    private readonly ICallGateProvider<string> _fetchStatus;
    private readonly ICallGateProvider<bool> _fetchBusy;

    private readonly RetainerContentsReader _contents;
    private readonly Func<bool> _executeEnabled;
    private readonly IPluginLog _log;

    public RetainerContentsIpc(
        IDalamudPluginInterface pluginInterface,
        RetainerContentsReader contents,
        Func<bool> executeEnabled,
        IPluginLog log)
    {
        _contents = contents;
        _executeEnabled = executeEnabled;
        _log = log;

        _getContents = pluginInterface.GetIpcProvider<string>("Charon.Retainers.GetContentsJson");
        _getItem = pluginInterface.GetIpcProvider<uint, string>("Charon.Retainers.GetItemJson");
        _requestRefresh = pluginInterface.GetIpcProvider<bool>("Charon.Retainers.RequestRefresh");
        _refreshBusy = pluginInterface.GetIpcProvider<bool>("Charon.Retainers.RefreshBusy");
        _requestFetch = pluginInterface.GetIpcProvider<uint, int, bool, bool>("Charon.Retainers.RequestFetch");
        _fetchStatus = pluginInterface.GetIpcProvider<string>("Charon.Retainers.FetchStatus");
        _fetchBusy = pluginInterface.GetIpcProvider<bool>("Charon.Retainers.FetchBusy");

        _getContents.RegisterFunc(GetContents);
        _getItem.RegisterFunc(GetItem);
        _requestRefresh.RegisterFunc(RequestRefresh);
        _refreshBusy.RegisterFunc(() => _executeEnabled() && _contents.RefreshBusy);
        _requestFetch.RegisterFunc(RequestFetch);
        _fetchStatus.RegisterFunc(() => _contents.Status);
        _fetchBusy.RegisterFunc(() => _executeEnabled() && _contents.FetchBusy);
    }

    /// <summary>The last thing a caller asked of us, for the Debug line.</summary>
    public string Status { get; private set; } = "no calls yet";

    private string GetContents()
    {
        // Reading is free and always answers; only the two OPERATIONS are opt-in.
        Status = "GetContentsJson";
        return _contents.ContentsJson();
    }

    private string GetItem(uint itemId)
    {
        Status = $"GetItemJson → {itemId}";
        return _contents.ItemJson(itemId);
    }

    private bool RequestRefresh()
    {
        if (!_executeEnabled())
        {
            Status = "RequestRefresh → refused (execution disabled)";
            return false;
        }

        if (_contents.Busy)
        {
            Status = "RequestRefresh → refused (busy)";
            return false;
        }

        var armed = _contents.ArmRefresh();
        Status = $"RequestRefresh → {(armed ? "armed" : "nothing to do")}";
        _log.Debug("[Retainers] IPC refresh requested: {0}", armed);
        return armed;
    }

    private bool RequestFetch(uint itemId, int quantity, bool highQuality)
    {
        if (!_executeEnabled())
        {
            Status = "RequestFetch → refused (execution disabled)";
            return false;
        }

        if (_contents.Busy)
        {
            // Never fight for the same windows: one operation at a time, always.
            Status = "RequestFetch → refused (busy)";
            return false;
        }

        var armed = _contents.ArmFetch(itemId, quantity, highQuality);
        Status = $"RequestFetch → {itemId} x{quantity}{(highQuality ? " HQ" : string.Empty)}: {(armed ? "armed" : "refused")}";
        _log.Debug("[Retainers] IPC fetch requested: {0} x{1} hq={2} → {3}", itemId, quantity, highQuality, armed);
        return armed;
    }

    public void Dispose()
    {
        _getContents.UnregisterFunc();
        _getItem.UnregisterFunc();
        _requestRefresh.UnregisterFunc();
        _refreshBusy.UnregisterFunc();
        _requestFetch.UnregisterFunc();
        _fetchStatus.UnregisterFunc();
        _fetchBusy.UnregisterFunc();
    }
}
