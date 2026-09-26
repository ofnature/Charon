using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Charon.Features.Retainers;

namespace Charon.Services.Game;

/// <summary>
/// Reads this character's retainers straight from <c>RetainerManager</c> — venture id, completion
/// time, level, item count and gil. READ-ONLY: it never opens a bell and never clicks anything, so
/// it is safe to run on an attended box.
///
/// Venture timers are fetched LAZILY by the client: a retainer can carry a venture id while its
/// completion timestamp is still zero, which is why <c>RequestVenturesTimers</c> exists. We ask for
/// them at most once a minute, and only when a venture is actually missing its timer — a read this
/// cheap has no business making a network request every tick. Until the answer arrives the board
/// says "unknown" rather than inventing a duration.
///
/// Fail-open: any unreadable state reports not-loaded and the UI says so instead of showing a
/// confident zero.
/// </summary>
public sealed unsafe class RetainerReader
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(2);

    /// <summary>Timers change on the minute scale; asking more often is pure noise.</summary>
    private static readonly TimeSpan TimerRequestEvery = TimeSpan.FromSeconds(60);

    private readonly IPluginLog _log;

    private List<RetainerVenture> _cached = new();
    private DateTime _cachedAtUtc = DateTime.MinValue;
    private DateTime _timersRequestedUtc = DateTime.MinValue;

    public RetainerReader(IPluginLog log)
    {
        _log = log;
    }

    /// <summary>Whether the game actually handed over retainer data this read.</summary>
    public bool Loaded { get; private set; }

    public string Status { get; private set; } = "not read yet";

    public IReadOnlyList<RetainerVenture> Read(DateTime utcNow)
    {
        if (utcNow - _cachedAtUtc < CacheFor)
            return _cached;
        _cachedAtUtc = utcNow;

        var list = new List<RetainerVenture>();
        var loaded = false;

        try
        {
            var manager = RetainerManager.Instance();
            if (manager != null && manager->IsReady)
            {
                loaded = true;
                var missingTimer = false;
                var count = manager->GetRetainerCount();

                for (uint i = 0; i < count; i++)
                {
                    var retainer = manager->GetRetainerBySortedIndex(i);
                    if (retainer == null || retainer->RetainerId == 0)
                        continue;

                    DateTime? complete = null;
                    if (retainer->VentureId != 0)
                    {
                        if (retainer->VentureComplete == 0)
                            missingTimer = true;
                        else
                            complete = DateTimeOffset.FromUnixTimeSeconds(retainer->VentureComplete).UtcDateTime;
                    }

                    list.Add(new RetainerVenture(
                        retainer->NameString,
                        retainer->VentureId,
                        complete,
                        retainer->Level,
                        retainer->ItemCount,
                        retainer->Gil,
                        retainer->ClassJob,
                        // The game's SORTED index — the same number the retainer list takes when it opens one, and
                        // therefore the only way to aim a run at a particular retainer.
                        (int)i));
                }

                if (missingTimer && utcNow - _timersRequestedUtc > TimerRequestEvery)
                {
                    _timersRequestedUtc = utcNow;
                    manager->RequestVenturesTimers();
                    _log.Debug("Retainers: asked the game for venture timers");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Retainer read threw");
            loaded = false;
            list.Clear();
        }

        Loaded = loaded;
        _cached = list;
        Status = VentureBoard.Summarize(loaded, VentureBoard.Compose(utcNow, list));
        return _cached;
    }
}
