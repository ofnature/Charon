using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Charon.Features.FcChest;
using Charon.Services.Game;

namespace Charon.Windows;

/// <summary>
/// The FC chest management body, shared by the main window's FC Chest section and the
/// standalone <see cref="FcChestWindow"/> that pops next to the game chest. Pure drawing —
/// state lives in <see cref="FcChestManager"/> and <see cref="CharonConfig"/>.
/// </summary>
internal static class FcChestView
{
    internal const float MinFontScale = 1.0f;
    internal const float MaxFontScale = 2.5f;

    /// <summary>
    /// Draws the body at the user's text scale (accessibility — the item list is small by
    /// default). SetWindowFontScale scales TEXT only, so every fixed pixel size in here is
    /// multiplied by the same factor to keep the layout proportional. The scale is reset
    /// before returning so it never leaks into the rest of the host window.
    /// </summary>
    public static void DrawBody(CharonConfig config, Action save, FcChestManager fcChest)
    {
        var scale = Math.Clamp(config.FcChestFontScale, MinFontScale, MaxFontScale);
        ImGui.SetWindowFontScale(scale);
        try
        {
            DrawScaledBody(config, save, fcChest, scale);
        }
        finally
        {
            ImGui.SetWindowFontScale(1f);
        }
    }

    private static void DrawScaledBody(CharonConfig config, Action save, FcChestManager fcChest, float scale)
    {
        var page = Math.Clamp(config.LastSelectedChestPage, 1, 5);
        ImGui.SetNextItemWidth(120f * scale);
        if (ImGui.BeginCombo("Chest Page", $"Page {page}"))
        {
            for (var i = 1; i <= 5; i++)
            {
                if (ImGui.Selectable($"Page {i}", i == page))
                {
                    config.LastSelectedChestPage = i;
                    save();
                }
            }
            ImGui.EndCombo();
        }

        // Accessibility: text size for this panel, persisted.
        var scalePercent = scale * 100f;
        ImGui.SetNextItemWidth(120f * scale);
        if (ImGui.SliderFloat("Text Size", ref scalePercent, MinFontScale * 100f, MaxFontScale * 100f, "%.0f%%"))
        {
            config.FcChestFontScale = Math.Clamp(scalePercent / 100f, MinFontScale, MaxFontScale);
            save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Make the item list bigger/smaller. Applies to this panel only.");

        var chestOpen = fcChest.IsChestOpen();
        var pageLoaded = chestOpen && fcChest.IsPageLoaded(page);
        var canOperate = FcChestPlanner.CanExecute(chestOpen, pageLoaded) && !fcChest.Busy;

        ImGui.Spacing();
        if (!canOperate) ImGui.BeginDisabled();
        if (ImGui.Button("Entrust Duplicates") && canOperate)
            ImGui.OpenPopup("fcChestConfirm");
        if (!canOperate) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!chestOpen
                ? "Must be near the FC chest — open the chest window first"
                : !pageLoaded
                    ? $"View the Page {page} tab in the chest once so its contents load"
                    : fcChest.Busy
                        ? "Operation in progress"
                        : $"Entrust every inventory stack of items already on Page {page}");

        // Confirm modal — the moves are irreversible.
        if (ImGui.BeginPopupModal("fcChestConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Entrust duplicate stacks to Page {page}?");
            ImGui.TextColored(CharonTheme.TextSecondary, "Whole stacks are moved — this cannot be undone.");
            ImGui.Spacing();

            if (ImGui.Button("Confirm", new Vector2(120f * scale, 0)))
            {
                fcChest.StartEntrust(page);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(120f * scale, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        // Page contents with per-item withdraw.
        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextSecondary, $"Page {page} contents");
        if (!pageLoaded)
        {
            ImGui.TextColored(CharonTheme.TextDisabled, chestOpen
                ? $"View the Page {page} tab in the chest once so its contents load."
                : "Open the FC chest to see this page.");
        }
        else
        {
            var contents = fcChest.GetPageContents(page);
            if (contents.Count == 0)
            {
                ImGui.TextColored(CharonTheme.TextDisabled, "Page is empty.");
            }
            else if (ImGui.BeginTable("fcContents", 4,
                         ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit
                         | ImGuiTableFlags.ScrollY, new Vector2(0f, 220f * scale)))
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 60f * scale);
                ImGui.TableSetupColumn("Stacks", ImGuiTableColumnFlags.WidthFixed, 50f * scale);
                ImGui.TableSetupColumn("##act", ImGuiTableColumnFlags.WidthFixed, 130f * scale);
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (var row in contents)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(row.Name);
                    ImGui.TableNextColumn();
                    ImGui.TextColored(CharonTheme.TextSecondary, $"×{row.TotalQuantity}");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(CharonTheme.TextSecondary, row.StackCount.ToString());
                    ImGui.TableNextColumn();

                    if (row.TotalQuantity <= 1)
                    {
                        ImGui.TextColored(CharonTheme.TextDisabled, "seed");
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip("Only the seed unit remains — always stays");
                    }
                    else
                    {
                        if (fcChest.Busy) ImGui.BeginDisabled();
                        if (ImGui.SmallButton($"Withdraw all but 1##wd{row.ItemId}") && !fcChest.Busy)
                            fcChest.StartWithdrawItem(page, row.ItemId);
                        if (fcChest.Busy) ImGui.EndDisabled();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"Withdraw ×{row.TotalQuantity - 1} — exactly 1 unit stays as the seed\n"
                                             + "(withdraw all, split 1 in bags, return it as the seed)");
                    }
                }

                ImGui.EndTable();
            }
        }

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextSecondary, $"Status: {fcChest.Status}");
        if (fcChest.LastOperation.Length > 0)
            ImGui.TextColored(CharonTheme.TextSecondary, $"Last operation: {fcChest.LastOperation}");

        if (fcChest.OperationLog.Count > 0)
        {
            if (fcChest.OperationJustFinished)
            {
                ImGui.SetNextItemOpen(true);
                fcChest.OperationJustFinished = false;
            }
            else if (config.ShowFCChestLog)
            {
                ImGui.SetNextItemOpen(true, ImGuiCond.Once);
            }

            if (ImGui.TreeNode($"Items ({fcChest.OperationLog.Count})##fcChestLog"))
            {
                foreach (var entry in fcChest.OperationLog)
                {
                    var failed = entry.Verb.StartsWith("FAILED", StringComparison.Ordinal);
                    ImGui.TextColored(failed ? CharonTheme.StatusRed : CharonTheme.TextDisabled,
                        $"{entry.Name}  ×{entry.Quantity} → {entry.Verb}");
                }
                ImGui.TreePop();
            }
        }
    }
}
