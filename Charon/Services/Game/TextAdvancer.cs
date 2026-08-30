using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Auto-advances quest dialogue: while the "Talk" subtitle box is up, click it. The click is
/// ECommons' AddonMaster.Talk shape (the mechanism TextAdvance itself is built on): a fresh
/// AtkEvent (listener = the addon, target = AtkStage's event target, state flags 132) with
/// zeroed event data, sent as MouseDown → MouseClick → MouseUp.
///
/// Two ways to be on:
/// - The Tweaks toggle (global, persisted) — for boxes that always want it.
/// - An IPC FORCE with a TTL (<see cref="Force"/>): Odysseus turns it on while questing by
///   refreshing the lease every so often; if Odysseus dies mid-run the lease simply expires,
///   so dialogue can never stay hijacked on a box nobody is driving.
/// </summary>
public sealed unsafe class TextAdvancer
{
    private const string TalkAddon = "Talk";
    private static readonly TimeSpan ClickPacing = TimeSpan.FromMilliseconds(100);

    private readonly IGameGui _gameGui;
    private readonly Func<bool> _enabled;
    private readonly IPluginLog _log;

    private DateTime _forceUntilUtc = DateTime.MinValue;
    private DateTime _lastClickUtc = DateTime.MinValue;

    public TextAdvancer(IGameGui gameGui, Func<bool> enabled, IPluginLog log)
    {
        _gameGui = gameGui;
        _enabled = enabled;
        _log = log;
    }

    public string Status { get; private set; } = "off";

    /// <summary>On via either the toggle or a live IPC lease.</summary>
    public bool EffectivelyEnabled => _enabled() || DateTime.UtcNow < _forceUntilUtc;

    /// <summary>True while the IPC lease (not the toggle) is what keeps it on.</summary>
    public bool Forced => DateTime.UtcNow < _forceUntilUtc;

    /// <summary>
    /// Force on for <paramref name="seconds"/> (capped at 5 minutes — callers refresh), or 0 to
    /// release the lease immediately.
    /// </summary>
    public void Force(int seconds)
    {
        _forceUntilUtc = seconds <= 0
            ? DateTime.MinValue
            : DateTime.UtcNow.AddSeconds(Math.Min(seconds, 300));
        _log.Debug("TextAdvance force lease: {0}", seconds <= 0 ? "released" : $"{seconds}s");
    }

    public void Update(DateTime now)
    {
        try
        {
            if (!EffectivelyEnabled)
            {
                Status = "off";
                return;
            }

            var source = Forced && !_enabled() ? " (IPC lease)" : "";
            var talk = _gameGui.GetAddonByName(TalkAddon);
            if (talk.IsNull || !talk.IsVisible)
            {
                Status = $"waiting for dialogue{source}";
                return;
            }

            if (now - _lastClickUtc < ClickPacing)
                return;

            _lastClickUtc = now;
            AtkClickHelper.AdvanceTalk((AtkUnitBase*)talk.Address);
            Status = $"advancing dialogue{source}";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Text advance threw");
            Status = "threw (see log)";
        }
    }
}
