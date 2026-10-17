using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Variant;

/// <summary>Which way the party goes at the dungeon's first fork.</summary>
public enum VariantPath
{
    Left,
    Middle,
    Right,
}

/// <summary>What a route marker hangs on: a live object found by id, or a fixed spot in the world.</summary>
public enum MarkerKind
{
    /// <summary>An EventObj, matched by its base id (the EObjName row — the same on every client).</summary>
    EventObj,

    /// <summary>A character, matched by its BNpcName id.</summary>
    Npc,

    /// <summary>A fixed world position (X, Y = height, Z).</summary>
    Spot,
}

/// <summary>One thing worth pointing at for the routes listed in <see cref="Routes"/>.</summary>
public sealed record RouteMarker(MarkerKind Kind, uint Id, float X, float Y, float Z, int[] Routes, string Label)
{
    public static RouteMarker Object(uint baseId, string label, params int[] routes) =>
        new(MarkerKind.EventObj, baseId, 0, 0, 0, routes, label);

    public static RouteMarker Npc(uint nameId, string label, params int[] routes) =>
        new(MarkerKind.Npc, nameId, 0, 0, 0, routes, label);

    public static RouteMarker Spot(float x, float y, float z, string label, params int[] routes) =>
        new(MarkerKind.Spot, 0, x, y, z, routes, label);
}

/// <summary>One record in the dungeon's notebook, and how to earn it.</summary>
/// <param name="Number">The route's number in the game's own notebook (1-12, 13 for the Merchant's Tale secret).</param>
/// <param name="Boss">The second boss this route ends at.</param>
/// <param name="Steps">What to do, in order, after the opening fork.</param>
public sealed record VariantRoute(int Number, VariantPath Path, bool Secret, string Boss, string[] Steps);

/// <summary>A variant dungeon: its notebook series, where it is, and its routes.</summary>
/// <param name="Series">The VVDNotebookSeries row (1-4).</param>
/// <param name="FirstNotebookRow">The VVDNotebookContents row of route 1; route n is this + n - 1.</param>
/// <param name="Forks">What to pick at the opening fork for each path.</param>
public sealed record VariantDungeon(
    int Series,
    string Name,
    uint TerritoryId,
    uint FirstNotebookRow,
    IReadOnlyDictionary<VariantPath, string> Forks,
    IReadOnlyList<VariantRoute> Routes,
    IReadOnlyList<RouteMarker> Markers)
{
    public uint NotebookRow(int route) => FirstNotebookRow + (uint)route - 1;

    public VariantRoute? Route(int number) => Routes.FirstOrDefault(r => r.Number == number);

    /// <summary>The markers that matter for one route.</summary>
    public IEnumerable<RouteMarker> MarkersFor(int route) => Markers.Where(m => m.Routes.Contains(route));
}

