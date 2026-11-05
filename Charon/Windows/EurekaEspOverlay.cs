using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Charon.Features.Eureka;
using Charon.Windows.Components;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Charon.Windows;

/// <summary>
/// The Eureka ESP: each mob's aggro shape by how it notices you, the friendly elementals and their spawn
/// spots, and bunny coffers with their spots while the coffer hunt is on. The facts and their sources are
/// in <see cref="EurekaData"/>; a "?" on a label marks a family rule or a single weak source, and a mob no
/// source covers is drawn as a grey circle labelled as unknown rather than guessed.
///
/// The idea comes from Eureka Suite's public feature list (Kyuuso; closed source, no licence — nothing of
/// it was used). Opened by the plugin only while in a Eureka zone. Fullscreen, no background, no inputs.
/// </summary>
public sealed class EurekaEspOverlay : Window
{
    private const float MobDrawRange = 50f;

    /// <summary>NecroLens's general aggro radius, hitbox + 10y. Eureka's real radii are not published.</summary>
    private const float AggroBeyondHitbox = 10f;

    private const float ElementalSpotRange = 120f;
    private const float SpotLabelRange = 50f;
    private const float CofferSpotRange = 120f;
    private const float CofferDrawRange = 100f;

    private static readonly Vector4 SightColor = new(0.85f, 0.25f, 0.25f, 0.9f);
    private static readonly Vector4 TrueSightColor = new(0.95f, 0.15f, 0.45f, 0.95f);
    private static readonly Vector4 SoundColor = new(0.90f, 0.75f, 0.20f, 0.9f);
    private static readonly Vector4 ProximityColor = new(0.90f, 0.45f, 0.20f, 0.9f);
    private static readonly Vector4 MagicColor = new(0.65f, 0.45f, 0.95f, 0.9f);
    private static readonly Vector4 LowHpColor = new(0.75f, 0.10f, 0.15f, 0.95f);
    private static readonly Vector4 UnknownColor = new(0.65f, 0.65f, 0.65f, 0.6f);
    private static readonly Vector4 ElementalColor = new(0.40f, 0.95f, 0.65f, 0.9f);
    private static readonly Vector4 CofferSpotColor = new(1f, 0.84f, 0.30f, 0.75f);
    private static readonly Vector4 NmColor = new(1f, 0.35f, 0.25f, 0.95f);
    private static readonly Vector4 BunnyColor = new(1f, 0.70f, 0.85f, 0.95f);
    private static readonly Vector4 LabelColor = new(0.95f, 0.95f, 0.95f, 0.9f);
    private static readonly Vector4 PeakExpColor = new(0.45f, 1f, 0.45f, 1f);
    private static readonly Vector4 ChainExpColor = new(0.70f, 0.95f, 0.70f, 0.95f);
    private static readonly Vector4 NoExpColor = new(0.60f, 0.60f, 0.60f, 0.9f);
    private static readonly Vector4 TriggerReadyColor = new(1f, 0.75f, 0.20f, 1f);
    private static readonly Vector4 TriggerWaitColor = new(0.85f, 0.70f, 0.45f, 0.7f);

    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly IFateTable _fates;
    private readonly WorldShapes _shapes;
    private readonly Func<bool> _showMobs;
    private readonly Func<bool> _showNames;
    private readonly Func<bool> _showElementalSpots;
    private readonly Func<bool> _showCofferSpots;
    private readonly Func<bool> _showFates;
    private readonly Func<bool> _showExp;
    private readonly Func<bool> _showTriggers;
    private readonly HashSet<ushort> _liveFateIds = new();

    public EurekaEspOverlay(IObjectTable objectTable, IGameGui gameGui, IClientState clientState, IFateTable fates,
        Func<bool> showMobs, Func<bool> showNames, Func<bool> showElementalSpots, Func<bool> showCofferSpots,
        Func<bool> showFates, Func<bool> showExp, Func<bool> showTriggers)
        : base("##CharonEurekaEsp")
    {
        _objectTable = objectTable;
        _clientState = clientState;
        _fates = fates;
        _shapes = new WorldShapes(gameGui);
        _showMobs = showMobs;
        _showNames = showNames;
        _showElementalSpots = showElementalSpots;
        _showCofferSpots = showCofferSpots;
        _showFates = showFates;
        _showExp = showExp;
        _showTriggers = showTriggers;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    /// <summary>FATEs live in this zone as of the last frame drawn (the Eureka page reads it).</summary>
    public IReadOnlySet<ushort> LiveFateIds => _liveFateIds;

    /// <summary>What the last frame drew — the Debug line.</summary>
    public string Status { get; private set; } = "not drawn yet";

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        Position = viewport.Pos;
        Size = viewport.Size;
    }

