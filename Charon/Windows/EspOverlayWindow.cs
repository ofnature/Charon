using System;
using System.Collections.Generic;
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
    private readonly IPluginLog _log;
    private readonly MobDatabase _mobs;
    private readonly Func<bool> _showMobs;
    private readonly Func<bool> _showChests;
    private readonly Func<bool> _showTraps;
    private readonly Func<bool> _showMobNames;
    private readonly Func<bool> _showHoardLine;
    private readonly Func<Vector3, IReadOnlyList<Spot>> _likelySpots;
    private readonly Func<uint> _contentId;

    /// <summary>How far out likely TRAP spots are drawn: the ones that matter are the ones you could walk
    /// onto. Hoard spots have no cap — they are destinations, and a floor set holds only a few hundred.</summary>
    public const float LikelyTrapRange = 40f;

    public const float LikelyHoardRange = float.MaxValue;

    /// <summary>Palace Pal's own marker radius for both kinds.</summary>
    private const float LikelySpotRadius = 1.7f;

    private static readonly float[] LikelyHoardRings = [LikelySpotRadius, 1.15f, 0.6f];

    /// <summary>EventObj base ids we have no entry for, logged once each — how the tables grow.</summary>
    private readonly HashSet<uint> _unknownIds = new();

    public EspOverlayWindow(IObjectTable objectTable, IGameGui gameGui, IClientState clientState,
        IPluginLog log, MobDatabase mobs, Func<bool> showMobs, Func<bool> showChests,
        Func<bool> showTraps, Func<bool> showMobNames, Func<bool> showHoardLine,
        Func<Vector3, IReadOnlyList<Spot>> likelySpots, Func<uint> contentId)
        : base("##CharonDeepDungeonEsp")
    {
        _objectTable = objectTable;
        _gameGui = gameGui;
        _clientState = clientState;
        _log = log;
        _mobs = mobs;
        _showMobs = showMobs;
        _showChests = showChests;
        _showTraps = showTraps;
        _showMobNames = showMobNames;
        _showHoardLine = showHoardLine;
        _likelySpots = likelySpots;
        _contentId = contentId;

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

    /// <summary>What the last frame actually drew — the Debug line, and the trap diagnostic.</summary>
    public string Status { get; private set; } = "not drawn yet";

    public override void Draw()
    {
        var local = _objectTable.LocalPlayer;
        if (local == null)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var inPotd = DeepDungeonIds.PotdMaps.Contains(_clientState.MapId);
        int mobs = 0, chests = 0, traps = 0;
        var mimicColour = FloorClock.MimicCoffer(_contentId());
        var hoardDistance = -1f;

        // Likely spots first, so anything real draws over them. Outlines only — a filled ring is
        // something the game has actually shown us, an outline is history saying "maybe here".
        var likely = _likelySpots(local.Position);
        foreach (var spot in likely)
        {
            var at = new Vector3(spot.X, spot.Y, spot.Z);
            if (spot.Kind == SpotKind.Trap)
            {
                DrawWorldCircle(drawList, at, LikelySpotRadius, LikelyTrapColor, filled: false);
                continue;
            }

            // Hoard spots are a TRIPLE ring (user, 2026-10-06): a single outline read as one more trap
            // guess among the orange ones. Three concentric rings are unmistakable at any distance.
            foreach (var radius in LikelyHoardRings)
                DrawWorldCircle(drawList, at, radius, LikelyHoardColor, filled: false);
        }

        foreach (var obj in _objectTable)
        {
            if (DeepDungeonIds.Ignored.Contains(obj.BaseId))
                continue;

            var distance = Vector3.Distance(local.Position, obj.Position);

            if (obj.ObjectKind == ObjectKind.BattleNpc)
            {
                if (_showMobs() && distance <= MobDrawRange && DrawMob(drawList, obj, inPotd))
                    mobs++;
                continue;
            }

            var visual = Classify(obj.BaseId);

            // The colour that can be a mimic on this floor set (silver 31-60, gold above) says so — the
            // same rule that stops auto-open touching it (FloorClock.MimicCoffer).
            if ((obj.BaseId == DeepDungeonIds.SilverChest && mimicColour == CofferKind.Silver)
                || (obj.BaseId == DeepDungeonIds.GoldChest && mimicColour == CofferKind.Gold))
                visual = visual with { Color = MimicCofferColor, Label = $"{visual.Label} (mimic?)" };
            if (visual.Kind == FloorKind.None)
            {
                // An unrecognised EventObj is either scenery or an id we don't know yet (a newer
                // dungeon's traps would look exactly like this) — log each once so the table can
                // grow from evidence instead of the overlay silently drawing nothing.
                if (obj.ObjectKind == ObjectKind.EventObj && _unknownIds.Add(obj.BaseId))
                    _log.Info("Deep dungeon ESP: unrecognised EventObj {0} '{1}'", obj.BaseId, obj.Name.TextValue);
                continue;
            }

            // The hoard gets a line from us, at ANY distance: Intuition reveals it rooms away, which is
            // exactly where the 35y highlight range would hide it. Its marker skips that cap too.
            var isHoard = obj.BaseId is DeepDungeonIds.AccursedHoard or DeepDungeonIds.AccursedHoardCoffer;
            if (isHoard)
            {
                hoardDistance = distance;
                if (_showHoardLine())
                    DrawWorldLine(drawList, local.Position, obj.Position, ImGui.GetColorU32(visual.Color));
            }

            // Traps carry their OWN toggle and no distance cap — NecroLens draws them wherever
            // they are on screen, and only chests/waypoints get the 35y highlight range.
            if (visual.Kind == FloorKind.Trap)
            {
                if (!_showTraps())
                    continue;
                traps++;
            }
            else
            {
                if (!_showChests() || (distance > ChestDrawRange && !isHoard))
                    continue;
                chests++;
            }

            DrawFloorObject(drawList, obj, visual);
        }

        Status = $"{mobs} mobs · {chests} chests/waypoints · {traps} traps · {likely.Count} likely spots"
                 + (hoardDistance >= 0 ? $" · hoard {hoardDistance:0}y" : "")
                 + (_unknownIds.Count > 0 ? $" · {_unknownIds.Count} unknown ids (see log)" : "");
    }

    // --- Mobs: aggro circles by how they notice you, patrol arrows, names ---

    private bool DrawMob(ImDrawListPtr drawList, IGameObject obj, bool inPotd)
    {
        if (obj is not IBattleNpc npc || npc.CurrentHp == 0)
            return false;

        if (npc.SubKind != (byte)BattleNpcSubKind.Combatant
            || DeepDungeonIds.FriendlyNames.Contains(npc.NameId))
            return false;

        // Aggroed mobs are the rotation's problem — circles only matter before the pull.
        if (npc.StatusFlags.HasFlag(StatusFlags.InCombat))
            return false;

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

        return true;
    }

    // --- Chests, passage, return, traps ---

    private enum FloorKind
    {
        None,
        Chest,
        Trap,
        Waypoint,
    }

    private readonly record struct FloorVisual(FloorKind Kind, float Radius, Vector4 Color, string Label);

    private static FloorVisual Classify(uint baseId) => baseId switch
    {
        _ when DeepDungeonIds.BronzeChests.Contains(baseId) =>
            new(FloorKind.Chest, 1f, new Vector4(0.80f, 0.55f, 0.30f, 0.9f), "Bronze"),
        DeepDungeonIds.SilverChest => new(FloorKind.Chest, 1f, new Vector4(0.85f, 0.85f, 0.90f, 0.9f), "Silver"),
        DeepDungeonIds.GoldChest => new(FloorKind.Chest, 1f, new Vector4(1f, 0.84f, 0.25f, 0.9f), "Gold"),
        DeepDungeonIds.MimicChest => new(FloorKind.Chest, 1f, new Vector4(1f, 0.30f, 0.30f, 0.95f), "MIMIC?"),
        DeepDungeonIds.AccursedHoard => new(FloorKind.Chest, 2f, new Vector4(0.55f, 0.90f, 0.95f, 0.9f), "Hoard"),
        DeepDungeonIds.AccursedHoardCoffer => new(FloorKind.Chest, 1f, new Vector4(0.55f, 0.90f, 0.95f, 0.9f), "Hoard"),
        _ when DeepDungeonIds.Passages.Contains(baseId) =>
            new(FloorKind.Waypoint, 2f, new Vector4(0.35f, 0.85f, 0.45f, 0.9f), "Passage"),
        _ when DeepDungeonIds.Returns.Contains(baseId) =>
            new(FloorKind.Waypoint, 2f, new Vector4(0.90f, 0.80f, 0.30f, 0.9f), "Return"),
        _ when DeepDungeonIds.Traps.ContainsKey(baseId) =>
            new(FloorKind.Trap, 1.7f, new Vector4(1f, 0.25f, 0.25f, 0.95f), DeepDungeonIds.Traps[baseId]),
        _ => new(FloorKind.None, 0f, default, ""),
    };

    /// <summary>The mimic chest's own red, so a maybe-mimic coffer reads as the same danger.</summary>
    private static readonly Vector4 MimicCofferColor = new(1f, 0.30f, 0.30f, 0.95f);

    private static readonly uint LikelyTrapColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.55f, 0.25f, 0.55f));
    private static readonly uint LikelyHoardColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.85f, 1f, 0.85f));

    private void DrawFloorObject(ImDrawListPtr drawList, IGameObject obj, FloorVisual visual)
    {
        var packed = ImGui.GetColorU32(visual.Color);
        DrawWorldCircle(drawList, obj.Position, visual.Radius, packed, filled: true);

        if (_gameGui.WorldToScreen(obj.Position, out var screen))
        {
            var size = ImGui.CalcTextSize(visual.Label);
            drawList.AddText(new Vector2(screen.X - size.X / 2f, screen.Y - size.Y - 2f), packed, visual.Label);
        }
    }

    // --- NecroLens's projection helpers: world-radius shapes, point-by-point WorldToScreen ---

    /// <summary>
    /// A world-space line, projected in pieces. A single screen-space line fails the moment one end is
    /// behind the camera (its projection is garbage), so the line is sampled and only the stretches in
    /// FRONT of the camera are drawn — turn away from the hoard and the line still points toward it.
    /// </summary>
    private void DrawWorldLine(ImDrawListPtr drawList, Vector3 from, Vector3 to, uint color)
    {
        const int segments = 32;
        var drawing = false;

        for (var i = 0; i <= segments; i++)
        {
            var point = Vector3.Lerp(from, to, (float)i / segments);
            if (_gameGui.WorldToScreen(point, out var screen, out _))
            {
                drawList.PathLineTo(screen);
                drawing = true;
            }
            else if (drawing)
            {
                drawList.PathStroke(color, ImDrawFlags.None, 2.5f);
                drawing = false;
            }
        }

        if (drawing)
            drawList.PathStroke(color, ImDrawFlags.None, 2.5f);
        drawList.PathClear();
    }

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
