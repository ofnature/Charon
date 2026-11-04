using System;
using System.Collections.Generic;
using System.Linq;
using Charon.Features.Tweaks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;

namespace Charon.Services.Game;

/// <summary>
/// Turns a bot client's rendering down while its window is in the background — the game's own
/// inactive frame cap, the Minimum character display limit, the lowest 3D resolution — and puts each
/// setting back the moment the window is focused, the character changes, or the plugin unloads.
///
/// These are the game's System settings, which live in FFXIV.cfg — one file for every client on the
/// PC. Changing them in memory affects only this client, but if the game writes the file while they
/// are turned down, the next client launched starts with them. So everything changed is recorded
/// (per character, in the config) the moment it is changed, and a setting is only ever put back
/// while it still holds the value Charon gave it: anything else is somebody's later choice.
/// </summary>
public sealed unsafe class BotRenderProfile : IDisposable
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(250);

    private static readonly Dictionary<RenderOption, SystemConfigOption> GameOption = new()
    {
        [RenderOption.FrameCap] = SystemConfigOption.FPSInActive,
        [RenderOption.DisplayLimit] = SystemConfigOption.DisplayObjectLimitType,
        [RenderOption.Resolution] = SystemConfigOption.GraphicsRezoScale,
    };

    private readonly IGameConfig _gameConfig;
    private readonly ICondition _condition;
    private readonly CharonConfig _config;
    private readonly Action _save;
    private readonly Func<ulong> _contentId;
    private readonly FrameWatch _frames;
    private readonly IPluginLog _log;

    /// <summary>The character whose overrides this client holds (0 = none yet).</summary>
    private ulong _owner;
    private DateTime? _backgroundSinceUtc;
    private DateTime _nextUpdateUtc;
    private string _leftoverNote = string.Empty;
    /// <summary>The live upscaler (DLSS) swapped out for AMD FSR while the resolution is held.</summary>
    private byte? _savedUpscaler;

    /// <summary>The renderer's resolution is being held this frame or was last frame.</summary>
    private bool _holding;

    /// <summary>Frames left in the hand-back after the resolution is released (see HoldResolution).</summary>
    private int _handBackFrames;

    /// <summary>The renderer's 3D resolution (a fraction) held while the profile has it turned down.</summary>
    private float? _heldScale;

    public string Status { get; private set; } = "not started";

    public BotRenderProfile(IGameConfig gameConfig, ICondition condition, CharonConfig config, Action save,
        Func<ulong> contentId, FrameWatch frames, IPluginLog log)
    {
        _gameConfig = gameConfig;
        _condition = condition;
        _config = config;
        _save = save;
        _contentId = contentId;
        _frames = frames;
        _log = log;
    }

    public void Update(DateTime nowUtc)
    {
        HoldResolution();

        if (nowUtc < _nextUpdateUtc)
            return;
        _nextUpdateUtc = nowUtc + UpdateInterval;

        // Settings are left alone mid-load; the next tick after it does the work.
        if (_condition[ConditionFlag.BetweenAreas] || _condition[ConditionFlag.BetweenAreas51])
            return;

        var contentId = _contentId();
        if (_owner != 0 && contentId != _owner)
        {
            RestoreAll(_owner, "the character changed");
            _owner = 0;
        }

        if (contentId == 0)
        {
            Status = "not logged in";
            return;
        }

        if (_owner == 0)
        {
            _owner = contentId;
            // Left over from a session that never put them back (a crash, or the game closing first).
            var left = RestoreAll(contentId, "an earlier session left it turned down");
            if (left > 0)
                _leftoverNote = $" · put back {left} setting{(left == 1 ? "" : "s")} an earlier session left turned down";
        }

        if (!_frames.WindowKnown || _frames.IsFocused)
            _backgroundSinceUtc = null;
        else
            _backgroundSinceUtc ??= nowUtc;

        var setting = _config.RenderProfileFor(contentId);
        var choice = new BotRenderChoice(setting.Enabled, setting.CapFrameRate, setting.LiftCapInCombat,
            setting.FewerCharacters, setting.LowResolution, setting.ResolutionPercent);
        var inCombat = _condition[ConditionFlag.InCombat];
        uint? floor = _gameConfig.TryGet(SystemConfigOption.GraphicsRezoScale, out UIntConfigProperties? props)
            ? props?.Minimum
            : null;
        var away = _backgroundSinceUtc is { } since ? nowUtc - since : (TimeSpan?)null;
        var wanted = BotRenderPolicy.Wanted(choice, away, inCombat, floor);

        // Created lazily: an empty record is never stored.
        var record = _config.RenderProfileRestore.TryGetValue(contentId, out var stored)
            ? stored
            : new Dictionary<string, CharonConfig.RenderOverride>();
        var changed = false;

        foreach (var name in record.Keys.ToList())
        {
            if (Enum.TryParse<RenderOption>(name, out var option) && wanted.ContainsKey(option))
                continue;

            if (Enum.TryParse(name, out option))
                Restore(option, record[name], "no longer wanted");
            record.Remove(name);
            changed = true;
        }

        var alreadyLow = 0;
        foreach (var (option, value) in wanted)
        {
            if (record.ContainsKey(option.ToString()) || !TryRead(option, out var current))
                continue;

            if (current == value)
            {
                alreadyLow++; // nothing to change, and so nothing to put back
                continue;
            }

            if (!TrySet(option, value))
                continue;

            record[option.ToString()] = new CharonConfig.RenderOverride { Original = current, Applied = value };
            _config.RenderProfileRestore[contentId] = record;
            _log.Info($"Bot render: {BotRenderPolicy.Describe(option, value)} "
                      + $"(was {BotRenderPolicy.Value(option, current)})");
            changed = true;
        }

        if (changed)
        {
            if (record.Count == 0)
                _config.RenderProfileRestore.Remove(contentId);
            _save();
        }

        // Held whenever the profile wants it down, recorded or not: a saved option already at the
        // floor still leaves the renderer at whatever the game last set.
        _heldScale = wanted.ContainsKey(RenderOption.Resolution) ? BotRenderPolicy.LiveResolution(choice) : null;
        var upscalerNote = _savedUpscaler is { } saved
            ? $" · upscaling switched from DLSS (type {saved}) to AMD FSR while turned down, put back after"
            : string.Empty;
        Status = Describe(choice, away, inCombat, record, alreadyLow) + upscalerNote + _leftoverNote;
    }

    public void Dispose()
    {
        if (_owner != 0)
            RestoreAll(_owner, "the plugin unloaded");

        // No next frame to hand back on: put the upscaler back now.
        var graphics = GraphicsConfig.Instance();
        if (_savedUpscaler is { } saved && graphics != null)
            HandBackUpscaler(graphics, saved);
    }

    private string Describe(BotRenderChoice choice, TimeSpan? away, bool inCombat,
        Dictionary<string, CharonConfig.RenderOverride> record, int alreadyLow)
    {
        if (!choice.Enabled)
            return "off for this character";
        if (!_frames.WindowKnown)
            return "on — waiting to find the game window";
        if (away is not { } time)
            return "on — window focused, your settings are in use";
        if (time < BotRenderPolicy.ApplyDelay)
            return $"on — in the background, turning down in {(BotRenderPolicy.ApplyDelay - time).TotalSeconds:0}s";

        var parts = record
            .Select(pair => Enum.TryParse<RenderOption>(pair.Key, out var option)
                ? $"{BotRenderPolicy.Describe(option, pair.Value.Applied)} (was {BotRenderPolicy.Value(option, pair.Value.Original)}"
                  + (option == RenderOption.Resolution && ReadLiveScale() is { } live ? $", renderer at {live:0.00})" : ")")
                : null)
            .Where(text => text != null)
            .ToList();

        // Below the game's floor the saved option may already sit at 50%, with nothing recorded.
        if (_heldScale is { } held && !record.ContainsKey(nameof(RenderOption.Resolution)))
            parts.Add($"3D resolution {held * 100:0}%"
                      + (ReadLiveScale() is { } live ? $" (renderer at {live:0.00})" : string.Empty));

        var text = parts.Count > 0
            ? $"on — turned down: {string.Join(", ", parts)}"
            : "on — in the background, nothing to turn down";
        if (alreadyLow > 0)
            text += $" · {alreadyLow} already as low as the profile asks";
        if (inCombat && choice.CapFrameRate && choice.LiftCapInCombat)
            text += " · frame cap lifted for combat";
        else if (choice.CapFrameRate && _frames.ChillFramesLoaded)
            text += " · Chill Frames is loaded and replaces the game's frame cap, so the cap does nothing here";
        return text;
    }

    /// <summary>Puts back everything recorded for a character. Returns how many settings it restored.</summary>
    private int RestoreAll(ulong contentId, string why)
    {
        if (!_config.RenderProfileRestore.TryGetValue(contentId, out var record) || record.Count == 0)
            return 0;

        var restored = 0;
        foreach (var (name, entry) in record)
            if (Enum.TryParse<RenderOption>(name, out var option) && Restore(option, entry, why))
                restored++;

        _config.RenderProfileRestore.Remove(contentId);
        _heldScale = null;
        _save();
        return restored;
    }

    private bool Restore(RenderOption option, CharonConfig.RenderOverride entry, string why)
    {
        if (!TryRead(option, out var current))
            return false;

        if (!BotRenderPolicy.ShouldRestore(current, entry.Applied))
        {
            _log.Info($"Bot render: left {BotRenderPolicy.Label(option)} at {BotRenderPolicy.Value(option, current)} "
                      + $"— it no longer holds what Charon set, so it is someone else's choice ({why})");
            return false;
        }

        if (!TrySet(option, entry.Original))
            return false;

        _log.Info($"Bot render: put {BotRenderPolicy.Label(option)} back to "
                  + $"{BotRenderPolicy.Value(option, entry.Original)} ({why})");
        return true;
    }

    /// <summary>
    /// The renderer's 3D resolution, held every frame while the profile has it turned down. Setting
    /// the saved option does not reach the renderer (seen live: the option read 50% with the picture
    /// unchanged), and the game resets the live value — RezoPlugin, which does the same job, writes it
    /// on every frame for that reason (fact from its source, AGPL; no code used).
    ///
    /// Under DLSS (live upscale type above <see cref="UpscalerFsr"/>) the scale has no effect, so the
    /// live upscaler is switched to AMD FSR while the resolution is held and put back afterwards — what
    /// XASlave does, per its own help text (AGPL; facts only). Like XASlave, the hand-back renders one
    /// frame at full scale before DLSS returns.
    /// </summary>
    private void HoldResolution()
    {
        var graphics = GraphicsConfig.Instance();
        if (graphics == null)
            return;

        if (_heldScale is { } scale)
        {
            _handBackFrames = 0;
            if (graphics->GraphicsRezoUpscaleType > UpscalerFsr)
            {
                _savedUpscaler ??= graphics->GraphicsRezoUpscaleType;
                graphics->GraphicsRezoUpscaleType = UpscalerFsr;
            }

            if (graphics->GraphicsRezoScale != scale)
                graphics->GraphicsRezoScale = scale;
            _holding = true;
            return;
        }

        if (_savedUpscaler is not { } saved)
        {
            // Released: hand the renderer back the saved option's value. A restore already wrote it,
            // but a hold below the game's floor can have nothing recorded to restore.
            if (_holding)
            {
                _holding = false;
                if (_gameConfig.TryGet(SystemConfigOption.GraphicsRezoScale, out uint percent) && percent > 0)
                    graphics->GraphicsRezoScale = percent / 100f;
            }

            return;
        }

        if (_handBackFrames == 0)
        {
            _handBackFrames = 1;
            graphics->GraphicsRezoScale = 1f; // one full-scale frame first
            return;
        }

        HandBackUpscaler(graphics, saved);
    }

    private void HandBackUpscaler(GraphicsConfig* graphics, byte saved)
    {
        graphics->GraphicsRezoUpscaleType = saved;
        if (_gameConfig.TryGet(SystemConfigOption.GraphicsRezoScale, out uint percent) && percent > 0)
            graphics->GraphicsRezoScale = percent / 100f;
        _savedUpscaler = null;
        _handBackFrames = 0;
        _holding = false;
        _log.Info($"Bot render: put the upscaler back to type {saved}");
    }

    /// <summary>Live upscale type for AMD FSR (RezoPlugin restores FSR by writing 1; 0 is the plain
    /// scaler, above 1 is DLSS).</summary>
    private const byte UpscalerFsr = 1;

    private static float? ReadLiveScale()
    {
        var graphics = GraphicsConfig.Instance();
        return graphics == null ? null : graphics->GraphicsRezoScale;
    }

    private bool TryRead(RenderOption option, out uint value) =>
        _gameConfig.TryGet(GameOption[option], out value);

    private bool TrySet(RenderOption option, uint value)
    {
        try
        {
            _gameConfig.Set(GameOption[option], value);

            // The renderer keeps its own copy of the resolution (see HoldResolution): set it too, so a
            // restore lands at once rather than waiting for System Configuration.
            if (option == RenderOption.Resolution)
            {
                var graphics = GraphicsConfig.Instance();
                if (graphics != null)
                    graphics->GraphicsRezoScale = value / 100f;
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"Bot render: could not set {GameOption[option]}: {ex.Message}");
            return false;
        }
    }
}
