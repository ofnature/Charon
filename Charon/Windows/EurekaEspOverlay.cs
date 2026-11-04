using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Charon.Features.Eureka;
using Charon.Windows.Components;
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

    private readonly IObjectTable _objectTable;
    private readonly IClientState _clientState;
    private readonly WorldShapes _shapes;
    private readonly Func<bool> _showMobs;
    private readonly Func<bool> _showNames;
    private readonly Func<bool> _showElementalSpots;
    private readonly Func<bool> _showCofferSpots;

    public EurekaEspOverlay(IObjectTable objectTable, IGameGui gameGui, IClientState clientState,
        Func<bool> showMobs, Func<bool> showNames, Func<bool> showElementalSpots, Func<bool> showCofferSpots)
        : base("##CharonEurekaEsp")
    {
        _objectTable = objectTable;
        _clientState = clientState;
        _shapes = new WorldShapes(gameGui);
        _showMobs = showMobs;
        _showNames = showNames;
        _showElementalSpots = showElementalSpots;
        _showCofferSpots = showCofferSpots;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

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
        int mobs = 0, unknown = 0, ignored = 0, elementals = 0, coffers = 0, elementalSpots = 0, cofferSpots = 0;
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

            if (DrawMob(drawList, npc, territory, myLevel, out var ignores))
                unknown++;
            if (ignores)
                ignored++;
            mobs++;
        }

        Status = $"{EurekaZones.Name(territory)} · your elemental Lv {(myLevel > 0 ? myLevel : "not read")} · "
                 + $"{mobs} mobs ({unknown} with no aggro on record, {ignored} won't aggro you) · "
                 + $"{elementals} elementals, {elementalSpots} elemental spots near · "
                 + (hunting ? $"coffer hunt on: {cofferSpots} spots near, {coffers} coffers" : $"{coffers} coffers, no coffer hunt");
    }

    /// <summary>Draws one mob's aggro shape and label. Returns true when no source covers it;
    /// <paramref name="ignores"/> is true when the mob is low enough to leave you alone.</summary>
    private bool DrawMob(ImDrawListPtr drawList, IBattleNpc npc, uint territory, int myLevel, out bool ignores)
    {
        var fact = EurekaMobs.Find(territory, npc.NameId);
        var aggro = fact?.Aggro ?? EurekaAggro.Unknown;
        var range = npc.HitboxRadius + AggroBeyondHitbox;
        ignores = EurekaAggroRules.IgnoresYou(npc.Level, myLevel);
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
        var text = _showNames() ? $"{npc.Name.TextValue} Lv{npc.Level} · {tag}" : tag;
        if (ignores)
            text += " · ignores you";
        _shapes.Label(drawList, npc.Position, text,
            ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, ignores ? 0.45f : 0.9f)));
        return fact == null;
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

    private static string? CofferLabel(uint baseId) => baseId switch
    {
        EurekaZones.GoldCoffer => "Gold coffer",
        EurekaZones.SilverCoffer => "Silver coffer",
        EurekaZones.BronzeCoffer => "Bronze coffer",
        _ => null,
    };
}
