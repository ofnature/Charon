using System;
using Charon.Features.Tweaks;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// The frame rate in the server info bar (the row of plugin readouts beside the game's clock),
/// refreshed once a second. It counts every frame this client draws, focused or not, so a client
/// held down in the background shows the rate it is really running at.
/// </summary>
public sealed class FpsDtr : IDisposable
{
    private const string Title = "Charon FPS";

    private readonly IDtrBar _dtrBar;
    private readonly Func<bool> _enabled;
    private readonly IPluginLog _log;
    private readonly FpsCounter _counter = new();

    private IDtrBarEntry? _entry;
    private bool _failed;

    public string Status { get; private set; } = "off";

    public FpsDtr(IDtrBar dtrBar, Func<bool> enabled, IPluginLog log)
    {
        _dtrBar = dtrBar;
        _enabled = enabled;
        _log = log;
    }

    public void Update(IFramework framework)
    {
        var changed = _counter.Record(framework.UpdateDelta.TotalMilliseconds);

        if (!_enabled())
        {
            if (_entry is { Shown: true })
                _entry.Shown = false;
            Status = "off";
            return;
        }

        if (_failed)
            return;

        try
        {
            _entry ??= _dtrBar.Get(Title);
            if (!_entry.Shown)
                _entry.Shown = true;

            if (!changed && _entry.Text != null)
                return;

            var text = _counter.Fps is { } fps ? $"{fps:0} fps" : "- fps";
            _entry.Text = text;
            Status = _entry.UserHidden ? $"{text} (hidden in Dalamud's server info bar settings)" : text;
        }
        catch (Exception ex)
        {
            // Another plugin holding the same title, or the bar not being there: say so once.
            _failed = true;
            Status = $"could not add the entry ({ex.Message})";
            _log.Warning(ex, "FPS readout: the server info bar entry could not be added");
        }
    }

    public void Dispose()
    {
        _entry?.Remove();
        _entry = null;
    }
}
