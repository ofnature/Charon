// Ported from Character Panel Refined by Kouzukii - https://github.com/Kouzukii/ffxiv-characterstatus-refined
// (commit 0e4b68c, v1.8.6.0). MIT License, Copyright (c) 2022 Kouzukii - the full notice is in
// Charon/Features/CharacterPanel/LICENSE-CharacterPanelRefined.txt.
// Kept close to the original, formatting included, so a later upstream change can be diffed in.
// Changed for Charon: the game reads were moved out to Services/Game/CharacterPanel/GameReads.cs so this
// file is arithmetic only and can be tested without the game.
using System;

namespace Charon.Features.CharacterPanel;

public class Equations {
    public static double CalcDh(int dh, ref StatInfo statInfo, in LevelModifier lvlModifier) {
        statInfo.CurrentValue = dh;
        var cVal = statInfo.DisplayValue = Math.Floor(550d * (dh - lvlModifier.Sub) / lvlModifier.Div) / 1000d;
        statInfo.PrevTier = (int)Math.Ceiling(cVal * 1000d * lvlModifier.Div / 550d + lvlModifier.Sub);
        statInfo.NextTier = (int)Math.Ceiling((cVal + 0.001) * 1000d * lvlModifier.Div / 550d + lvlModifier.Sub);
        statInfo.PointsPerTier = lvlModifier.Div / 550d;
        return cVal;
    }

    public static double CalcDet(int det, ref StatInfo statInfo, in LevelModifier lvlModifier) {
        statInfo.CurrentValue = det;
        var cVal = statInfo.DisplayValue = Math.Floor(140d * (det - lvlModifier.Main) / lvlModifier.Div) / 1000d;
        statInfo.PrevTier = (int)Math.Ceiling(cVal * 1000d * lvlModifier.Div / 140d + lvlModifier.Main);
        statInfo.NextTier = (int)Math.Ceiling((cVal + 0.001) * 1000d * lvlModifier.Div / 140d + lvlModifier.Main);
        statInfo.PointsPerTier = lvlModifier.Div / 140d;
        return cVal;
    }

    public static double CalcCritRate(int crit, ref StatInfo statInfo, in LevelModifier lvlModifier) {
        statInfo.CurrentValue = crit;
        var cVal = statInfo.DisplayValue = Math.Floor(200d * (crit - lvlModifier.Sub) / lvlModifier.Div + 50) / 1000d;
        statInfo.PrevTier = (int)Math.Ceiling((cVal * 1000d - 50.0000001) * lvlModifier.Div / 200d + lvlModifier.Sub);
        statInfo.NextTier = (int)Math.Ceiling(((cVal + 0.001) * 1000d - 50.0000001) * lvlModifier.Div / 200d + lvlModifier.Sub);
        statInfo.PointsPerTier = lvlModifier.Div / 200d;
        return cVal;
    }

    public static double CalcCritDmg(int crit, ref StatInfo statInfo, in LevelModifier lvlModifier) {
        statInfo.CurrentValue = crit;
        var cVal = statInfo.DisplayValue = Math.Floor(200d * (crit - lvlModifier.Sub) / lvlModifier.Div + 1400) / 1000d;
        statInfo.PrevTier = (int)Math.Ceiling((cVal * 1000d - 1400.0000001) * lvlModifier.Div / 200d + lvlModifier.Sub);
        statInfo.NextTier = (int)Math.Ceiling(((cVal + 0.001) * 1000d - 1400.0000001) * lvlModifier.Div / 200d + lvlModifier.Sub);
        statInfo.PointsPerTier = lvlModifier.Div / 200d;
        return cVal;
    }

    public static void CalcDef(int def, ref StatInfo statInfo, in LevelModifier lvlModifier) {
        statInfo.CurrentValue = def;
        var cVal = statInfo.DisplayValue = Math.Floor(15d * def / lvlModifier.Div) / 100d;
        statInfo.PrevTier = (int)Math.Ceiling(cVal * 100d * lvlModifier.Div / 15d);
        statInfo.NextTier = (int)Math.Ceiling((cVal + 0.01) * 100d * lvlModifier.Div / 15d);
        statInfo.PointsPerTier = lvlModifier.Div / 15d;
    }

