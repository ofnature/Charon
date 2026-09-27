using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Charon.Features.Diagnostics;

namespace Charon.Windows.Components;

/// <summary>
/// Shows a <see cref="StepTrace"/> with a Copy button — DEBUG BUILDS ONLY.
///
/// Every method here is <c>[Conditional("DEBUG")]</c>, so in a Release build the compiler removes
/// the CALLS themselves (arguments included): a player never sees the panel, and a feature that wants
/// a trace just calls <see cref="Draw"/> with no <c>#if</c> of its own. Recording is left running in
/// every build — it is cheap and it also feeds the Debug log line — only the panel is gated.
/// </summary>
internal static class TracePanel
{
    /// <summary>
    /// Draws the newest <paramref name="show"/> lines under <paramref name="heading"/>, with a Copy
    /// button that takes the WHOLE trace plus any <paramref name="extras"/> (extra evidence, each under
    /// its own heading). Nothing is drawn while the trace is empty.
    /// </summary>
    [Conditional("DEBUG")]
    public static void Draw(
        string heading,
        StepTrace trace,
        IReadOnlyList<(string Title, IReadOnlyList<string> Lines)>? extras = null,
        int show = 10)
    {
        var lines = trace.Lines;
        if (lines.Count == 0)
            return;

        ImGui.TextColored(CharonTheme.TextSecondary, $"{heading} (newest first):");
        for (var i = 0; i < lines.Count && i < show; i++)
            ImGui.TextColored(CharonTheme.TextDisabled, lines[i]);

        // A unique id per heading, so two panels on one page never share a button.
        if (ImGui.SmallButton($"Copy trace##{heading}"))
            ImGui.SetClipboardText(trace.ToText(extras));
    }
}