    public override void Draw()
    {
        var local = _objectTable.LocalPlayer;
        var territory = (uint)_clientState.TerritoryType;
        if (local == null || !EurekaZones.IsEureka(territory))
            return;

        var drawList = ImGui.GetWindowDrawList();
        var here = local.Position;
        int mobs = 0, unknown = 0, ignored = 0, worthExp = 0, triggers = 0, elementals = 0, coffers = 0, elementalSpots = 0, cofferSpots = 0;
        var liveFates = TrackFates(drawList, here, _showFates());
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var myLevel = ElementalLevel();

        if (_showElementalSpots())
        {
            var color = ImGui.GetColorU32(ElementalColor with { W = 0.55f });
            foreach (var spot in EurekaSpots.Near(EurekaData.ElementalSpots, territory, here, ElementalSpotRange))
            {
                _shapes.Circle(drawList, spot, 2f, color, filled: false);
                if (Vector3.Distance(here, spot) <= SpotLabelRange)
                    _shapes.Label(drawList, spot, "elemental spot", color);
                elementalSpots++;
            }
        }

        // Bunny coffers are personal and only exist during the hunt, so their spots only matter then.
        var hunting = local.StatusList.Any(s => s.StatusId == EurekaZones.CofferHuntStatus);
        if (_showCofferSpots() && hunting)
        {
            var color = ImGui.GetColorU32(CofferSpotColor);
            foreach (var spot in EurekaSpots.Near(EurekaData.CofferSpots, territory, here, CofferSpotRange))
            {
                _shapes.Circle(drawList, spot, 1.5f, color, filled: false);
                cofferSpots++;
            }

            if (EurekaSpots.Nearest(EurekaData.CofferSpots, territory, here, CofferSpotRange) is { } nearest)
            {
                _shapes.Line(drawList, here, nearest, color);
                _shapes.Label(drawList, nearest, $"coffer spot {Vector3.Distance(here, nearest):0}y", color);
            }
        }

        foreach (var obj in _objectTable)
        {
            var distance = Vector3.Distance(here, obj.Position);

            if (obj.ObjectKind == ObjectKind.EventObj && distance <= CofferDrawRange && CofferLabel(obj.BaseId) is { } label)
            {
                var color = ImGui.GetColorU32(CofferSpotColor with { W = 0.95f });
                _shapes.Circle(drawList, obj.Position, 1f, color, filled: true);
                _shapes.Label(drawList, obj.Position, label, color);
                coffers++;
                continue;
            }

            if (obj is not IBattleNpc npc || npc.CurrentHp == 0)
                continue;

            if (EurekaZones.Elementals.Contains(npc.NameId))
            {
                var color = ImGui.GetColorU32(ElementalColor);
                _shapes.Circle(drawList, obj.Position, npc.HitboxRadius + 1f, color, filled: true);
                _shapes.Label(drawList, obj.Position, "Elemental", color);
                elementals++;
                continue;
            }

            if (!_showMobs() || distance > MobDrawRange || npc.SubKind != (byte)BattleNpcSubKind.Combatant)
                continue;

            // Aggroed mobs are the rotation's problem — shapes only matter before the pull.
            if (npc.StatusFlags.HasFlag(StatusFlags.InCombat))
                continue;

            if (DrawMob(drawList, npc, territory, myLevel, out var ignores, out var band))
                unknown++;
            if (_showTriggers() && DrawTrigger(drawList, npc, territory, now))
                triggers++;
            if (ignores)
                ignored++;
            if (band is EurekaExpBand.Chain or EurekaExpBand.Peak)
                worthExp++;
            mobs++;
        }

        Status = $"{EurekaZones.Name(territory)} · your elemental Lv {(myLevel > 0 ? myLevel : "not read")} · "
                 + $"{mobs} mobs ({unknown} with no aggro on record, {ignored} won't aggro you, {worthExp} chain EXP) · "
                 + $"{liveFates} live FATE(s){(_showFates() ? "" : " (lines off)")} · {triggers} NM trigger mobs near · "
                 + WeatherCheck(territory, now) + " · "
                 + $"{elementals} elementals, {elementalSpots} elemental spots near · "
                 + (hunting ? $"coffer hunt on: {cofferSpots} spots near, {coffers} coffers" : $"{coffers} coffers, no coffer hunt");
    }

