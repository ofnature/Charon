using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Charon.Features.Leveling;
using Charon.Services.Game;
using Charon.Windows.Components;

namespace Charon.Windows;

/// <summary>
/// The Doman donation body: the week's numbers as tiles, the two steps with their state, the exact
/// stack math and the live status line. Shared by the main window's Doman Donate section and by the
/// pop-up window that rides the donation basket (<see cref="DomanWindow"/>), the way
/// <see cref="FcChestView"/> is shared — so neither is a dead end and the two can never disagree.
///
/// The flow is two-phase and that is the whole reason this body is laid out as steps: PREPARE closes the
/// basket on purpose (the game blocks inventory splits with it open) and splits the stack, then STAGE
/// needs it open again. A single unlabelled pair of buttons makes that read as a bug.
/// </summary>
internal static class DomanView
{
    public static void DrawBody(CharonConfig config, Action save, DomanDonator doman, GilCapSeller gilSeller)
    {
        var snapshot = doman.GetSnapshot();
        var (itemName, price) = gilSeller.ItemInfo(config.GilItemId);
        var held = GilCapSeller.CountInBags(config.GilItemId);
        var enclave = doman.ReadEnclaveState();

        // The window's numbers are only readable while it is open; the enclave manager reads anywhere.
        // Prefer the window when it is up (it is what the actions themselves act on).
        var budget = snapshot.Open ? snapshot.BudgetRemaining : enclave.Loaded ? enclave.BudgetRemaining : -1;
        var rate = snapshot.Open ? snapshot.RatePercent : enclave.Loaded ? enclave.RatePercent : -1;
        // What Stage could hand over: the prepared stack, or a holding already at/under the target
        // (holding less than the target means the whole holding IS the donation — the manager's own rule).
        var target = snapshot.Open
            ? DonationWindowParser.TargetQuantity(snapshot.BudgetRemaining, price, snapshot.RatePercent, held)
            : 0;
        var stageable = doman.StackReady || (held > 0 && target > 0 && held <= target);
        var plan = DomanStepPolicy.Compose(snapshot.Open, doman.Busy, doman.StackReady, held, stageable, budget, itemName);

        DrawTiles(budget, rate, snapshot.GrandTotal);
        ImGui.Spacing();

        DrawStep(1, "Prepare stack",
            "Reads the budget and rate, closes the basket (the game blocks splits while it is open) and "
            + "splits the exact stack off what you carry.",
            plan.Primary == DomanStep.Prepare, plan.StepOneDone);
        DrawStep(2, "Stage into basket",
            "Reopen the basket, then this donates the stack, presses Donate and answers the confirmation. "
            + "It overshoots the weekly budget by the smallest margin — over, never short.",
            plan.Primary == DomanStep.Stage, plan.StepTwoDone);

        ImGui.Spacing();
        DrawMath(snapshot, enclave, doman, itemName, price, held);
        DrawFeedback(doman, enclave);

        ImGui.Spacing();
        DrawActions(config, doman, plan);
    }

    /// <summary>Budget left, rate, and what is staged in the window right now.</summary>
    private static void DrawTiles(long budget, long rate, long staged)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = MathF.Max((ImGui.GetContentRegionAvail().X - (14f * scale)) / 3f, 80f);
        var budgetText = budget >= 0 ? budget.ToString("N0") : "—";
        var rateText = rate >= 0 ? $"{rate}%" : "—";

