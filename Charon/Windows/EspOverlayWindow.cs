using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Charon.Features.DeepDungeon;
using Charon.Services.Game;

namespace Charon.Windows;

/// <summary>
/// The deep-dungeon ESP: chests, passage/return, traps, and mob aggro ranges drawn over the
/// world. Rendering and rules ported from NecroLens (MIT, Jukkales/NecroLens) — plain 2D
/// screen-space, WorldToScreen per point, no extra dependencies: aggro circles are world-radius
/// circles projected segment by segment, sight cones sweep ±45° of the mob's facing.
///
/// Opened by the plugin ONLY while a deep-dungeon instance is live (same gate as the floor
/// map). Fullscreen, no background, no inputs — it can never eat a click.
/// </summary>
public sealed class EspOverlayWindow : Window
{
    private const int CircleSegments = 48;
    private const float MobDrawRange = 50f;
    private const float ChestDrawRange = 35f;

    private readonly IObjectTable _objectTable;
    private readonly IGameGui _gameGui;
    private readonly IClientState _clientState;
    private readonly MobDatabase _mobs;
    private readonly Func<bool> _showMobs;
    private readonly Func<bool> _showChests;
    private readonly Func<bool> _showMobNames;

    public EspOverlayWindow(IObjectTable objectTable, IGameGui gameGui, IClientState clientState,
        MobDatabase mobs, Func<bool> showMobs, Func<bool> showChests, Func<bool> showMobNames)
        : base("##CharonDeepDungeonEsp")
    {
        _objectTable = objectTable;
        _gameGui = gameGui;
        _clientState = clientState;
        _mobs = mobs;
        _showMobs = showMobs;
        _showChests = showChests;
        _showMobNames = showMobNames;

        Flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        RespectCloseHotkey = false;
        IsOpen = false;
    }

    public override void PreDraw()
    {
        var viewport = ImGui.GetMainViewport();
        Position = viewport.Pos;
        Size = viewport.Size;
    }

    public override void Draw()
    {
        var local = _objectTable.LocalPlayer;
        if (local == null)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var inPotd = DeepDungeonIds.PotdMaps.Contains(_clientState.MapId);

        foreach (var obj in _objectTable)
        {
            if (DeepDungeonIds.Ignored.Contains(obj.BaseId))
                continue;

            var distance = Vector3.Distance(local.Position, obj.Position);

            if (obj.ObjectKind == ObjectKind.BattleNpc)
            {
                if (_showMobs() && distance <= MobDrawRange)
                    DrawMob(drawList, obj, inPotd);
                continue;
            }

            if (_showChests() && distance <= ChestDrawRange)
                DrawFloorObject(drawList, obj);
        }
    }

    // --- Mobs: aggro circles by how they notice you, patrol arrows, names ---

    private void DrawMob(ImDrawListPtr drawList, IGameObject obj, bool inPotd)
    {
        if (obj is not IBattleNpc npc || npc.CurrentHp == 0)
            return;

        if (npc.SubKind != (byte)BattleNpcSubKind.Combatant
            || DeepDungeonIds.FriendlyNames.Contains(npc.NameId))
            return;

        // Aggroed mobs are the rotation's problem — circles only matter before the pull.
        if (npc.StatusFlags.HasFlag(StatusFlags.InCombat))
            return;

        var record = _mobs.Find(npc.NameId);
        var isMimic = DeepDungeonIds.MimicNames.Contains(npc.NameId);

        // NecroLens's numbers: hitbox + ~10y is a safe general aggro radius; PotD mimics ~14y.
        var aggroRange = npc.HitboxRadius + (isMimic && inPotd ? 14f : 10f);
        var aggro = record?.Aggro ?? MobAggro.Proximity;

        var color = aggro switch
        {
            MobAggro.Sound => ImGui.GetColorU32(new Vector4(0.85f, 0.75f, 0.20f, 0.9f)),
            _ => ImGui.GetColorU32(new Vector4(0.80f, 0.25f, 0.25f, 0.9f)),
        };

        switch (aggro)
        {
            case MobAggro.Sight:
                DrawCone(drawList, obj.Position, obj.Rotation, aggroRange, color);
                break;
            case MobAggro.Sound:
                DrawWorldCircle(drawList, obj.Position, aggroRange, color, filled: false);
                DrawWorldCircle(drawList, obj.Position, npc.HitboxRadius, color, filled: true);
                break;
            default:
                DrawWorldCircle(drawList, obj.Position, aggroRange, color, filled: false);
                break;
        }

        if (record is { Patrol: true })
            DrawFacingArrow(drawList, obj.Position, obj.Rotation, ImGui.GetColorU32(new Vector4(1f, 0.3f, 0.3f, 1f)));

        if (_showMobNames() && _gameGui.WorldToScreen(obj.Position, out var screen))
        {
            var name = obj.Name.TextValue;
            if (record is { Patrol: true })
                name += " (patrol)";
            var size = ImGui.CalcTextSize(name);
            drawList.AddText(new Vector2(screen.X - size.X / 2f, screen.Y - size.Y - 2f),
                ImGui.GetColorU32(new Vector4(0.95f, 0.95f, 0.95f, 0.9f)), name);
        }
    }

    // --- Chests, passage, return, traps ---

