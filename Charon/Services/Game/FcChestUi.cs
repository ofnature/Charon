using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Facts about the FreeCompanyChest addon, shared by the search filter and the transfer
/// pipeline: the five tab radio buttons live at NodeList indices 101 (tab 1) down to 97
/// (tab 5); the checked one is detected by its checkmark child (component node 2) being
/// visible; a tab switch is the addon's own callback with two ints (0, page index 0-4).
/// </summary>
public static unsafe class FcChestUi
{
    public const string AddonName = "FreeCompanyChest";

    /// <summary>NodeList index of the tab-1 radio button; tabs run DOWN from here.</summary>
    public const int Tab1NodeIndex = 101;

    /// <summary>Which item page (1-5) the chest window is showing; -1 = crystals/gil/unknown.</summary>
    public static int CurrentPage(AtkUnitBase* unit)
    {
        for (var tab = 0; tab < 5; tab++)
        {
            var index = Tab1NodeIndex - tab;
            if (index >= unit->UldManager.NodeListCount)
                continue;

            var node = unit->UldManager.NodeList[index];
            if (node == null || !node->IsVisible())
                continue;

            var component = node->GetAsAtkComponentNode();
            if (component == null || component->Component == null)
                continue;

            var inner = component->Component->UldManager;
            if (inner.NodeListCount > 2 && inner.NodeList[2] != null && inner.NodeList[2]->IsVisible())
                return tab + 1;
        }

        return -1;
    }

    /// <summary>Simulate clicking the tab for page 1-5 (updateState false — the production shape).</summary>
    public static void SwitchToPage(AtkUnitBase* unit, int page)
    {
        var values = stackalloc AtkValue[2];
        values[0].SetInt(0);
        values[1].SetInt(page - 1);
        unit->FireCallback(2, values, false);
    }
}