/// <summary>
/// The four variant dungeons and every route through them. Pure data — no Dalamud types.
///
/// SOURCES, each checked rather than remembered:
///  - Series, notebook rows and territories are the GAME'S: VVDNotebookSeries lists rows 1-12 / 13-24 /
///    25-36 / 37-49 (the Merchant's Tale has a 13th), VVDData names the duties' ContentFinderCondition rows
///    868 / 945 / 961 / 1066, and those resolve to territories 1069 / 1137 / 1176 / 1315 (read with Lumina
///    from the game files). Record TITLES are read from VVDNotebookContents at runtime, not kept here.
///  - The route steps are the community's solutions (the FFXIV Console Games Wiki objective tables), cross-
///    checked against the route markers in the user's own Splatoon layouts, and REWRITTEN here in short form.
///  - Marker object ids are EObjName rows and NPC ids BNpcName rows, every one looked up in the game files
///    by name ("myrrh incense burner" 2013135, "Zeal-blind Zozone" 12531 …). Fixed spots are the positions
///    from the user's Splatoon route layouts (Aloalo Island and the Merchant's Tale — Splatoon has none for
///    the other two), converted from Splatoon's (x, z-up) order to the game's (x, height, z).
/// </summary>
public static class VariantRoutes
{
    public static readonly VariantDungeon Sildihn = new(
        Series: 1,
        Name: "The Sil'dihn Subterrane",
        TerritoryId: 1069,
        FirstNotebookRow: 1,
        Forks: new Dictionary<VariantPath, string>
        {
            [VariantPath.Left] = "Take the LEFT door at the start.",
            [VariantPath.Middle] = "Take the MIDDLE door at the start.",
            [VariantPath.Right] = "Take the RIGHT door at the start.",
        },
        Routes:
        [
            new(1, VariantPath.Left, false, "Silkie",
            [
                "Solve the two-eyes puzzle: the blue sack right of the north door, and the yellow sack in the far south-east corner.",
                "Past the teleporter, clear the enemies, then open the door hidden behind the rubble BEFORE Nanamo opens the gate.",
            ]),
            new(2, VariantPath.Left, false, "Silkie",
            [
                "Solve the two-eyes puzzle: the blue sack right of the north door, and the yellow sack in the far south-east corner.",
                "Let Nanamo open the gate — do not touch the rubble door.",
            ]),
            new(3, VariantPath.Left, false, "Silkie",
            [
                "Fail the two-eyes puzzle.",
                "In the next room, hit every cotton spore while fighting the enemies.",
            ]),
            new(4, VariantPath.Left, false, "Silkie",
            [
                "Fail the two-eyes puzzle.",
                "Avoid every cotton spore and carry on normally.",
            ]),
            new(5, VariantPath.Right, false, "Shadowcaster Zeless Gah",
            [
                "After the first boss take the LEFT lift.",
                "At the blocked door, answer \"The blessed spark\".",
            ]),
            new(6, VariantPath.Right, false, "Shadowcaster Zeless Gah",
            [
                "After the first boss take the LEFT lift.",
                "At the blocked door, answer \"The sacred fire\".",
            ]),
            new(7, VariantPath.Right, false, "Shadowcaster Zeless Gah",
            [
                "After the first boss take the RIGHT lift.",
                "Kill the drakes largest to smallest: father, mother, brother, sister, drakeling.",
                "After the wraiths, use the myrrh incense burner on the right side of the room.",
                "Interact with the sarcophagus and fight.",
            ]),
            new(8, VariantPath.Middle, false, "Gladiator of Sil'dih",
            [
                "Fail the scales puzzle.",
                "Pull the RIGHT lever.",
            ]),
            new(9, VariantPath.Middle, false, "Gladiator of Sil'dih",
            [
                "Fail the scales puzzle.",
                "Pull the LEFT lever.",
            ]),
            new(10, VariantPath.Middle, false, "Gladiator of Sil'dih",
            [
                "Solve the scales: the helm on the LEFT pan, the fruit on the RIGHT.",
                "In the next chamber, interact with the statue of NALD.",
            ]),
            new(11, VariantPath.Middle, false, "Gladiator of Sil'dih",
            [
                "Solve the scales: the helm on the LEFT pan, the fruit on the RIGHT.",
                "In the next chamber, interact with the statue of THAL.",
            ]),
            new(12, VariantPath.Right, true, "Thorne Knight",
            [
                "After the first boss take the RIGHT lift.",
                "Kill the drakes largest to smallest: father, mother, brother, sister, drakeling.",
                "After the wraiths, use the myrrh incense burner on the right side of the room.",
                "At the sarcophagus, target it and DON'T interact: /bow, /respect, /vpose, /kneel — in that order.",
            ]),
        ],
        Markers:
        [
            RouteMarker.Object(2013140, "scales: helm LEFT, fruit RIGHT", 10, 11),
            RouteMarker.Object(2013140, "scales: get it wrong", 8, 9),
            RouteMarker.Object(2013143, "pull the RIGHT lever", 8),
            RouteMarker.Object(2013143, "pull the LEFT lever", 9),
            RouteMarker.Object(2013145, "interact: statue of Nald", 10),
            RouteMarker.Object(2013146, "interact: statue of Thal", 11),
            RouteMarker.Object(2013135, "use the incense burner", 7, 12),
            RouteMarker.Object(2013136, "interact", 7),
            RouteMarker.Object(2013136, "target: /bow /respect /vpose /kneel", 12),
        ]);