    public static void CalcMagicDef(int def, ref StatInfo statInfo, in LevelModifier lvlModifier) {
        statInfo.CurrentValue = def;
        var cVal = statInfo.DisplayValue = Math.Floor(15d * def / lvlModifier.Div) / 100d;
        statInfo.PrevTier = (int)Math.Ceiling(cVal * 100d * lvlModifier.Div / 15d);
        statInfo.NextTier = (int)Math.Ceiling((cVal + 0.01) * 100d * lvlModifier.Div / 15d);
        statInfo.PointsPerTier = lvlModifier.Div / 15d;
    }

    public static void CalcTenacityMit(int ten, ref StatInfo statInfo, in LevelModifier lvlModifier) {
        statInfo.CurrentValue = ten;
        var cVal = statInfo.DisplayValue = Math.Floor(200d * (ten - lvlModifier.Sub) / lvlModifier.Div) / 1000d;
        statInfo.PrevTier = (int)Math.Ceiling(cVal * 1000d * lvlModifier.Div / 200d + lvlModifier.Sub);
        statInfo.NextTier = (int)Math.Ceiling((cVal + 0.001) * 1000d * lvlModifier.Div / 200d + lvlModifier.Sub);
        statInfo.PointsPerTier = lvlModifier.Div / 200d;
    }

    public static double CalcTenacityDmg(int ten, ref StatInfo statInfo, in LevelModifier lvlModifier) {
        statInfo.CurrentValue = ten;
        var cVal = statInfo.DisplayValue = Math.Floor(112d * (ten - lvlModifier.Sub) / lvlModifier.Div) / 1000d;
        statInfo.PrevTier = (int)Math.Ceiling(cVal * 1000d * lvlModifier.Div / 112d + lvlModifier.Sub);
        statInfo.NextTier = (int)Math.Ceiling((cVal + 0.001) * 1000d * lvlModifier.Div / 112d + lvlModifier.Sub);
        statInfo.PointsPerTier = lvlModifier.Div / 112d;
        return cVal;
    }

    public static void CalcPiety(int pie, ref StatInfo statInfo, in LevelModifier lvlModifier) {
        statInfo.CurrentValue = pie;
        var cVal = Math.Floor(150d * (pie - lvlModifier.Main) / lvlModifier.Div);
        statInfo.DisplayValue = cVal + 200;
        statInfo.PrevTier = (int)Math.Ceiling(cVal * lvlModifier.Div / 150d + lvlModifier.Main);
        statInfo.NextTier = (int)Math.Ceiling((cVal + 1) * lvlModifier.Div / 150d + lvlModifier.Main);
        statInfo.PointsPerTier = lvlModifier.Div / 150d;
    }

    public static void CalcSpeed(int speed, ref StatInfo statInfo, ref StatInfo gcdMain, ref StatInfo gcdAlt, in LevelModifier lvlModifier, in AlternateGcd? altGcd, in GcdModifier? mod, out int baseModGcd, out int? altBaseModGcd) {
        var (_, sub, div) = lvlModifier;
        var modifier = mod?.Mod ?? 0;

        int SpeedCalc(double gcd, double spd) =>
            (int)Math.Floor(Math.Floor((1000.0 + Math.Ceiling(130.0 * (sub - spd) / div)) * gcd / 100d) * (100d - modifier) / 1000d);

        int TierCalc(double curVal, int gcd) =>
            -(int)Math.Floor(Math.Floor(Math.Ceiling(curVal * 100d * 1000d / (100d - modifier) - 0.01d) * 100 / gcd - 1000.01d) * div / 130d - sub);

        statInfo.CurrentValue = speed;
        var cVal = statInfo.DisplayValue = Math.Floor(130d * (speed - sub) / div) / 1000d;
        statInfo.PrevTier = (int)Math.Ceiling(cVal * 1000d * div / 130d + sub);
        statInfo.NextTier = (int)Math.Ceiling((cVal + 0.001) * 1000d * div / 130d + sub);
        statInfo.PointsPerTier = div / 130d;
        gcdMain.CurrentValue = speed;
        cVal = gcdMain.DisplayValue = SpeedCalc(250, speed) / 100d;
        baseModGcd = modifier != 0 ? SpeedCalc(250, sub) : 250;
        gcdMain.PrevTier = cVal < baseModGcd / 100d ? TierCalc(cVal + 0.01, 250) : sub;
        gcdMain.NextTier = TierCalc(cVal, 250);
        gcdMain.PointsPerTier = div / 130d * 1000d / baseModGcd;
        if (altGcd != null) {
            gcdAlt.CurrentValue = speed;
            cVal = gcdAlt.DisplayValue = SpeedCalc(altGcd.Gcd, speed) / 100d;
            altBaseModGcd = modifier != 0 ? SpeedCalc(altGcd.Gcd, sub) : altGcd.Gcd;
            gcdAlt.PrevTier = cVal < altBaseModGcd.Value / 100d ? TierCalc(cVal + 0.01, altGcd.Gcd) : sub;
            gcdAlt.NextTier = TierCalc(cVal, altGcd.Gcd);
            gcdAlt.PointsPerTier = div / 130d * 1000d / altBaseModGcd.Value;
        } else {
            altBaseModGcd = null;
        }
    }

