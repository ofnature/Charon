using System;
using System.Linq;
using System.Numerics;
using Charon.Features.Variant;
using Charon.Services.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace Charon.Windows;

/// <summary>
/// Labels the guided route's objects in the world while inside a variant dungeon: a ring and a line of
/// text on each EventObj / NPC the route names, and on its fixed spots. Fullscreen, no background, no
/// inputs — it can never eat a click (the deep dungeon ESP's shape). Only the GUIDED route is drawn, so a
/// lever reads "pull the RIGHT lever" rather than every route's instruction at once.
/// </summary>
public sealed class VariantMarkerOverlay : Window
{
    private const int CircleSegments = 40;
    private const float ObjectRing = 1.2f;
    private const float SpotRing = 2.5f;

    /// <summary>Fixed spots further than this are not drawn — they belong to another part of the dungeon.</summary>
    private const float SpotRange = 60f;

    private static readonly Vector4 MarkerColor = new(0.47f, 0.94f, 0.92f, 0.95f);

    private readonly IObjectTable _objectTable;
    private readonly IGameGui _gameGui;
    private readonly VariantService _service;

    public VariantMarkerOverlay(IObjectTable objectTable, IGameGui gameGui, VariantService service)
        : base("##CharonVariantMarkers")
    {
        _objectTable = objectTable;
        _gameGui = gameGui;
        _service = service;

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
        var dungeon = _service.Current;
        if (local == null || dungeon == null)
            return;

        var route = _service.Target(dungeon);
        var markers = dungeon.MarkersFor(route).ToList();
        var drawList = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(MarkerColor);
        int objects = 0, spots = 0;

        foreach (var marker in markers.Where(m => m.Kind == MarkerKind.Spot))
        {
            var at = new Vector3(marker.X, marker.Y, marker.Z);
            if (Vector3.Distance(local.Position, at) > SpotRange)
                continue;
            if (Draw(drawList, at, SpotRing, color, marker.Label))
                spots++;
        }

        foreach (var obj in _objectTable)
        {
            var match = markers.FirstOrDefault(m => Matches(m, obj));
            if (match == null)
                continue;
            if (Draw(drawList, obj.Position, ObjectRing, color, match.Label))
                objects++;
        }

        _service.OverlayStatus = markers.Count == 0
            ? $"route {route} has no markers"
            : $"route {route}: {objects} objects + {spots} spots on screen of {markers.Count}";
    }

    private static bool Matches(RouteMarker marker, IGameObject obj) => marker.Kind switch
    {
        MarkerKind.EventObj => obj.ObjectKind == ObjectKind.EventObj && obj.BaseId == marker.Id,
        MarkerKind.Npc => obj is ICharacter character && character.NameId == marker.Id,
        _ => false,
    };

    /// <summary>A world ring and its label; false when the point is behind the camera or off screen.</summary>
    private bool Draw(ImDrawListPtr drawList, Vector3 at, float radius, uint color, string label)
    {
        if (!_gameGui.WorldToScreen(at, out var screen))
            return false;

        for (var i = 0; i <= CircleSegments; i++)
        {
            var angle = 2f * MathF.PI * i / CircleSegments;
            if (_gameGui.WorldToScreen(new Vector3(at.X + radius * MathF.Sin(angle), at.Y, at.Z + radius * MathF.Cos(angle)),
                    out var point))
                drawList.PathLineTo(point);
        }

        drawList.PathStroke(color, ImDrawFlags.None, 2f);
        drawList.PathClear();

        var size = ImGui.CalcTextSize(label);
        var origin = new Vector2(screen.X - size.X / 2f, screen.Y - size.Y - 6f);
        drawList.AddRectFilled(origin - new Vector2(4f, 2f), origin + size + new Vector2(4f, 2f),
            ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.55f)), 3f);
        drawList.AddText(origin, color, label);
        return true;
    }
}
