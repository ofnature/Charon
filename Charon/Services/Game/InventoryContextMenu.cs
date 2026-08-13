using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Charon.Services.Game;

/// <summary>One stack of an item sitting in a player bag.</summary>
public readonly record struct BagStack(InventoryType Container, int Slot, int Quantity);

/// <summary>
/// The item context menu, driven the way SimpleTweaks QuickSellItems and DailyRoutines
/// AutoSplitStacks drive it in production (verified sources, not guesses):
/// AgentInventoryContext.OpenForItemSlot opens the menu, the entries live in the agent's
/// EventParams from ContexItemStartIndex (sic — the ClientStructs field really is missing a
/// 't'), and a click is callback [0, entryIndex, 0, 0, 0] on the context-menu addon.
///
/// Entry matching is by Addon-sheet text (row 93 "Sell", row 92 "Split"), so it is
/// language-independent — both sides of the comparison are localized. Shared by the gil-cap
/// seller and the Doman donator, which are the same split arithmetic pointed at different
/// windows.
/// </summary>
public static unsafe class InventoryContextMenu
{
    public const uint SellTextRow = 93;   // Addon sheet: "Sell"
    public const uint SplitTextRow = 92;  // Addon sheet: "Split"

    public static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    public static long CurrentGil()
    {
        var inventory = InventoryManager.Instance();
        return inventory == null ? 0 : inventory->GetInventoryItemCount(1);
    }

    /// <summary>Every stack of <paramref name="itemId"/> in the player bags, live.</summary>
    public static List<BagStack> FindStacks(uint itemId)
    {
        var stacks = new List<BagStack>();
        var manager = InventoryManager.Instance();
        if (manager == null)
            return stacks;

        foreach (var bag in PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded)
                continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->GetBaseItemId() == itemId && slot->GetQuantity() > 0)
                    stacks.Add(new BagStack(bag, i, (int)slot->GetQuantity()));
            }
        }

        return stacks;
    }

    /// <summary>Outcome of one click attempt on the open menu.</summary>
    public enum ClickResult
    {
        /// <summary>The menu isn't up yet — try again next tick (it needs a frame to build).</summary>
        NotReady,

        /// <summary>Entry found and clicked; the menu was closed.</summary>
        Clicked,

        /// <summary>The menu is up but has no such entry (wrong window state); menu closed.</summary>
        EntryMissing,
    }

    /// <summary>
    /// Ask the game to open <paramref name="stack"/>'s context menu. TWO preconditions learned
    /// the hard way (both are in DailyRoutines' production flow):
    /// - The INVENTORY WINDOW must be visible first — OpenForItemSlot silently does nothing
    ///   without it, and an unattended toon never has it open. False = we opened/asked for the
    ///   inventory instead; call again next tick.
    /// - The menu then takes a frame to build — clicking in the same tick does nothing, so
    ///   follow with <see cref="TryClickEntry"/> on subsequent ticks.
    /// </summary>
    public static bool OpenMenu(BagStack stack)
    {
        var agent = AgentInventoryContext.Instance();
        var inventoryAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        if (agent == null || inventoryAgent == null || !EnsureInventoryOpen())
            return false;

        agent->OpenForItemSlot(stack.Container, stack.Slot, 0, inventoryAgent->GetAddonId());
        return true;
    }

    /// <summary>
    /// The inventory window must be VISIBLE for item operations to land — an unattended toon
    /// never has it open. False = we asked for it to open; call again next tick.
    /// </summary>
    public static bool EnsureInventoryOpen()
    {
        var inventoryAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        if (inventoryAgent == null)
            return false;

        var inventoryAddonId = inventoryAgent->GetAddonId();
        var inventoryAddon = inventoryAddonId == 0
            ? null
            : RaptureAtkUnitManager.Instance()->GetAddonById((ushort)inventoryAddonId);

        if (inventoryAddon == null)
        {
            inventoryAgent->Show(); // never opened this session — bring the window up properly
            return false;
        }

        if (!inventoryAddon->IsVisible)
        {
            inventoryAddon->Open(1); // DailyRoutines' production call for a hidden inventory
            return false;
        }

        return true;
    }

    /// <summary>
    /// Split <paramref name="quantity"/> off a bag stack into a fresh stack — the direct
    /// <c>InventoryManager.SplitItem</c> call the FC chest seed-return already uses (verified in
    /// production IN THIS REPO; own-bag splits work silently, no InputNumeric involved). Run
    /// <see cref="EnsureInventoryOpen"/> first.
    /// </summary>
    public static int SplitStack(BagStack stack, int quantity) =>
        InventoryManager.Instance()->SplitItem(stack.Container, (ushort)stack.Slot, quantity);

    /// <summary>
    /// Click the open menu's entry whose text is the given Addon-sheet row. Call each tick after
    /// <see cref="OpenMenu"/> until it stops returning <see cref="ClickResult.NotReady"/>.
    /// The click is callback [0, entryIndex, 0, 0, 0] on the context addon (SimpleTweaks'
    /// production values); on any terminal outcome the menu is closed — a stray context menu
    /// must never be left on screen.
    /// </summary>
    public static ClickResult TryClickEntry(IDataManager dataManager, uint addonTextRow)
    {
        var wanted = dataManager.GetExcelSheet<Addon>()?.GetRowOrDefault(addonTextRow)?.Text.ExtractText();
        if (string.IsNullOrEmpty(wanted))
            return ClickResult.EntryMissing;

        var agent = AgentInventoryContext.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive())
            return ClickResult.NotReady;

        var contextAddonId = agent->AgentInterface.GetAddonId();
        var contextAddon = contextAddonId == 0
            ? null
            : RaptureAtkUnitManager.Instance()->GetAddonById((ushort)contextAddonId);
        if (contextAddon == null || !contextAddon->IsVisible)
            return ClickResult.NotReady;

        var clicked = false;
        for (var i = 0; i < agent->ContextItemCount; i++)
        {
            var param = agent->EventParams[agent->ContexItemStartIndex + i];
            if (param.Type != AtkValueType.String)
                continue;

            var text = MemoryHelper.ReadSeStringNullTerminated((nint)param.String.Value).TextValue;
            if (!string.Equals(text, wanted, StringComparison.Ordinal))
                continue;

            var values = stackalloc AtkValue[5];
            values[0].SetInt(0);
            values[1].SetInt(i);
            values[2].SetUInt(0);
            values[3].SetInt(0);
            values[4].SetInt(0);
            contextAddon->FireCallback(5, values, true);
            clicked = true;
            break;
        }

        agent->AgentInterface.Hide();
        contextAddon->Close(false);
        return clicked ? ClickResult.Clicked : ClickResult.EntryMissing;
    }
}