    /// <summary>Draws one mob's aggro shape and label. Returns true when no source covers it;
    /// <paramref name="ignores"/> is true when the mob is low enough to leave you alone.</summary>
    private bool DrawMob(ImDrawListPtr drawList, IBattleNpc npc, uint territory, int myLevel, out bool ignores,
        out EurekaExpBand band)
    {
        var fact = EurekaMobs.Find(territory, npc.NameId);
        var aggro = fact?.Aggro ?? EurekaAggro.Unknown;
        var range = npc.HitboxRadius + AggroBeyondHitbox;
        var mobLevel = ForayLevel(npc);
        ignores = EurekaAggroRules.IgnoresYou(mobLevel, myLevel);
        band = EurekaExp.Band(mobLevel, myLevel);
        var tint = aggro switch
        {
            EurekaAggro.Sight => SightColor,
            EurekaAggro.TrueSight => TrueSightColor,
            EurekaAggro.Sound => SoundColor,
            EurekaAggro.Proximity => ProximityColor,
            EurekaAggro.Magic => MagicColor,
            EurekaAggro.LowHp => LowHpColor,
            _ => UnknownColor,
        };
        // A mob that will not come for you stays visible but faded: it still matters if you hit it.
        var color = ImGui.GetColorU32(ignores ? tint with { W = tint.W * 0.3f } : tint);

        switch (aggro)
        {
            case EurekaAggro.Sight:
            case EurekaAggro.TrueSight:
                _shapes.Cone(drawList, npc.Position, npc.Rotation, range, color);
                break;
            case EurekaAggro.Sound:
                // Sound's look matches the deep-dungeon ESP: a ring with a filled core.
                _shapes.Circle(drawList, npc.Position, range, color, filled: false);
                _shapes.Circle(drawList, npc.Position, npc.HitboxRadius, color, filled: true);
                break;
            default:
                _shapes.Circle(drawList, npc.Position, range, color, filled: false);
                break;
        }

        var tag = EurekaMobs.Label(aggro, fact?.Confirmed ?? false);
        var level = mobLevel > 0 ? $"Lv{mobLevel}" : "Lv?";
        var text = _showNames() ? $"{npc.Name.TextValue} {level} · {tag}" : tag;
        if (ignores)
            text += " · ignores you";
        var labelColor = LabelColor;
        if (_showExp() && band != EurekaExpBand.Unknown)
        {
            text += " · " + EurekaExp.Label(band);
            labelColor = band switch
            {
                EurekaExpBand.Peak => PeakExpColor,
                EurekaExpBand.Chain => ChainExpColor,
                EurekaExpBand.None => NoExpColor,
                _ => LabelColor,
            };
        }
        _shapes.Label(drawList, npc.Position, text,
            ImGui.GetColorU32(ignores ? labelColor with { W = labelColor.W * 0.5f } : labelColor));
        return fact == null;
    }

    /// <summary>
    /// A line from you to every live Eureka FATE, at any distance (the FATE list is zone-wide), with its
    /// area ringed and a label naming the boss, level, progress and distance. Returns how many were drawn.
    /// </summary>
    private int TrackFates(ImDrawListPtr drawList, Vector3 here, bool draw)
    {
        _liveFateIds.Clear();
        var drawn = 0;
        foreach (var fate in _fates)
        {
            if (fate == null || !EurekaFates.IsLive((int)fate.State))
                continue;
            _liveFateIds.Add(fate.FateId);
            drawn++;
            if (!draw)
                continue;
            var info = EurekaFates.Find(fate.FateId);
            var color = ImGui.GetColorU32(info?.Kind == EurekaFateKind.Bunny ? BunnyColor : NmColor);
            var distance = Vector3.Distance(here, fate.Position);
            _shapes.Line(drawList, here, fate.Position, color);
            if (fate.Radius > 0)
                _shapes.Circle(drawList, fate.Position, fate.Radius, color, filled: false);
            _shapes.Label(drawList, fate.Position,
                EurekaFates.Label(info, fate.Name.TextValue, fate.Progress, distance), color);
        }

        return drawn;
    }

