using System;
using System.Collections.Generic;
using System.Text;

namespace Charon.Features.Diagnostics;

/// <summary>
/// A bounded, newest-first record of what a feature SAW and DID, step by step. Pure — no Dalamud
/// types — and reusable by any feature that drives the game through a sequence of windows.
///
/// Born at the summoning bell, where it found every bug that the status line alone could not:
///   - it lives IN MEMORY because the feature's first live failure happened while dalamud.log was
///     pinned at its size cap and recording nothing — a diagnostic that exists only in the log is no
///     diagnostic when the log is dead;
///   - it records STALLS as well as actions (<see cref="Note"/>), because a trace of actions alone
///     makes a hang look like an ordinary last step. Each distinct reason for doing nothing is noted
///     ONCE, so a stall names itself without flooding the buffer.
///
/// The display half is <c>Windows/Components/TracePanel</c>, which only exists in Debug builds.
/// </summary>
public sealed class StepTrace
{
    private readonly List<string> _lines = new();
    private readonly int _capacity;
    private string _lastNoted = string.Empty;

    public StepTrace(int capacity = 16)
    {
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<string> Lines => _lines;

    /// <summary>
    /// Records an action. <paramref name="at"/> is formatted as given, so the caller decides local
    /// or UTC (and a test can pass a fixed time). Recording an action also re-arms <see cref="Note"/>:
    /// after doing something, the same waiting reason is worth hearing about again.
    /// </summary>
    public void Record(DateTime at, string line)
    {
        _lastNoted = string.Empty;
        Push(at, line);
    }

    /// <summary>
    /// Records a reason for doing NOTHING, but only when it differs from the last one noted — so a
    /// stall appears in the trace once instead of never (actions-only) or every frame (unfiltered).
    /// Returns whether a line was added.
    /// </summary>
    public bool Note(DateTime at, string reason, string line)
    {
        if (string.Equals(reason, _lastNoted, StringComparison.Ordinal))
            return false;

        _lastNoted = reason;
        Push(at, line);
        return true;
    }

    public void Clear()
    {
        _lines.Clear();
        _lastNoted = string.Empty;
    }

    /// <summary>
    /// The trace as one block of text for the clipboard, with any extra evidence appended under its
    /// own heading — the retainer list's raw values rode along this way, which is how the row layout
    /// got read off real data instead of guessed.
    /// </summary>
    public string ToText(IReadOnlyList<(string Title, IReadOnlyList<string> Lines)>? extras = null)
    {
        var text = new StringBuilder(string.Join(Environment.NewLine, _lines));
        if (extras != null)
        {
            foreach (var (title, lines) in extras)
            {
                if (lines.Count == 0)
                    continue;

                text.Append(Environment.NewLine).Append("--- ").Append(title).Append(" ---");
                foreach (var line in lines)
                    text.Append(Environment.NewLine).Append(line);
            }
        }

        return text.ToString();
    }

    private void Push(DateTime at, string line)
    {
        _lines.Insert(0, $"{at:HH:mm:ss.f}  {line}");
        if (_lines.Count > _capacity)
            _lines.RemoveAt(_lines.Count - 1);
    }
}
