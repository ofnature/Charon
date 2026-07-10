using System;
using Dalamud.Plugin.Services;
using Charon.Features.AutoAccept;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>
/// Bridges the game's party-invite state to <see cref="GroupInviteManager"/> via
/// <c>InfoProxyPartyInvite</c> — the client's own pending-invite record. This carries the inviter
/// name, home-world id, and invite timestamp directly, and exposes <c>RespondToInvitation</c>,
/// the same function the Yes button runs. No SelectYesno text parsing, no dialog clicking,
/// works in every client language.
///
/// Poll each framework tick: a new (name, time) pair is handed to the manager; when the manager's
/// delayed accept fires, <see cref="AcceptCurrent"/> responds to the invitation if it is still
/// pending. Ignored invites are left completely untouched — the dialog stays up for the player.
/// </summary>
public sealed unsafe class GroupInviteInterop : IDisposable
{
    private readonly IDataManager _dataManager;
    private readonly GroupInviteManager _manager;
    private readonly IPluginLog _log;

    private string _currentInviter = string.Empty;
    private uint _currentInviteTime;

    public GroupInviteInterop(IDataManager dataManager, GroupInviteManager manager, IPluginLog log)
    {
        _dataManager = dataManager;
        _manager = manager;
        _log = log;
    }

    /// <summary>Inviter of the currently pending invite ("" when none) — shown in the debug section.</summary>
    public string PendingInviter => _currentInviter;

    public void Dispose()
    {
        // Nothing registered — the poller holds no game hooks.
    }

    /// <summary>Watch InfoProxyPartyInvite for a new pending invite. Call every framework tick.</summary>
    public void Poll(DateTime nowUtc)
    {
        try
        {
            var proxy = InfoProxyPartyInvite.Instance();
            if (proxy == null)
            {
                ClearTracking();
                return;
            }

            var inviter = proxy->InviterName.ToString();
            if (inviter.Length == 0)
            {
                // Invite gone (responded, declined, or expired) — cancel any pending accept.
                ClearTracking();
                return;
            }

            var inviteTime = proxy->InviteTime;
            if (inviter == _currentInviter && inviteTime == _currentInviteTime)
                return; // same invite we already handled

            _currentInviter = inviter;
            _currentInviteTime = inviteTime;

            var world = ResolveWorldName(proxy->InviterWorldId);
            _log.Debug("Party invite detected: {0}@{1}", inviter, world);
            _manager.OnInviteReceived(inviter, world, nowUtc);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to poll InfoProxyPartyInvite");
        }
    }

    /// <summary>Accept callback for the manager: respond to the invitation if it is still pending.</summary>
    public void AcceptCurrent()
    {
        try
        {
            if (_currentInviter.Length == 0)
                return;

            var proxy = InfoProxyPartyInvite.Instance();
            if (proxy == null || proxy->InviterName.ToString() != _currentInviter)
                return; // invite disappeared while the accept delay ran

            var accepted = proxy->RespondToInvitation(_currentInviter, true);
            _log.Debug("RespondToInvitation({0}, accept) → {1}", _currentInviter, accepted);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to accept the party invite");
        }
    }

    private void ClearTracking()
    {
        if (_currentInviter.Length == 0)
            return;

        _currentInviter = string.Empty;
        _currentInviteTime = 0;
        _manager.OnInviteWithdrawn();
    }

    private string ResolveWorldName(ushort worldId)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<World>();
            if (sheet != null && sheet.TryGetRow(worldId, out var row))
                return row.Name.ExtractText();
        }
        catch
        {
            // fall through
        }

        return string.Empty;
    }
}