    public static readonly VariantDungeon Rokkon = new(
        Series: 2,
        Name: "Mount Rokkon",
        TerritoryId: 1137,
        FirstNotebookRow: 13,
        Forks: new Dictionary<VariantPath, string>
        {
            [VariantPath.Left] = "Take the LEFT path at the start.",
            [VariantPath.Middle] = "Take the MIDDLE path at the start.",
            [VariantPath.Right] = "Take the RIGHT path at the start.",
        },
        Routes:
        [
            new(1, VariantPath.Left, false, "Moko the Restless",
            [
                "In the crystal-ball room, use the orb WITHOUT touching the statues.",
                "In the next room, close the katana case after Hancock inspects it.",
            ]),
            new(2, VariantPath.Left, false, "Moko the Restless",
            [
                "In the crystal-ball room, use the orb WITHOUT touching the statues.",
                "In the next room, leave the katana case alone.",
            ]),
            new(3, VariantPath.Left, false, "Moko the Restless",
            [
                "Turn the Seiryu statue to face EAST (toward Suzaku) and the Suzaku statue to face SOUTH (the big doors), then use the orb.",
                "At the last pack in the Upper Pagoda, LET the Apa finish casting Water III.",
            ]),
            new(4, VariantPath.Left, false, "Moko the Restless",
            [
                "Turn the Seiryu statue to face EAST (toward Suzaku) and the Suzaku statue to face SOUTH (the big doors), then use the orb.",
                "At the last pack in the Upper Pagoda, kill or interrupt the Apa before Water III goes off.",
            ]),
            new(5, VariantPath.Middle, false, "Gorai the Uncaged",
            [
                "Before the second boss, clear the enemies and answer \"Deal\" about sparing the baboon.",
            ]),
            new(6, VariantPath.Middle, false, "Gorai the Uncaged",
            [
                "Before the second boss, clear the enemies and answer \"Think about it\" about sparing the baboon.",
            ]),
            new(7, VariantPath.Middle, false, "Gorai the Uncaged",
            [
                "After the tiger, climb the pile of crates and pull the rope.",
                "Wait for Hancock upstairs, then kill the lantern enemies normally.",
            ]),
            new(8, VariantPath.Right, false, "Shishio",
            [
                "Ignore the Rokkon sentinel.",
                "After the first boss, interact with all FOUR stones of protection along the path.",
            ]),
            new(9, VariantPath.Right, false, "Shishio",
            [
                "Ignore the Rokkon sentinel AND the stones of protection.",
            ]),
            new(10, VariantPath.Right, false, "Shishio",
            [
                "Interact with the Rokkon sentinel (it takes the dogu from the tree).",
                "At the unmarked shrine by the fallen tree, /easternbow, then place the dogu in it.",
                "Later, interact with the IWAKURA.",
            ]),
            new(11, VariantPath.Right, false, "Shishio",
            [
                "Interact with the Rokkon sentinel (it takes the dogu from the tree).",
                "At the unmarked shrine by the fallen tree, /easternbow, then place the dogu in it.",
                "Later, interact with the ANCIENT TREE instead of the iwakura.",
            ]),
            new(12, VariantPath.Middle, true, "Enenra",
            [
                "After the tiger, climb the pile of crates and pull the rope; wait for Hancock upstairs.",
                "Pull the three lantern enemies down the stairs and kill each one FACING one of the three unlit lanterns in front of the yellow-patterned wall — their death cone lights it.",
                "With three lit, the lantern on the blank wall lights: interact with it to reach the secret boss.",
            ]),
        ],
        Markers:
        [
            RouteMarker.Object(2013317, "leave the statue", 1, 2),
            RouteMarker.Object(2013316, "leave the statue", 1, 2),
            RouteMarker.Object(2013317, "Seiryu: face EAST (toward Suzaku)", 3, 4),
            RouteMarker.Object(2013316, "Suzaku: face SOUTH (the big doors)", 3, 4),
            RouteMarker.Object(2013319, "CLOSE the case after Hancock", 1),
            RouteMarker.Object(2013319, "leave the case open", 2),
            RouteMarker.Object(2013351, "climb, then pull the rope", 7, 12),
            RouteMarker.Object(2013344, "pull the rope", 7, 12),
            RouteMarker.Object(2013345, "pull the rope", 7, 12),
            RouteMarker.Object(2013346, "interact once the three lanterns are lit", 12),
            RouteMarker.Object(2013367, "ignore the sentinel", 8, 9),
            RouteMarker.Object(2013367, "interact: take the dogu", 10, 11),
            RouteMarker.Object(2013368, "clean this stone", 8),
            RouteMarker.Object(2013369, "clean this stone", 8),
            RouteMarker.Object(2013370, "clean this stone", 8),
            RouteMarker.Object(2013371, "clean this stone", 8),
            RouteMarker.Object(2013368, "leave the stone", 9),
            RouteMarker.Object(2013369, "leave the stone", 9),
            RouteMarker.Object(2013370, "leave the stone", 9),
            RouteMarker.Object(2013371, "leave the stone", 9),
            RouteMarker.Object(2013373, "interact: iwakura", 10),
            RouteMarker.Object(2013372, "interact: ancient tree", 11),
        ]);

