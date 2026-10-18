using System.Collections.Generic;
using System.Linq;

namespace Charon.Features.Variant;

/// <summary>
/// One step on the way through a variant dungeon: the choice made (<see cref="Label"/>), and either the
/// choices that follow it or the record it ends in (<see cref="Route"/> > 0).
/// </summary>
public sealed record RouteNode(string Label, int Route, IReadOnlyList<RouteNode> Children)
{
    public bool IsLeaf => Children.Count == 0;

    public static RouteNode Branch(string label, params RouteNode[] children) => new(label, 0, children);

    public static RouteNode Leaf(string label, int route) => new(label, route, []);

    /// <summary>Every leaf under this node, top to bottom.</summary>
    public IEnumerable<RouteNode> Leaves() => IsLeaf ? [this] : Children.SelectMany(c => c.Leaves());
}

/// <summary>
/// Where the routes go: each dungeon's routes as the decision tree you actually walk — the fork at the
/// start, then each choice that splits the routes, ending in the record. Pure data, the same solutions as
/// <see cref="VariantRoutes"/> laid out as branches. A route may end two branches (Aloalo route 10 is
/// reachable by accepting Statice's offer AND by denying it).
/// </summary>
public static class VariantTrees
{
    public static IReadOnlyDictionary<VariantPath, RouteNode> For(VariantDungeon dungeon) => dungeon.Series switch
    {
        1 => Sildihn,
        2 => Rokkon,
        3 => Aloalo,
        4 => MerchantsTale,
        _ => new Dictionary<VariantPath, RouteNode>(),
    };

    private static readonly Dictionary<VariantPath, RouteNode> Sildihn = new()
    {
        [VariantPath.Left] = RouteNode.Branch("Left door",
            RouteNode.Branch("Solve the two-eyes puzzle",
                RouteNode.Leaf("Open the rubble door first", 1),
                RouteNode.Leaf("Let Nanamo open the gate", 2)),
            RouteNode.Branch("Fail the two-eyes puzzle",
                RouteNode.Leaf("Hit every cotton spore", 3),
                RouteNode.Leaf("Avoid the cotton spores", 4))),
        [VariantPath.Middle] = RouteNode.Branch("Middle door",
            RouteNode.Branch("Fail the scales",
                RouteNode.Leaf("Pull the RIGHT lever", 8),
                RouteNode.Leaf("Pull the LEFT lever", 9)),
            RouteNode.Branch("Solve the scales (helm L, fruit R)",
                RouteNode.Leaf("Statue of Nald", 10),
                RouteNode.Leaf("Statue of Thal", 11))),
        [VariantPath.Right] = RouteNode.Branch("Right door",
            RouteNode.Branch("Left lift",
                RouteNode.Leaf("\"The blessed spark\"", 5),
                RouteNode.Leaf("\"The sacred fire\"", 6)),
            RouteNode.Branch("Right lift, drakes big to small",
                RouteNode.Leaf("Interact with the sarcophagus", 7),
                RouteNode.Leaf("Emotes at the sarcophagus", 12))),
    };

    private static readonly Dictionary<VariantPath, RouteNode> Rokkon = new()
    {
        [VariantPath.Left] = RouteNode.Branch("Left path",
            RouteNode.Branch("Leave the statues",
                RouteNode.Leaf("Close the katana case", 1),
                RouteNode.Leaf("Leave the case open", 2)),
            RouteNode.Branch("Turn Seiryu E, Suzaku S",
                RouteNode.Leaf("Let the Apa cast Water III", 3),
                RouteNode.Leaf("Stop the Apa's Water III", 4))),
        [VariantPath.Middle] = RouteNode.Branch("Middle path",
            RouteNode.Branch("Spare the baboon?",
                RouteNode.Leaf("\"Deal\"", 5),
                RouteNode.Leaf("\"Think about it\"", 6)),
            RouteNode.Branch("Climb the crates, pull the rope",
                RouteNode.Leaf("Kill the lanterns normally", 7),
                RouteNode.Leaf("Light the three lanterns", 12))),
        [VariantPath.Right] = RouteNode.Branch("Right path",
            RouteNode.Branch("Ignore the sentinel",
                RouteNode.Leaf("Clean all four stones", 8),
                RouteNode.Leaf("Leave the stones", 9)),
            RouteNode.Branch("Take the dogu to the shrine",
                RouteNode.Leaf("Interact with the iwakura", 10),
                RouteNode.Leaf("Interact with the ancient tree", 11))),
    };

    private static readonly Dictionary<VariantPath, RouteNode> Aloalo = new()
    {
        [VariantPath.Left] = RouteNode.Branch("Left portal",
            RouteNode.Branch("Decline Matsya's fishing",
                RouteNode.Leaf("Let Zozone flee", 1),
                RouteNode.Leaf("Keep Zozone alive", 2)),
            RouteNode.Branch("Let Matsya fish",
                RouteNode.Leaf("Patch of sand (right)", 3),
                RouteNode.Leaf("Mound of rocks (left)", 4))),
        [VariantPath.Middle] = RouteNode.Branch("Middle portal",
            RouteNode.Leaf("Straight through, touch nothing", 5),
            RouteNode.Leaf("Scare off both armadillo sets", 6),
            RouteNode.Branch("Treant first, then caterpillars",
                RouteNode.Leaf("Leave J'jhimei alone", 7),
                RouteNode.Leaf("Hit J'jhimei", 8))),
        [VariantPath.Right] = RouteNode.Branch("Right portal",
            RouteNode.Branch("Accept Statice's offer",
                RouteNode.Leaf("Take her portal", 9),
                RouteNode.Leaf("Vine tunnel, dodge the trap", 10)),
            RouteNode.Branch("Deny her offer three times",
                RouteNode.Leaf("Leave the coffers, pull the lever", 10),
                RouteNode.Leaf("Open a coffer, pull the lever", 11),
                RouteNode.Leaf("Do the three rites", 12))),
    };

    private static readonly Dictionary<VariantPath, RouteNode> MerchantsTale = new()
    {
        [VariantPath.Left] = RouteNode.Branch("Residential area",
            RouteNode.Branch("Break the damaged fence",
                RouteNode.Leaf("Kill the wind before Flee", 1),
                RouteNode.Leaf("Let it steal the apple", 2)),
            RouteNode.Branch("Through the gate",
                RouteNode.Leaf("Wisp's AoE breaks the railing", 3),
                RouteNode.Leaf("Carry on, touch nothing", 4))),
        [VariantPath.Middle] = RouteNode.Branch("Great gate",
            RouteNode.Branch("\"Let the mushrooms guide us\"",
                RouteNode.Leaf("Light all three rock clusters", 9),
                RouteNode.Leaf("Kill the puppet, no clusters", 10)),
            RouteNode.Branch("\"Floating rocks…\"",
                RouteNode.Leaf("Make the three rocks glow", 11),
                RouteNode.Leaf("Stay away from the rocks", 12))),
        [VariantPath.Right] = RouteNode.Branch("Marketplace",
            RouteNode.Branch("\"The seaweed won't steer us wrong\"",
                RouteNode.Leaf("Goobbue swallows all four rocks", 5),
                RouteNode.Leaf("Kill it before it swallows them", 6),
                RouteNode.Leaf("Altar: /bow, \"I am returned!\"", 13)),
            RouteNode.Branch("\"A stroll on the beach\"",
                RouteNode.Leaf("Interact with the conch shell", 7),
                RouteNode.Leaf("Leave the conch shell", 8))),
    };
}