    private void DrawFloorObject(ImDrawListPtr drawList, IGameObject obj)
    {
        var baseId = obj.BaseId;
        var (radius, color, label) = baseId switch
        {
            _ when DeepDungeonIds.BronzeChests.Contains(baseId) =>
                (1f, new Vector4(0.80f, 0.55f, 0.30f, 0.9f), "Bronze"),
            DeepDungeonIds.SilverChest => (1f, new Vector4(0.85f, 0.85f, 0.90f, 0.9f), "Silver"),
            DeepDungeonIds.GoldChest => (1f, new Vector4(1f, 0.84f, 0.25f, 0.9f), "Gold"),
            DeepDungeonIds.MimicChest => (1f, new Vector4(1f, 0.30f, 0.30f, 0.95f), "MIMIC?"),
            DeepDungeonIds.AccursedHoard => (2f, new Vector4(0.55f, 0.90f, 0.95f, 0.9f), "Hoard"),
            DeepDungeonIds.AccursedHoardCoffer => (1f, new Vector4(0.55f, 0.90f, 0.95f, 0.9f), "Hoard"),
            _ when DeepDungeonIds.Passages.Contains(baseId) =>
                (2f, new Vector4(0.35f, 0.85f, 0.45f, 0.9f), "Passage"),
            _ when DeepDungeonIds.Returns.Contains(baseId) =>
                (2f, new Vector4(0.90f, 0.80f, 0.30f, 0.9f), "Return"),
            _ when DeepDungeonIds.Traps.ContainsKey(baseId) =>
                (1.7f, new Vector4(1f, 0.25f, 0.25f, 0.95f), DeepDungeonIds.Traps[baseId]),
            _ => (0f, default, ""),
        };

        if (radius <= 0f)
            return;

        var packed = ImGui.GetColorU32(color);
        DrawWorldCircle(drawList, obj.Position, radius, packed, filled: true);

        if (_gameGui.WorldToScreen(obj.Position, out var screen))
        {
            var size = ImGui.CalcTextSize(label);
            drawList.AddText(new Vector2(screen.X - size.X / 2f, screen.Y - size.Y - 2f), packed, label);
        }
    }

    // --- NecroLens's projection helpers: world-radius shapes, point-by-point WorldToScreen ---

    private void DrawWorldCircle(ImDrawListPtr drawList, Vector3 center, float radius, uint color, bool filled)
    {
        for (var i = 0; i <= CircleSegments; i++)
        {
            var angle = 2f * MathF.PI * i / CircleSegments;
            _gameGui.WorldToScreen(
                new Vector3(center.X + radius * MathF.Sin(angle), center.Y, center.Z + radius * MathF.Cos(angle)),
                out var point);
            drawList.PathLineTo(point);
        }

        if (filled)
        {
            drawList.PathFillConvex((color & 0x00FFFFFF) | 0x40000000);
            // redraw the outline — the fill consumed the path
            for (var i = 0; i <= CircleSegments; i++)
            {
                var angle = 2f * MathF.PI * i / CircleSegments;
                _gameGui.WorldToScreen(
                    new Vector3(center.X + radius * MathF.Sin(angle), center.Y, center.Z + radius * MathF.Cos(angle)),
                    out var point);
                drawList.PathLineTo(point);
            }
        }

        drawList.PathStroke(color, ImDrawFlags.None, 1.5f);
        drawList.PathClear();
    }

    /// <summary>NecroLens's sight cone: ±45° around the mob's facing (their rotation+π/4 sweep).</summary>
    private void DrawCone(ImDrawListPtr drawList, Vector3 center, float rotation, float radius, uint color)
    {
        const float coneAngle = 1.571f; // 90° total
        var start = rotation + MathF.PI / 4f;
        var step = coneAngle / CircleSegments;

        _gameGui.WorldToScreen(center, out var origin);
        drawList.PathLineTo(origin);
        for (var i = 0; i <= CircleSegments; i++)
        {
            var angle = start - i * step;
            _gameGui.WorldToScreen(
                new Vector3(center.X + radius * MathF.Sin(angle), center.Y, center.Z + radius * MathF.Cos(angle)),
                out var point);
            drawList.PathLineTo(point);
        }

        drawList.PathLineTo(origin);
        drawList.PathFillConvex((color & 0x00FFFFFF) | 0x33000000);

        drawList.PathLineTo(origin);
        for (var i = 0; i <= CircleSegments; i++)
        {
            var angle = start - i * step;
            _gameGui.WorldToScreen(
                new Vector3(center.X + radius * MathF.Sin(angle), center.Y, center.Z + radius * MathF.Cos(angle)),
                out var point);
            drawList.PathLineTo(point);
        }

        drawList.PathLineTo(origin);
        drawList.PathStroke(color, ImDrawFlags.None, 1.5f);
        drawList.PathClear();
    }

    private void DrawFacingArrow(ImDrawListPtr drawList, Vector3 center, float rotation, uint color)
    {
        var tip = new Vector3(center.X + 2.2f * MathF.Sin(rotation), center.Y, center.Z + 2.2f * MathF.Cos(rotation));
        if (!_gameGui.WorldToScreen(center, out var from) || !_gameGui.WorldToScreen(tip, out var to))
            return;

        drawList.AddLine(from, to, color, 2.5f);
        var dir = Vector2.Normalize(to - from);
        var normal = new Vector2(-dir.Y, dir.X);
        drawList.AddTriangleFilled(to + dir * 6f, to + normal * 4f, to - normal * 4f, color);
    }
}
