// Ported from Character Panel Refined by Kouzukii - https://github.com/Kouzukii/ffxiv-characterstatus-refined
// (commit 0e4b68c, v1.8.6.0). MIT License, Copyright (c) 2022 Kouzukii - the full notice is in
// Charon/Features/CharacterPanel/LICENSE-CharacterPanelRefined.txt.
// Kept close to the original, formatting included, so a later upstream change can be diffed in.
using System;
using System.Runtime.InteropServices;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game.CharacterPanel;

internal static class Util {
    public static unsafe T* CloneNode<T>(T* original) where T : unmanaged {
        var size = sizeof(T);
        var allocation = MemoryHelper.GameAllocateUi((ulong)size);
        var bytes = new byte[size];
        Marshal.Copy(new IntPtr(original), bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, allocation, bytes.Length);

        var newNode = (AtkResNode*)allocation;
        newNode->ChildNode = null;
        newNode->ChildCount = 0;
        newNode->PrevSiblingNode = null;
        newNode->NextSiblingNode = null;
        return (T*)newNode;
    }
}
