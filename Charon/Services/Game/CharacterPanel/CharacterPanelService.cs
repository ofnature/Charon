using System;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game.CharacterPanel;

/// <summary>
/// The refined Character window: crit / direct hit / determination as percentages, recast times,
/// mitigation, average damage and healing per 100 potency, stat-tier tooltips, and synced stats in
/// item tooltips.
///
/// A port of Character Panel Refined by Kouzukii (MIT) — the files beside this one are the original's,
/// kept close to it; this class stands where its plugin class, configuration and signature holder
/// stood. Three things are Charon's own:
///  - it does nothing while the original plugin is loaded (two plugins rebuilding one window would
///    each add their rows);
///  - the window's nodes are dropped when the window closes, and on unload with a refined window
///    still open the tooltip text is left allocated rather than freed under the game;
///  - what it is doing is reported as a status line.
/// </summary>
public sealed unsafe class CharacterPanelService : IDisposable
{
    /// <summary>The original plugin's internal name, as Dalamud lists it.</summary>
    private const string OriginalPlugin = "CharacterPanelRefined";
    private const string StatusAddon = "CharacterStatus";
    private const string ItemAddon = "ItemDetail";

    /// <summary>The game's own "show this node's tooltip" (the original's signature; one match in 7.5x).</summary>
    private const string ShowNodeTooltipSignature = "E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 41 83 FF 71";

    private static readonly TimeSpan OriginalCheckEvery = TimeSpan.FromSeconds(1);

    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IKeyState _keyState;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly CharacterStatusAugments _status;
    private readonly ItemTooltipAugments _items;

    private DateTime _nextOriginalCheckUtc = DateTime.MinValue;
    private bool _originalLoaded;
    private string? _note;
    private bool _failureLogged;

    internal CharonConfig Config { get; }
    internal IDataManager DataManager { get; }
    internal IGameGui GameGui { get; }
    internal IObjectTable ObjectTable { get; }
    internal IPluginLog Log { get; }
    internal bool CtrlHeld { get; private set; }

    /// <summary>Null when the signature is not found: the open tooltip then refreshes on the next hover.</summary>
    internal readonly delegate* unmanaged[Thiscall]<AtkTooltipManager*, AtkResNode*, void> ShowNodeTooltip;

    /// <summary>Charon's setting is on and the original plugin is not there to do the same job.</summary>
    internal bool Active => Config.CharacterPanelEnabled && !_originalLoaded;

    public CharacterPanelService(
        IAddonLifecycle addonLifecycle,
        IKeyState keyState,
        IDalamudPluginInterface pluginInterface,
        ISigScanner sigScanner,
        IDataManager dataManager,
        IGameGui gameGui,
        IObjectTable objectTable,
        CharonConfig config,
        IPluginLog log)
    {
        _addonLifecycle = addonLifecycle;
        _keyState = keyState;
        _pluginInterface = pluginInterface;
        Config = config;
        DataManager = dataManager;
        GameGui = gameGui;
        ObjectTable = objectTable;
        Log = log;

        if (sigScanner.TryScanText(ShowNodeTooltipSignature, out var address))
            ShowNodeTooltip = (delegate* unmanaged[Thiscall]<AtkTooltipManager*, AtkResNode*, void>)address;

        _status = new CharacterStatusAugments(this);
        _status.ReloadLocs();
        _items = new ItemTooltipAugments(this);
        _originalLoaded = OriginalIsLoaded();

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, StatusAddon, OnStatusSetup);
        _addonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, StatusAddon, OnStatusUpdate);
        _addonLifecycle.RegisterListener(AddonEvent.PreFinalize, StatusAddon, OnStatusFinalize);
        _addonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, ItemAddon, OnItemUpdate);
    }

    public string Status
    {
        get
        {
            if (!Config.CharacterPanelEnabled)
                return _status.WindowLive ? "off (the open Character window keeps its rows until it is closed)" : "off";
            if (_originalLoaded)
                return "standing down - Character Panel Refined is loaded and has the window";
            if (_note != null)
                return _note;

            var state = _status.WindowLive ? "on - the Character window is refined" : "on - waiting for the Character window to open";
            return ShowNodeTooltip == null ? state + " (an open tooltip will not refresh on Ctrl: signature not found)" : state;
        }
    }

    /// <summary>A problem to show instead of the usual status; null clears it.</summary>
    internal void Note(string? note) => _note = note;

    /// <summary>Once per frame: Ctrl switches the recast rows and tooltips between with and without the job's speed buff.</summary>
    public void Update(DateTime utcNow)
    {
        if (utcNow >= _nextOriginalCheckUtc)
        {
            _nextOriginalCheckUtc = utcNow + OriginalCheckEvery;
            _originalLoaded = OriginalIsLoaded();
        }

        var ctrl = _keyState[VirtualKey.CONTROL];
        if (ctrl == CtrlHeld)
            return;

        CtrlHeld = ctrl;
        Guarded("refreshing on Ctrl", () => _status.Update());
    }

    private bool OriginalIsLoaded()
        => _pluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == OriginalPlugin);

    private void OnStatusSetup(AddonEvent type, AddonArgs args)
        => Guarded("rebuilding the Character window", () => _status.OnSetup(type, args));

    private void OnStatusUpdate(AddonEvent type, AddonArgs args)
        => Guarded("updating the Character window", () => _status.RequestedUpdate(args.Addon.Address));

    private void OnStatusFinalize(AddonEvent type, AddonArgs args)
        => _status.OnFinalize();

    private void OnItemUpdate(AddonEvent type, AddonArgs args)
        => Guarded("rewriting an item tooltip", () => _items.RequestedUpdate(type, args));

    private void Guarded(string doing, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _note = $"failed while {doing}: {ex.Message}";
            if (_failureLogged)
                return;

            _failureLogged = true;
            Log.Warning(ex, $"Character panel: failed while {doing}");
        }
    }

    public void Dispose()
    {
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, StatusAddon, OnStatusSetup);
        _addonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, StatusAddon, OnStatusUpdate);
        _addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, StatusAddon, OnStatusFinalize);
        _addonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, ItemAddon, OnItemUpdate);
        _status.Dispose();
    }
}
