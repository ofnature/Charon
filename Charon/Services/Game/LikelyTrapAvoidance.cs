using System;
using System.Linq;
using System.Numerics;
using Charon.Features.DeepDungeon;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// Hands the likely trap spots around the player to Ariadne, so every walking route it plans in a deep
/// dungeon — the fleet follow's included — keeps off them where there is another way
/// (<see cref="LikelyTrapAvoid"/>). Revealed traps are Ariadne's own business: it sees them itself.
///
/// The hand-over is a LEASE (<c>Ariadne.Avoid.Circles(owner, circles, seconds)</c>), renewed every couple
/// of seconds and let go on leaving the dungeon or turning the setting off — so a Charon that stops or
/// crashes cannot leave Ariadne avoiding spots for ever. The spots are the ones the ESP would draw:
/// nothing once Sight has shown the floor's real traps or Safety has removed them.
///
/// Only Ariadne can take a list of circles; on vnavmesh this does nothing and says so. An Ariadne without
/// the gate (older than the one that added it) is reported the same way, never guessed at.
/// </summary>
public sealed class LikelyTrapAvoidance
{
    public const string Owner = "Charon.LikelyTraps";

    private static readonly TimeSpan Renew = TimeSpan.FromSeconds(2);
    private const int LeaseSeconds = 6;

    private readonly NavClient _nav;
    private readonly PalaceSpotService _spots;
    private readonly IObjectTable _objectTable;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _inDeepDungeon;
    private readonly IPluginLog _log;

    private DateTime _nextUtc;
    private bool _holding;
    private string _lastLogged = string.Empty;

    public LikelyTrapAvoidance(NavClient nav, PalaceSpotService spots, IObjectTable objectTable, Func<bool> enabled,
        Func<bool> inDeepDungeon, IPluginLog log)
    {
        _nav = nav;
        _spots = spots;
        _objectTable = objectTable;
        _enabled = enabled;
        _inDeepDungeon = inDeepDungeon;
        _log = log;
    }

    public string Status { get; private set; } = "off";

    public void Update(DateTime nowUtc)
    {
        if (nowUtc < _nextUtc)
            return;
        _nextUtc = nowUtc + Renew;

        if (!_enabled())
        {
            Release();
            Set("off");
            return;
        }

        if (!_inDeepDungeon() || _objectTable.LocalPlayer is not { } me)
        {
            Release();
            Set("not in a deep dungeon");
            return;
        }

        if (!_nav.CanAvoid)
        {
            Set("vnavmesh cannot take a list of spots — only Ariadne can");
            return;
        }

        var near = _spots.Near(me.Position, LikelyTrapAvoid.Range, 0f, traps: true, hoards: false)
            .Select(s => new Vector3(s.X, s.Y, s.Z));
        var circles = LikelyTrapAvoid.Circles(near, me.Position);
        var count = LikelyTrapAvoid.Count(circles);

        if (count == 0)
        {
            // Nothing to keep off (none known here, or Sight / Safety has settled the floor).
            Release();
            Set("no likely trap spots near");
            return;
        }

        switch (_nav.AvoidCircles(Owner, circles, LeaseSeconds))
        {
            case true:
                _holding = true;
                Set($"routes keep off {count} likely trap spot(s) where there is another way");
                break;
            case false:
                Set($"Ariadne refused {count} likely trap spot(s)");
                break;
            default:
                Set("this Ariadne has no way to take likely trap spots (it needs a newer one)");
                break;
        }
    }

    /// <summary>Lets the lease go at once (plugin unload); it would lapse by itself a few seconds later anyway.</summary>
    public void Dispose() => Release();

    private void Release()
    {
        if (!_holding)
            return;
        _holding = false;
        _nav.AvoidCircles(Owner, [], 0);
    }

    private void Set(string status)
    {
        Status = status;

        // The count changes with every step; log the kind of state, not each number.
        var kind = new string(status.Where(c => !char.IsDigit(c)).ToArray());
        if (kind == _lastLogged)
            return;
        _lastLogged = kind;
        _log.Info($"Likely traps: {status}");
    }
}
