using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Clicks addon buttons and checkboxes the way ECommons' ClickHelper does in production: build a
/// FRESH zeroed <c>AtkEvent</c> carrying only Target (the component's node) and Listener (the
/// addon), plus a zeroed <c>AtkEventData</c>, and hand both to the addon's ReceiveEvent with the
/// type + param read from the node's own registered event (recorder-verified: the Donate button
/// is ButtonClick param 0).
///
/// TWO crash traps, learned the hard way (a live crash inside
/// AddonReconstructionBox.ReceiveEvent+0x247): do NOT replay the node's own live AtkEvent object,
/// and do NOT omit the AtkEventData — the handler dereferences both. ECommons'
/// EventData.ForNormalTarget + InputData.Empty is exactly this fresh-and-zeroed shape.
/// </summary>
public static unsafe class AtkClickHelper
{
    /// <summary>Click a button — a real click as far as the addon knows.</summary>
    public static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null)
            return false;

        return Click(addon, button->AtkComponentBase.OwnerNode);
    }

    /// <summary>Same for a checkbox (the SelectYesno Confirm box), then mark it checked.</summary>
    public static bool ClickCheckBox(AtkUnitBase* addon, AtkComponentCheckBox* checkbox)
    {
        if (addon == null || checkbox == null)
            return false;

        if (!Click(addon, checkbox->AtkComponentButton.AtkComponentBase.OwnerNode))
            return false;

        checkbox->IsChecked = true;
        return true;
    }

    /// <summary>
    /// ECommons' SelectYesno.Yes() move: a Yes button still disabled (the Confirm checkbox gates
    /// it in the UI only) is force-enabled by flipping NodeFlags bit 5 before clicking.
    /// </summary>
    public static void ForceEnable(AtkComponentButton* button)
    {
        if (button == null || button->IsEnabled)
            return;

        var node = button->AtkComponentBase.OwnerNode;
        if (node == null)
            return;

        var flags = (ushort*)&node->AtkResNode.NodeFlags;
        *flags ^= 1 << 5;
    }

    /// <summary>
    /// The Talk subtitle box advance — ECommons AddonMaster.Talk.Click verbatim: a fresh
    /// AtkEvent (listener = the addon, target = AtkStage's event target, state flags 132) with
    /// zeroed AtkEventData, delivered as MouseDown, MouseClick, MouseUp.
    /// </summary>
    public static void AdvanceTalk(AtkUnitBase* addon)
    {
        if (addon == null)
            return;

        var evt = default(AtkEvent);
        evt.Listener = (AtkEventListener*)addon;
        evt.Target = &AtkStage.Instance()->AtkEventTarget;
        evt.State.StateFlags = (AtkEventStateFlags)132;
        var data = default(AtkEventData);

        addon->ReceiveEvent(AtkEventType.MouseDown, 0, &evt, &data);
        addon->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
        addon->ReceiveEvent(AtkEventType.MouseUp, 0, &evt, &data);
    }

    private static bool Click(AtkUnitBase* addon, AtkComponentNode* node)
    {
        if (node == null)
            return false;

        // The node's registered event supplies the true type + param; everything else is fresh.
        var registered = node->AtkResNode.AtkEventManager.Event;
        if (registered == null)
            return false;

        var evt = default(AtkEvent);
        evt.Target = (AtkEventTarget*)node;
        evt.Listener = (AtkEventListener*)addon;
        var eventData = default(AtkEventData);

        addon->ReceiveEvent(registered->State.EventType, (int)registered->Param, &evt, &eventData);
        return true;
    }
}