    public static readonly VariantDungeon Aloalo = new(
        Series: 3,
        Name: "Aloalo Island",
        TerritoryId: 1176,
        FirstNotebookRow: 25,
        Forks: new Dictionary<VariantPath, string>
        {
            [VariantPath.Left] = "Take the LEFT portal at the start.",
            [VariantPath.Middle] = "Take the MIDDLE portal at the start.",
            [VariantPath.Right] = "Take the RIGHT portal at the start.",
        },
        Routes:
        [
            new(1, VariantPath.Left, false, "Ketuduke",
            [
                "Decline Matsya's request to fish.",
                "Do not help Zozone — let him run off at zero HP.",
            ]),
            new(2, VariantPath.Left, false, "Ketuduke",
            [
                "Decline Matsya's request to fish.",
                "Keep Zozone alive: heal him (Variant Cure works on any job) or hold the enemies off him.",
            ]),
            new(3, VariantPath.Left, false, "Ketuduke",
            [
                "Let Matsya fish.",
                "Later, interact with the patch of sand on the RIGHT (a southern sow bug).",
            ]),
            new(4, VariantPath.Left, false, "Ketuduke",
            [
                "Let Matsya fish.",
                "Later, interact with the mound of rocks on the LEFT (a southern worm).",
            ]),
            new(5, VariantPath.Middle, false, "Lala",
            [
                "Go straight through, kill everything, touch nothing.",
            ]),
            new(6, VariantPath.Middle, false, "Lala",
            [
                "At the second AND third packs after the first boss, walk into the red-flowered bushes and scare off the armadillos at the end of each hidden path.",
            ]),
            new(7, VariantPath.Middle, false, "Lala",
            [
                "After the first boss, kill the treant first, then every caterpillar on the sigil.",
                "At the last pack, do NOT damage J'jhimei behind the left wood golem — wait for the golems to wake.",
            ]),
            new(8, VariantPath.Middle, false, "Lala",
            [
                "After the first boss, kill the treant first, then every caterpillar on the sigil.",
                "At the last pack, hit J'jhimei behind the left wood golem too.",
            ]),
            new(9, VariantPath.Right, false, "Statice",
            [
                "Accept Statice's offer and take the portal she makes.",
            ]),
            new(10, VariantPath.Right, false, "Statice",
            [
                "Deny Statice's offer three times.",
                "Leave the treasure coffers alone, ignore the animal statues, and pull the lever.",
                "(Also possible by accepting: after the portal and the six raptors, take the vine-hidden tunnel on the right and dodge her whoopee-cushion trap.)",
            ]),
            new(11, VariantPath.Right, false, "Statice",
            [
                "Deny Statice's offer three times.",
                "Open a treasure coffer and kill the mimics; ignore the animal statues and pull the lever.",
            ]),
            new(12, VariantPath.Right, true, "Loquloqui",
            [
                "Deny Statice's offer three times. Do NOT pull the lever until all three rites are done.",
                "Sparrow: /say O dancer of the skies, hear me.  — /blowkiss at the sparrow, circle ONCE clockwise (turtle, whale, sparrow), /dance at the sparrow.",
                "Whale: /say O messenger from beyond the horizon, hear me.  — circle ONCE clockwise (sparrow, turtle, whale), then ONCE counter-clockwise, /dance at the whale.",
                "Turtle: /say O wayfarer of land and sea, hear me.  — circle TWICE counter-clockwise (sparrow, whale, turtle), /bow at the turtle.",
                "Each rite: start in the blue circle, finish within a minute; only the player with the buff counts. A failed rite can be retried once the buff ends.",
                "Follow Matsya down the secret passage, take the leather sack from Whasbyrm, then place the statues: sparrow furthest from the boss room, whale middle, turtle nearest.",
            ]),
        ],
        Markers:
        [
            RouteMarker.Npc(12531, "let Zozone flee", 1),
            RouteMarker.Npc(12531, "keep Zozone alive", 2),
            RouteMarker.Object(2013482, "interact: patch of sand", 3),
            RouteMarker.Object(2013485, "interact: mound of rocks", 4),
            RouteMarker.Spot(37.52f, 194.30f, -694.80f, "into the bushes: scare the armadillos", 6),
            RouteMarker.Spot(70.83f, 201.64f, -805.38f, "into the bushes: scare the armadillos", 6),
            RouteMarker.Spot(121.15f, 182.17f, -612.82f, "treant first, then the caterpillars", 7, 8),
            RouteMarker.Npc(12532, "do NOT hit J'jhimei", 7),
            RouteMarker.Npc(12532, "hit J'jhimei", 8),
            RouteMarker.Spot(908.06f, 327.20f, -561.26f, "Statice: ACCEPT", 9),
            RouteMarker.Spot(908.06f, 327.20f, -561.26f, "Statice: deny x3", 10, 11, 12),
            RouteMarker.Spot(619.60f, 67.41f, -682.26f, "route 9", 9),
            RouteMarker.Spot(809.13f, 329.18f, -611.70f, "leave the coffers", 10),
            RouteMarker.Spot(809.13f, 329.18f, -611.70f, "open a coffer", 11),
            RouteMarker.Spot(861.10f, 333.96f, -687.68f, "the three rites", 12),
            RouteMarker.Object(2013493, "pull the lever", 10, 11),
            RouteMarker.Object(2013493, "NOT until all three rites are done", 12),
            RouteMarker.Object(2013537, "take the leather sack", 12),
        ]);

