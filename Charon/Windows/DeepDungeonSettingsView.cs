using System;
using Charon.Services.Game;
using Dalamud.Bindings.ImGui;

namespace Charon.Windows;

/// <summary>
/// The deep dungeon switches: the floor map, the ESP overlay and its parts, the likely spots and the
/// Palace Pal import. One body, drawn by the main window's Deep Dungeon page AND by the Deep Dungeon edge
/// tab, so the two can never disagree (the <see cref="FcChestView"/> split).
/// </summary>
public static class DeepDungeonSettingsView
{
    /// <summary>
    /// Every pomander by its DeepDungeonItem row, for the exclude list. Set once by the plugin: this view is
    /// drawn from two places (the main window's page and the edge tab) and neither holds the sheet reader.
    /// </summary>
    public static Func<System.Collections.Generic.IReadOnlyList<(uint Id, string Name)>>? PomanderNames { get; set; }

    /// <param name="contentId">This character, for the one switch that is per character (0 = not logged in).</param>
    /// <summary>
    /// What each votive candelabra does (Pilgrim's Traverse) — the reference list; the floor window shows
    /// the one that is lit. Names are the English ones: this is a list to read, not a read of the game.
    /// </summary>
    private static void DrawVotiveList()
    {
        if (!ImGui.TreeNode("What the candelabra do (Pilgrim's Traverse)##ddvotives"))
            return;

        ImGui.TextColored(CharonTheme.TextDim, "Lit on one floor, it acts on the next. Through stone 30 only the helpful ones come up.");
        if (ImGui.BeginTable("##ddvotivelist", 2, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthFixed);
            ImGui.TableSetupColumn("##does", ImGuiTableColumnFlags.WidthFixed, 420f);
            foreach (var effect in Charon.Features.DeepDungeon.VotiveEffects.All)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextColored(effect.Harmful ? CharonTheme.StatusRed : CharonTheme.TextPrimary, effect.Name);
                ImGui.TableNextColumn();
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 420f);
                ImGui.TextColored(CharonTheme.TextSecondary, effect.Detail);
                ImGui.PopTextWrapPos();
            }

