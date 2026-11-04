using System;
using System.Collections.Generic;

namespace Charon.Features.Tweaks;

/// <summary>The game settings the bot render profile can turn down.</summary>
public enum RenderOption
{
    /// <summary>System option FPSInActive: the game's own "limit frame rate when inactive".</summary>
    FrameCap,

    /// <summary>System option DisplayObjectLimitType: the character display limit.</summary>
    DisplayLimit,

    /// <summary>System option GraphicsRezoScale: 3D resolution scaling, in percent.</summary>
    Resolution,
}

/// <summary>One character's choices for the bot render profile.</summary>
public sealed record BotRenderChoice(
    bool Enabled,
    bool CapFrameRate,
    bool LiftCapInCombat,
    bool FewerCharacters,
    bool LowResolution,
    int ResolutionPercent = BotRenderPolicy.DefaultResolutionPercent);

/// <summary>
/// When to turn a bot client's rendering down, and to what. The profile applies only while the
/// window has been in the background for <see cref="ApplyDelay"/> — a quick alt-tab is not worth two
/// settings changes — and comes off the moment the window is focused, because whoever focused it
/// wants to see the game.
/// </summary>
public static class BotRenderPolicy
{
    public static readonly TimeSpan ApplyDelay = TimeSpan.FromSeconds(3);

    /// <summary>The 3D resolution a new profile turns down to (the game's own floor).</summary>
    public const int DefaultResolutionPercent = 50;

    /// <summary>Lowest 3D resolution the slider offers. The renderer takes values below the game's
    /// 50% floor (RezoPlugin offers 10%, XASlave 1%); 10% is already a blur.</summary>
    public const int MinResolutionPercent = 10;

    /// <summary>Game value for the frame cap: on.</summary>
    public const uint FrameCapOn = 1;

    /// <summary>Game value for the "Minimum" character display limit (0 Maximum … 4 Minimum).</summary>
    public const uint DisplayLimitMinimum = 4;

    /// <summary>
    /// The settings that should be overridden right now, with the value each should hold.
    /// </summary>
    /// <param name="backgroundFor">How long the window has been out of focus; null while focused.</param>
    /// <param name="lowestResolution">The game's own floor for 3D resolution scaling; null when it
    /// could not be read, which leaves resolution alone rather than guessing a number.</param>
    public static IReadOnlyDictionary<RenderOption, uint> Wanted(
        BotRenderChoice choice, TimeSpan? backgroundFor, bool inCombat, uint? lowestResolution)
    {
        var wanted = new Dictionary<RenderOption, uint>();
        if (!choice.Enabled || backgroundFor is not { } away || away < ApplyDelay)
            return wanted;

        // The rotation and the boss AI decide once per frame, so a fight gets every frame it can.
        if (choice.CapFrameRate && !(inCombat && choice.LiftCapInCombat))
            wanted[RenderOption.FrameCap] = FrameCapOn;

        if (choice.FewerCharacters)
            wanted[RenderOption.DisplayLimit] = DisplayLimitMinimum;

        // The saved option cannot go below the game's floor; the renderer is held at the real value
        // (see LiveResolution), so the option only records that the profile has it turned down.
        if (choice.LowResolution && lowestResolution is { } floor)
            wanted[RenderOption.Resolution] = Math.Max(floor, (uint)ClampPercent(choice.ResolutionPercent));

        return wanted;
    }

    /// <summary>
    /// Whether to put a setting back. Only when it still holds what we set: a different value means
    /// someone changed it since, and that is their choice to keep, not ours to undo.
    /// </summary>
    /// <summary>The renderer's live 3D resolution (a fraction) while the profile holds it.</summary>
    public static float LiveResolution(BotRenderChoice choice) => ClampPercent(choice.ResolutionPercent) / 100f;

    public static int ClampPercent(int percent) => Math.Clamp(percent, MinResolutionPercent, 100);

    public static bool ShouldRestore(uint current, uint applied) => current == applied;

    /// <summary>"frame cap on", "characters Minimum", "3D resolution 50%".</summary>
    public static string Describe(RenderOption option, uint value) => $"{Label(option)} {Value(option, value)}";

    public static string Label(RenderOption option) => option switch
    {
        RenderOption.FrameCap => "frame cap",
        RenderOption.DisplayLimit => "characters",
        RenderOption.Resolution => "3D resolution",
        _ => option.ToString(),
    };

    /// <summary>"on", "Minimum", "50%".</summary>
    public static string Value(RenderOption option, uint value) => option switch
    {
        RenderOption.FrameCap => value != 0 ? "on" : "off",
        RenderOption.DisplayLimit => DisplayLimitName(value),
        RenderOption.Resolution => $"{value}%",
        _ => value.ToString(),
    };

    private static string DisplayLimitName(uint value) => value switch
    {
        0 => "Maximum",
        1 => "High",
        2 => "Normal",
        3 => "Low",
        4 => "Minimum",
        _ => $"raw {value}",
    };
}