    // Charon: the game reads this needs (attack power, weapon damage, item level sync) are done in
    // Services/Game/CharacterPanel/GameReads.cs; this is the arithmetic alone.
    public static (double AvgDamage, double NormalDamage, double CritDamage, double AvgHeal, double NormalHeal, double CritHeal) CalcExpectedOutput(int lvl, int ap, int weaponBaseDamage, JobId jobId, double det, double critMult, double critRate, double dh, double ten, in LevelModifier lvlModifier) {
        var weaponDamage = Math.Floor(weaponBaseDamage + lvlModifier.Main * jobId.AttackModifier() / 1000.0) / 100.0;
        var lvlAttackModifier = jobId.UsesTenacity() ? LevelModifiers.TankAttackModifier(lvl) : LevelModifiers.AttackModifier(lvl);
        var atk = Math.Floor(100 + lvlAttackModifier * (ap - lvlModifier.Main) / lvlModifier.Main) / 100;
        var baseMultiplier = Math.Floor(100 * atk * weaponDamage);
        var withDet = Math.Floor(baseMultiplier * (1 + det));
        var withTen = Math.Floor(withDet * (1 + ten));
        var normalDamage = Math.Floor(withTen * jobId.TraitModifiers(lvl));
        var avgDamage = Math.Floor(Math.Floor(normalDamage * (1 + (critMult - 1) * critRate)) * (1 + dh * 0.25));
        var critDamage = Math.Floor(normalDamage * critMult);

        var healPot = Math.Floor(100 + LevelModifiers.HealModifier(lvl) * (ap - lvlModifier.Main) / lvlModifier.Main) / 100;
        var healBaseMultiplier = Math.Floor(100 * healPot * weaponDamage);
        var healWithDet = Math.Floor(healBaseMultiplier * (1 + det));
        var healWithTen = Math.Floor(healWithDet * (1 + ten));
        var normalHeal = Math.Floor(healWithTen * (jobId.IsCaster() ? jobId.TraitModifiers(lvl) : 1));
        var avgHeal = Math.Floor(normalHeal * (1 + (critMult - 1) * critRate));
        var critHeal = Math.Floor(normalHeal * critMult);
        return (avgDamage, normalDamage, critDamage, avgHeal, normalHeal, critHeal);
    }

    public static void CalcHp(int lvl, JobId jobId, out double hpPerVitality, out double hpModifier) {
        hpPerVitality = jobId.UsesTenacity() ? LevelModifiers.TankHpModifier(lvl) : LevelModifiers.HpModifier(lvl);
        hpModifier = jobId.HpModifier() / 100d;
    }

    // Charon: one food bonus taken back off a stat (the arithmetic of the original's EstimateBaseStats).
    public static int WithoutFoodBonus(int currentStat, int value, bool isRelative, int max) {
        if (!isRelative)
            return currentStat - value;
        var sub = Math.Min(max, Math.Floor(currentStat - currentStat / (1 + value * 0.01)));
        return (int)(currentStat - sub);
    }
}