    public static readonly VariantDungeon MerchantsTale = new(
        Series: 4,
        Name: "The Merchant's Tale",
        TerritoryId: 1315,
        FirstNotebookRow: 37,
        Forks: new Dictionary<VariantPath, string>
        {
            [VariantPath.Left] = "At the first fork answer \"left, into the residential area\".",
            [VariantPath.Middle] = "At the first fork answer \"straight, toward the great gate\".",
            [VariantPath.Right] = "At the first fork answer \"right, into the marketplace\".",
        },
        Routes:
        [
            new(1, VariantPath.Left, false, "The Pari of Plenty",
            [
                "After the first boss, break the damaged fence beside the spoils coffer.",
                "Wait at the bird nest for the willful wind and kill it before its Flee cast finishes.",
            ]),
            new(2, VariantPath.Left, false, "The Pari of Plenty",
            [
                "After the first boss, break the damaged fence beside the spoils coffer.",
                "Ignore the willful wind and let it steal the apple.",
            ]),
            new(3, VariantPath.Left, false, "The Pari of Plenty",
            [
                "After the first boss, take the main path through the gate.",
                "Stand by the damaged stone railing on the left and let the wisp's AoE hit you, then go through the broken railing.",
            ]),
            new(4, VariantPath.Left, false, "The Pari of Plenty",
            [
                "After the first boss, take the main path through the gate and carry on — touch nothing.",
            ]),
            new(5, VariantPath.Right, false, "Darya the Sea-maid",
            [
                "After the first boss tell Y'nazqha \"The seaweed won't steer us wrong.\"",
                "Let the goobbue's Inhale swallow all FOUR clusters of colourful rocks before you kill it.",
            ]),
            new(6, VariantPath.Right, false, "Darya the Sea-maid",
            [
                "After the first boss tell Y'nazqha \"The seaweed won't steer us wrong.\"",
                "Kill the goobbue before it swallows all the rocks.",
            ]),
            new(7, VariantPath.Right, false, "Darya the Sea-maid",
            [
                "After the first boss tell Y'nazqha \"I'd fancy a stroll on the beach.\"",
                "After the first pack, interact with the giant conch shell.",
            ]),
            new(8, VariantPath.Right, false, "Darya the Sea-maid",
            [
                "After the first boss tell Y'nazqha \"I'd fancy a stroll on the beach.\"",
                "Leave the giant conch shell alone and go to the boss.",
            ]),
            new(9, VariantPath.Middle, false, "The Lone Swordmaster",
            [
                "After the first boss tell Y'nazqha \"Let the mushrooms guide us true!\"",
                "Drag the stone puppet so its Magnetic Rock AoE hits all three large rock clusters in the cave.",
            ]),
            new(10, VariantPath.Middle, false, "The Lone Swordmaster",
            [
                "After the first boss tell Y'nazqha \"Let the mushrooms guide us true!\"",
                "Kill the stone puppet without lighting the rock clusters.",
            ]),
            new(11, VariantPath.Middle, false, "The Lone Swordmaster",
            [
                "After the first boss tell Y'nazqha \"Floating rocks warrant a closer look.\"",
                "Stand by each of the three large rocks on their platforms until they glow (a path on the west side reaches the upper one).",
            ]),
            new(12, VariantPath.Middle, false, "The Lone Swordmaster",
            [
                "After the first boss tell Y'nazqha \"Floating rocks warrant a closer look.\"",
                "Stay away from the large rocks.",
            ]),
            new(13, VariantPath.Right, true, "The Deadly Dandan",
            [
                "After the first boss tell Y'nazqha \"The seaweed won't steer us wrong.\"",
                "At the Shimmering Stage altar before the seaweed barrier, /bow — five coloured panels appear.",
                "/say I am returned!  then step on four panels in order: coral (green), conch (purple), starfish (magenta), pearl (blue).",
            ]),
        ],
        Markers:
        [
            RouteMarker.Object(2015018, "break the fence", 1, 2),
            RouteMarker.Object(2015018, "leave the fence: take the gate", 3, 4),
            RouteMarker.Spot(-706f, -36f, -548f, "this way (1 & 2)", 1, 2),
            RouteMarker.Spot(-730f, -38f, -542f, "this way (3 & 4)", 3, 4),
            RouteMarker.Npc(14343, "kill it before Flee", 1),
            RouteMarker.Npc(14343, "let it take the apple", 2),
            RouteMarker.Spot(-777.16f, -50.23f, -677.52f, "stand by the railing for the wisp's AoE", 3),
            RouteMarker.Spot(212f, -30f, 664f, "seaweed", 5, 6, 13),
            RouteMarker.Spot(205f, -29f, 650f, "beach", 7, 8),
            RouteMarker.Spot(300f, -28f, 690f, "let it Inhale all four rock clusters", 5),
            RouteMarker.Spot(300f, -28f, 690f, "kill it before it swallows them all", 6),
            RouteMarker.Object(2015020, "interact: giant conch shell", 7),
            RouteMarker.Object(2015020, "leave the shell", 8),
            RouteMarker.Spot(64f, -31f, -634f, "mushrooms", 9, 10),
            RouteMarker.Spot(57f, -31f, -642f, "floating rocks", 11, 12),
            RouteMarker.Spot(130f, -25f, -626f, "bait its AoE onto the three rock clusters", 9),
            RouteMarker.Spot(130f, -25f, -626f, "don't light the rock clusters", 10),
            RouteMarker.Spot(66f, -30f, -714f, "stand by the three rocks until they glow", 11),
            RouteMarker.Spot(66f, -30f, -714f, "stay away from the rocks", 12),
            RouteMarker.Spot(400f, -29f, 658f, "/bow, then /say I am returned!", 13),
        ]);

    public static readonly IReadOnlyList<VariantDungeon> All = [Sildihn, Rokkon, Aloalo, MerchantsTale];

    public static VariantDungeon? ByTerritory(uint territoryId) => All.FirstOrDefault(d => d.TerritoryId == territoryId);

    public static VariantDungeon? BySeries(int series) => All.FirstOrDefault(d => d.Series == series);
}
