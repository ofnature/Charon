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

    /// <summary>Last-typed exact-withdraw amount (session-scoped UI state, shared across rows).</summary>
    private static int _withdrawAmount = 1;

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

        // Deposit All — everything tradeable in the bags, all five tabs (unviewed tabs are
        // loaded first by clicking them for you). Crystals/gil/untradeables stay put.
        ImGui.SameLine();
        var canDepositAll = chestOpen && !fcChest.Busy;
        if (!canDepositAll) ImGui.BeginDisabled();
        if (ImGui.Button("Deposit All") && canDepositAll)
            ImGui.OpenPopup("fcDepositAllConfirm");
        if (!canDepositAll) ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(!chestOpen
                ? "Must be near the FC chest — open the chest window first"
                : fcChest.Busy
                    ? "Operation in progress"
                    : "Deposit every bag stack of items the chest ALREADY holds (all tabs).\n"
                      + "Items not in the chest, crystals, gil and untradeables stay put.");

        if (ImGui.BeginPopupModal("fcDepositAllConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("Deposit every bag stack of items the chest already holds?");
            ImGui.TextColored(CharonTheme.TextSecondary,
                "Duplicates only — the chest's contents are the shopping list; nothing new is\n"
                + "seeded. Fills matching chest stacks first, then empty slots, across all five\n"
                + "tabs. Crystals, gil and untradeables stay put. This cannot be undone.");
            if (!fcChest.QuantityMovesAvailable)
                ImGui.TextColored(CharonTheme.StatusYellow,
                    "Quantity moves unavailable — only whole stacks into empty slots this session.");
            ImGui.Spacing();

            if (ImGui.Button("Confirm##depAll", new Vector2(120f * scale, 0)))
            {
                fcChest.StartDepositAll();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##depAll", new Vector2(120f * scale, 0)))
                ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }

        var searchEnabled = config.FcChestSearchEnabled;
        if (ImGui.Checkbox("Search bar on the chest window", ref searchEnabled))
        {
            config.FcChestSearchEnabled = searchEnabled;
            save();
        }
        CharonTheme.HelpMarker("A search field over the game's FC chest window — items that don't\n"
                               + "match what you type dim out, and tabs with no match dim too.");

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
                        // Snapshot Busy: clicking starts an operation that flips it mid-draw,
                        // which would leave BeginDisabled/EndDisabled unbalanced and corrupt
                        // ImGui's stack (throws every frame after).
                        var busy = fcChest.Busy;
                        if (busy) ImGui.BeginDisabled();
                        if (ImGui.SmallButton($"Withdraw all but 1##wd{row.ItemId}") && !busy)
                            fcChest.StartWithdrawItem(page, row.ItemId);
                        if (busy) ImGui.EndDisabled();
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"Withdraw ×{row.TotalQuantity - 1} — exactly 1 unit stays as the seed\n"
                                             + "(withdraw all, split 1 in bags, return it as the seed)");

                        // Exact-amount withdraw (native quantity move — no split, no prompt).
                        if (fcChest.QuantityMovesAvailable)
                        {
                            ImGui.SameLine();
                            if (busy) ImGui.BeginDisabled();
                            if (ImGui.SmallButton($"…##wdq{row.ItemId}") && !busy)
                            {
                                _withdrawAmount = Math.Min(_withdrawAmount, row.TotalQuantity);
                                if (_withdrawAmount < 1)
                                    _withdrawAmount = 1;
                                ImGui.OpenPopup($"wdAmt{row.ItemId}");
                            }
                            if (busy) ImGui.EndDisabled();
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip("Withdraw an exact amount");

                            if (ImGui.BeginPopup($"wdAmt{row.ItemId}"))
                            {
                                ImGui.TextUnformatted(row.Name);
                                ImGui.SetNextItemWidth(120f * scale);
                                if (ImGui.InputInt($"##amt{row.ItemId}", ref _withdrawAmount))
                                    _withdrawAmount = Math.Clamp(_withdrawAmount, 1, row.TotalQuantity);
                                ImGui.SameLine();
                                ImGui.TextColored(CharonTheme.TextSecondary, $"of {row.TotalQuantity}");
                                if (ImGui.Button($"Withdraw ×{_withdrawAmount}##go{row.ItemId}"))
                                {
                                    fcChest.StartWithdrawAmount(page, row.ItemId, _withdrawAmount);
                                    ImGui.CloseCurrentPopup();
                                }
                                ImGui.EndPopup();
                            }
                        }
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