            ImGui.EndTable();
        }

        ImGui.TextColored(CharonTheme.TextDim, "From the Console Games Wiki; red = harmful.");
        ImGui.TreePop();
    }

    public static void Draw(CharonConfig config, Action save, PalaceSpotService palaceSpots, ulong contentId)
    {
        var autoPomander = config.AutoPomanderFor(contentId);
        if (contentId == 0)
            ImGui.BeginDisabled();
        if (ImGui.Checkbox("Use a pomander when the pouch is full of it (this character)##ddpom", ref autoPomander) && contentId != 0)
        {
            config.AutoPomanderByCharacter[contentId] = autoPomander;
            save();
        }
        if (contentId == 0)
            ImGui.EndDisabled();
        CharonTheme.HelpMarker("When the party holds three of one pomander, one toon uses one so the\n"
                               + "next to drop is not turned away.\n\n"
                               + "Steel goes tank, then healer, then damage; strength goes damage, then\n"
                               + "healer, then tank. A toon whose buff is still running is passed over,\n"
                               + "unless it has a minute or less left.\n\n"
                               + "Every other kind is used by one toon (a damage dealer if there is one).\n"
                               + "Witching and the transformations wait for a fight; a kind already in\n"
                               + "effect is not used again.\n\n"
                               + "Each toon decides for itself, so switch this on for every character\n"
                               + "that should take its turn.");

        if (autoPomander && PomanderNames != null && ImGui.TreeNode("Kinds it may use##ddpomkinds"))
        {
            ImGui.TextColored(CharonTheme.TextDisabled, "Untick a kind to leave it alone (all characters on this client).");
            if (ImGui.BeginTable("##ddpomtable", 2, ImGuiTableFlags.SizingFixedFit))
            {
                foreach (var (id, name) in PomanderNames())
                {
                    ImGui.TableNextColumn();
                    var allowed = !config.AutoPomanderSkipItems.Contains(id);
                    if (ImGui.Checkbox($"{name}##ddpom{id}", ref allowed))
                    {
                        if (allowed)
                            config.AutoPomanderSkipItems.Remove(id);
                        else
                            config.AutoPomanderSkipItems.Add(id);
                        save();
                    }
                }

                ImGui.EndTable();
            }

            ImGui.TreePop();
        }

        var avoidLikely = config.AvoidLikelyTrapSpots;
        if (ImGui.Checkbox("Walk around likely trap spots##ddavoid", ref avoidLikely))
        {
            config.AvoidLikelyTrapSpots = avoidLikely;
            save();
        }

        CharonTheme.HelpMarker(
            "Routes already go round a trap that is revealed. With this on, routes also keep off the spots where a trap has stood before on this floor set - "
            + "where there is another way; a corridor with a spot in it is still walked. Needs Ariadne as the movement provider.");

        var announce = config.AnnounceAetherpoolIncreases;
        if (ImGui.Checkbox("Say in chat when another toon's aetherpool goes up##ddannounce", ref announce))
        {
            config.AnnounceAetherpoolIncreases = announce;
            save();
        }

        CharonTheme.HelpMarker(
            "The game only tells each toon about its own arm and armor. With this on, when another fleet toon's goes up "
            + "a line appears in this client's chat (only you see it). Needs the other toon on this build with Daedalus's LAN relay.");

        DrawVotiveList();

        var ddMap = config.DeepDungeonMapEnabled;
        if (ImGui.Checkbox("Deep dungeon floor map##qol", ref ddMap))
        {
            config.DeepDungeonMapEnabled = ddMap;
            save();
        }
        CharonTheme.HelpMarker("A floor map window shown only inside deep dungeons: the full 5x5\n"
                               + "room layout with connections, passage, return, chests and party\n"
                               + "positions — including rooms the game hasn't revealed yet (dim).");

        var ddEsp = config.DeepDungeonEspEnabled;
        if (ImGui.Checkbox("Deep dungeon ESP overlay##qol", ref ddEsp))
        {
            config.DeepDungeonEspEnabled = ddEsp;
            save();
        }
        CharonTheme.HelpMarker("Draws chests, passage, return, revealed traps and mob aggro ranges\n"
                               + "over the world while in a deep dungeon. Aggro shapes follow how each\n"
                               + "mob notices you: circle = proximity, circle+core = sound, cone =\n"
                               + "sight (NecroLens's dataset, MIT). Patrols get a facing arrow.");
        if (ddEsp)
        {
            ImGui.Indent();
            var espMobs = config.DeepDungeonEspMobs;
            if (ImGui.Checkbox("Mob aggro ranges##ddesp", ref espMobs))
            {
                config.DeepDungeonEspMobs = espMobs;
                save();
            }

            var espNames = config.DeepDungeonEspMobNames;
            if (ImGui.Checkbox("Mob names##ddesp", ref espNames))
            {
                config.DeepDungeonEspMobNames = espNames;
                save();
            }

            var espChests = config.DeepDungeonEspChests;
            if (ImGui.Checkbox("Chests and floor objects##ddesp", ref espChests))
            {
                config.DeepDungeonEspChests = espChests;
                save();
            }
            CharonTheme.HelpMarker("Coffers, passage and return within 35 yalms; hoards at any distance.");

            var espHoardLine = config.DeepDungeonEspHoardLine;
            if (ImGui.Checkbox("Line to the hoard##ddesp", ref espHoardLine))
            {
                config.DeepDungeonEspHoardLine = espHoardLine;
                save();
            }
            CharonTheme.HelpMarker("Once an accursed hoard is in the world (an Intuition pomander\n"
                                   + "reveals it), a line runs from you to it — at any distance.");

            var espTraps = config.DeepDungeonEspTraps;
            if (ImGui.Checkbox("Traps##ddesp", ref espTraps))
            {
                config.DeepDungeonEspTraps = espTraps;
                save();
            }
            CharonTheme.HelpMarker("Revealed traps, at any distance — one you can see down a\n"
                                   + "corridor is exactly the one worth routing around.\n\n"
                                   + "Unrevealed traps are SERVER-SIDE: until a Pomander of Sight\n"
                                   + "reveals them they do not exist in the world at all, so no\n"
                                   + "plugin can draw them (NecroLens can't either). Without Sight\n"
                                   + "the first you know of a landmine is the chat line.");

            ImGui.Spacing();
            ImGui.TextColored(CharonTheme.TextSecondary, "Likely spots");
            CharonTheme.HelpMarker("Every place a trap or hoard has been seen on this floor set —\n"
                                   + "imported from Palace Pal, plus every one the fleet sees from\n"
                                   + "now on, drawn as outlines: traps within 40 yalms, hoards anywhere on\n"
                                   + "the floor — only in rooms this floor actually has. Hidden once the\n"
                                   + "floor is settled: Sight or Safety for traps, Intuition for the hoard.");

            var likelyTraps = config.DeepDungeonEspLikelyTraps;
            if (ImGui.Checkbox("Likely trap spots##ddesp", ref likelyTraps))
            {
                config.DeepDungeonEspLikelyTraps = likelyTraps;
                save();
            }

            ImGui.SameLine();
            var likelyHoards = config.DeepDungeonEspLikelyHoards;
            if (ImGui.Checkbox("Likely hoard spots##ddesp", ref likelyHoards))
            {
                config.DeepDungeonEspLikelyHoards = likelyHoards;
                save();
            }

            ImGui.TextColored(CharonTheme.TextDisabled, $"{palaceSpots.KnownSpots:N0} spots known on this machine");
            if (palaceSpots.PalacePalFileExists)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Import from Palace Pal##ddesp"))
                    palaceSpots.ImportFromPalacePal();
                CharonTheme.HelpMarker("Reads Palace Pal's saved spots (read-only). Safe to press again —\n"
                                       + "spots already known are not added twice.");
            }

            if (palaceSpots.ImportStatus.Length > 0)
                ImGui.TextColored(CharonTheme.TextDisabled, palaceSpots.ImportStatus);
            ImGui.Unindent();
        }
    }
}