        StatTile.Draw("Budget left", budgetText, null, budget > 0 ? CharonTheme.AccentMint : CharonTheme.TextDim, width,
            "Weekly Doman Enclave donation budget remaining. A spent basket refuses to open at all.");
        ImGui.SameLine();
        StatTile.Draw("Rate", rateText, null, CharonTheme.Accent, width,
            "Gratuity rate this week — what each item's vendor value is multiplied by.");
        ImGui.SameLine();
        StatTile.Draw("Staged", staged > 0 ? staged.ToString("N0") : "—", null, CharonTheme.AccentAmber, width,
            "Value the game's basket is holding right now, before Donate is pressed.");
    }

    /// <summary>One numbered step: the flow's shape, with the state it is in and why.</summary>
    private static void DrawStep(int number, string title, string description, bool isNext, bool isDone)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var textX = 26f * scale;

        var accent = isDone ? CharonTheme.AccentMint : isNext ? CharonTheme.Accent : CharonTheme.TextDim;
        var chip = isDone ? "DONE" : isNext ? "NEXT" : "WAITING";
        var chipSize = Pill.Measure(chip);
        var chipX = origin.X + width - chipSize.X;

        var titleColour = isNext || isDone ? CharonTheme.TextStrong : CharonTheme.TextSecondary;

        ImGui.SetCursorScreenPos(new Vector2(origin.X + textX, origin.Y));
        ImGui.PushTextWrapPos(chipX - (8f * scale));
        ImGui.TextColored(titleColour, title);
        ImGui.PopTextWrapPos();

        ImGui.PushTextWrapPos(origin.X + width);
        ImGui.TextColored(CharonTheme.TextMuted, description);
        ImGui.PopTextWrapPos();
        var used = ImGui.GetItemRectMax().Y - origin.Y;

        // The circle and the chip are painted on top, at absolute positions.
        var centre = new Vector2(origin.X + (10f * scale), origin.Y + (10f * scale));
        if (isDone)
            dl.AddCircleFilled(centre, 9f * scale, ImGui.GetColorU32(CharonTheme.WithAlpha(accent, 0.14f)));
        dl.AddCircle(centre, 9f * scale, ImGui.GetColorU32(CharonTheme.WithAlpha(accent, 0.75f)), 24, 1.4f * scale);
        var numberSize = ImGui.CalcTextSize(number.ToString());
        dl.AddText(new Vector2(centre.X - (numberSize.X / 2f), centre.Y - (numberSize.Y / 2f)),
            ImGui.GetColorU32(accent), number.ToString());
        Pill.DrawAt(new Vector2(chipX, origin.Y), chip, accent);

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, MathF.Max(used, 22f * scale)));
    }

    /// <summary>What the donation would actually be: the item, what you carry, and the stack target.</summary>
    private static void DrawMath(
        DomanDonator.WindowSnapshot snapshot,
        DomanDonator.EnclaveState enclave,
        DomanDonator doman,
        string itemName,
        long price,
        int held)
    {
        if (snapshot.Open)
        {
            var gratuity = DonationWindowParser.UnitGratuity(price, snapshot.RatePercent);
            var target = DonationWindowParser.TargetQuantity(snapshot.BudgetRemaining, price, snapshot.RatePercent, held);
            ImGui.TextColored(CharonTheme.TextSecondary,
                $"{itemName}: {held:N0} in bags · {gratuity:N0} budget each"
                + (target > 0 ? $" · would donate {target}" : " · nothing to donate"));
            return;
        }

        // No basket: say what is known from the manager/cache rather than showing nothing.
        if (enclave.Loaded)
        {
            ImGui.TextColored(CharonTheme.TextSecondary, enclave.AcceptingDonations
                ? $"Game state: accepting — {enclave.BudgetRemaining:N0} budget left, rate {enclave.RatePercent}%"
                : "Game state: not accepting donations (spent this week, or Doman not unlocked)");
            ImGui.TextColored(CharonTheme.TextDim, $"{itemName}: {held:N0} in bags · open the basket for the stack math");
            return;
        }

        if (doman.DonatedThisWeek)
        {
            ImGui.TextColored(CharonTheme.StatusGreen,
                "This week's budget is spent — resets Tuesday 08:00 UTC. No trip needed.");
            return;
        }

        ImGui.TextColored(CharonTheme.TextDim, "Stand at the Doman Enclave donation basket and open it — the window is the session.");
    }

    /// <summary>The week-spent escape hatch, for a donation Charon never saw.</summary>
    private static void DrawFeedback(DomanDonator doman, DomanDonator.EnclaveState enclave)
    {
        if (enclave.Loaded || doman.DonatedThisWeek)
            return;

        if (ImGui.SmallButton("Mark this week spent##doman"))
            doman.MarkWeekSpent();
        CharonTheme.HelpMarker("For a donation made without Charon: a spent basket refuses to\n"
                               + "even open, so it can't be detected — tell it here instead.\n"
                               + "Clears itself at the Tuesday reset.\n"
                               + "(Only shown when the game's own Doman state isn't readable.)");
    }

    private static void DrawActions(CharonConfig config, DomanDonator doman, DomanStepPlan plan)
    {
        if (doman.Busy)
        {
            if (Buttons.Action("Stop", true, 120f, CharonTheme.AccentRose))
                doman.Cancel();
            CharonTheme.HelpMarker("Stops on the next tick. Nothing is queued, so there is never a\n"
                                   + "backlog to drain — closing the window stops it just as well.");
            ImGui.Spacing();
            ImGui.TextColored(CharonTheme.TextDisabled, doman.Status);
            return;
        }

        // Two buttons, two gates: Prepare needs the basket open and the item in the bags, Stage unlocks
        // once step 1 has left something stageable (a prepared stack, or a holding already under target).
        // The primary is whichever step's turn it is, so the next move is labelled rather than implied.
        var primaryIsPrepare = plan.Primary == DomanStep.Prepare;
        if (Buttons.Action(primaryIsPrepare ? "1. Prepare stack" : "2. Stage into basket",
                primaryIsPrepare ? plan.CanPrepare : plan.CanStage, 176f))
        {
            if (primaryIsPrepare)
                doman.RequestPrepare(config.GilItemId);
            else
                doman.RequestStage(config.GilItemId);
        }

        ImGui.SameLine();
        if (Buttons.Action(primaryIsPrepare ? "2. Stage into basket" : "1. Prepare stack",
                primaryIsPrepare ? plan.CanStage : plan.CanPrepare, 148f, CharonTheme.TextDim))
        {
            if (primaryIsPrepare)
                doman.RequestStage(config.GilItemId);
            else
                doman.RequestPrepare(config.GilItemId);
        }

        CharonTheme.HelpMarker("Prepare reads the budget and rate, closes the window (the game\n"
                               + "blocks splits while it is open) and splits the exact stack.\n"
                               + "Reopen the basket, then Stage runs the rest: donates the stack\n"
                               + "into the list, presses Donate, ticks Confirm on the budget\n"
                               + "dialog and answers Yes. Target overshoots the weekly budget by\n"
                               + "the smallest possible margin — over, never short.");

        if (plan.Reason.Length > 0 && plan.Reason != DomanStepPolicy.Running)
            ImGui.TextColored(CharonTheme.TextDim, plan.Reason);

        ImGui.Spacing();
        ImGui.TextColored(CharonTheme.TextDisabled, doman.Status);
    }
}
