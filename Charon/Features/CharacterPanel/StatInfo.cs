// Ported from Character Panel Refined by Kouzukii - https://github.com/Kouzukii/ffxiv-characterstatus-refined
// (commit 0e4b68c, v1.8.6.0). MIT License, Copyright (c) 2022 Kouzukii - the full notice is in
// Charon/Features/CharacterPanel/LICENSE-CharacterPanelRefined.txt.
// Kept close to the original, formatting included, so a later upstream change can be diffed in.
namespace Charon.Features.CharacterPanel;

public struct StatInfo {
    public double DisplayValue { get; set; }
    public int CurrentValue { get; set; }
    public int PrevTier { get; set; }
    public int NextTier { get; set; }
    public double PointsPerTier { get; set; }
}
