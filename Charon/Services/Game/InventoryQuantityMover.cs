using System;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Charon.Services.Game;

/// <summary>
/// The game's native inventory move WITH a quantity parameter. ClientStructs' MoveItemSlot
/// wrapper doesn't expose the count, which is why Charon's FC chest tools were whole-stack-only;
/// the native itself takes one (fact learned from FCCH's integration — AGPL, so the code here is
/// our own; the signature is a fact about the game binary). Moving N of a stack this way needs
/// no split and raises no quantity prompt.
///
/// Fail-open: if the signature stops matching after a patch, <see cref="Available"/> is false
/// and every caller falls back to its whole-stack path — nothing breaks, a capability just
/// disappears until the sig is refreshed.
/// </summary>
public sealed unsafe class InventoryQuantityMover
{
    private delegate int MoveItemDelegate(InventoryManager* manager, InventoryType srcInv, ushort srcSlot,
        InventoryType dstInv, ushort dstSlot, int quantity);

    private const string MoveItemSig =
        "48 89 5C 24 10 48 89 6C 24 18 56 57 41 55 41 56 41 57 48 83 EC 30 8D BA 60 F0 FF FF 45 0F BF E8 8D 82 FC EF FF FF 41 8B E9 44 8B FA 4C 8B F1";

    private readonly MoveItemDelegate? _moveItem;

    public InventoryQuantityMover(ISigScanner sigScanner, IPluginLog log)
    {
        try
        {
            if (sigScanner.TryScanText(MoveItemSig, out var address))
            {
                _moveItem = Marshal.GetDelegateForFunctionPointer<MoveItemDelegate>(address);
                log.Info("InventoryQuantityMover: MoveItem resolved at 0x{0:X}", address);
            }
            else
            {
                log.Warning("InventoryQuantityMover: MoveItem signature not found — quantity moves unavailable, whole-stack fallbacks apply");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "InventoryQuantityMover: signature scan threw — quantity moves unavailable");
        }
    }

    public bool Available => _moveItem != null;

    /// <summary>
    /// Move <paramref name="quantity"/> units. The return code alone doesn't prove delivery —
    /// callers verify by re-reading the containers, per house doctrine.
    /// </summary>
    public bool Move(InventoryType srcInv, ushort srcSlot, InventoryType dstInv, ushort dstSlot, int quantity)
    {
        if (_moveItem == null || quantity <= 0)
            return false;

        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        _moveItem(manager, srcInv, srcSlot, dstInv, dstSlot, quantity);
        return true;
    }

    /// <summary>The game's own pending-operation ring: true while a previous move is still settling.</summary>
    public static bool HasPendingOperation()
    {
        var manager = InventoryManager.Instance();
        if (manager == null)
            return false;

        foreach (var op in manager->PendingOperations)
        {
            if (!op.IsEmpty)
                return true;
        }

        return false;
    }
}