    /// <summary>
    /// A second line over a mob whose kills spawn a notorious monster: which NM, and whether its weather and
    /// time of day allow it now — or how long until they do. Returns true when the mob is a trigger.
    /// </summary>
    private bool DrawTrigger(ImDrawListPtr drawList, IBattleNpc npc, uint territory, long now)
    {
        if (EurekaSpawns.ForMob(territory, npc.NameId) is not { } trigger)
            return false;
        var nm = EurekaFates.Find(trigger.FateId);
        var boss = nm?.Boss ?? "an NM";
        var level = nm is { } n ? $" Lv{n.Level}" : "";
        string text;
        Vector4 color;
        if (_liveFateIds.Contains(trigger.FateId))
        {
            text = $"spawns {boss}{level} - it is up now";
            color = TriggerWaitColor;
        }
        else if (trigger.MetAt(now))
        {
            text = $"kill to spawn {boss}{level}";
            color = TriggerReadyColor;
        }
        else
        {
            text = $"spawns {boss}{level} · needs {trigger.Needs()} ({EurekaSpawns.Describe(EurekaSpawns.NextWindow(trigger, now), now)})";
            color = TriggerWaitColor;
        }

        _shapes.Label(drawList, npc.Position, text, ImGui.GetColorU32(color), 1);
        return true;
    }

    /// <summary>The forecast's weather beside the game's own, so a wrong forecast shows itself.</summary>
    private static unsafe string WeatherCheck(uint territory, long now)
    {
        var forecast = EorzeaClock.WeatherAt(territory, now);
        var manager = WeatherManager.Instance();
        if (manager == null)
            return $"weather {EurekaWeather.Name(forecast)} (forecast; game not read)";
        var game = (uint)manager->GetCurrentWeather();
        return game == forecast
            ? $"weather {EurekaWeather.Name(forecast)} (forecast = game)"
            : $"weather: forecast {EurekaWeather.Name(forecast)}, GAME SAYS {EurekaWeather.Name(game)}";
    }

    /// <summary>
    /// Your elemental level, as the game uses it for aggro: PlayerState content key 2, "Effective Elemental
    /// Level" (after the zone's level sync), else key 4, "Current Elemental Level" — both documented in
    /// ClientStructs' GetContentValue table and only loaded inside Eureka. Mercury reads key 4 the same way.
    /// 0 = not read.
    /// </summary>
    private static unsafe int ElementalLevel()
    {
        var state = PlayerState.Instance();
        if (state == null || !state->IsLoaded)
            return 0;
        var effective = (int)state->GetContentValue(2);
        return effective > 0 ? effective : (int)state->GetContentValue(4);
    }

    /// <summary>
    /// The mob's ELEMENTAL level — the number on its Eureka nameplate — from the character's foray info
    /// (ClientStructs <c>Character.GetForayInfo</c>; BossMod reads every actor's foray level the same way).
    /// <c>IBattleNpc.Level</c> is NOT it: in game it read 70 on a Squib whose nameplate said 1.
    /// 0 = not read.
    /// </summary>
    private static unsafe int ForayLevel(IBattleNpc npc)
    {
        if (npc.Address == nint.Zero)
            return 0;
        var info = ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)npc.Address)->GetForayInfo();
        return info == null ? 0 : info->Level;
    }

    private static string? CofferLabel(uint baseId) => baseId switch
    {
        EurekaZones.GoldCoffer => "Gold coffer",
        EurekaZones.SilverCoffer => "Silver coffer",
        EurekaZones.BronzeCoffer => "Bronze coffer",
        _ => null,
    };
}
