using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Facts about the FreeCompanyChest addon, shared by the search filter and the transfer
/// pipeline: the five tab radio buttons live at NodeList indices 101 (tab 1) down to 97
/// (tab 5); the checked one is detected by its checkmark child (component node 2) being
/// visible; a tab switch is the addon's own callback (Int 0, UInt page index 0-4), see SwitchToPage.
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

    /// <summary>
    /// Switch the chest to item page 1-5, sent exactly as the game's own tab click sends it: two values,
    /// (Int 0, UInt tab index 0-4), with the close flag SET. Taken from a real click (Daedalus crucible
    /// recording 2026-11-05 16:52:00: <c>[0=i0 | 1=u3] (closing)</c>), whose index is 0-based: value 2 of the
    /// window then read 3, and the page read straight after was page 4 (Charon's chest reader and Plutus
    /// both logged page 4, and the deposit that followed landed there). Before that this sent Int + Int with
    /// the flag clear (FCCH's shape), never seen to work here.
    /// </summary>
    public static void SwitchToPage(AtkUnitBase* unit, int page)
    {
        var values = stackalloc AtkValue[2];
        values[0].SetInt(0);
        values[1].SetUInt((uint)(page - 1));
        unit->FireCallback(2, values, true);
    }

    /// <summary>
    /// The window's own value 2, the displayed item tab 0-4 (0 at every opening, 3 after switching to
    /// page 4 in the recording), or -1 when it is not a UInt. Logged beside <see cref="CurrentPage"/> as
    /// evidence; not relied on, since what it says on the crystal and gil tabs has never been seen.
    /// </summary>
    public static int TabValue(AtkUnitBase* unit)
    {
        if (unit->AtkValuesCount <= 2 || unit->AtkValues[2].Type != AtkValueType.UInt)
            return -1;

        return (int)unit->AtkValues[2].UInt;
    }
}
