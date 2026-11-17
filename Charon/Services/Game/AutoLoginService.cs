using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Charon.Features.AutoLogin;
using Dalamud.Hooking;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Charon.Services.Game;

/// <summary>
/// Logs this client's character back in after a disconnect. The decisions are
/// <see cref="AutoLoginPolicy"/>; this reads the lobby screens and presses the buttons.
///
/// WHO: the character is remembered by content id while logged in, in memory only — every client on
/// the PC shares one config file, so a saved "last character" would be whichever client wrote last.
///
/// HOW each screen is driven comes from ECommons (MIT, NightmareXIV/ECommons — AddonMaster._TitleMenu
/// and _CharaSelectListMenu): the title menu is ready when its node 7 is visible, its node 3 is fully
/// opaque and neither TitleDCWorldMap nor TitleConnect is loaded, and Start is component button 4; the
/// character list is AgentLobby.LobbyData.CharaSelectEntries, and logging one in is a MouseClick event
/// with index 5 + its position. The disconnect box is the "Dialogue" addon with OK at button 4.
///
/// THE CLIENT STAYING OPEN: on a lobby error the game normally closes itself, which would end any
/// attempt to log back in during an outage. The game's lobby error handler is hooked and the error
/// code rewritten to a harmless one (0x3E80) before the game acts on it. That mechanism is Bluefissure's
/// NoKillPlugin (no licence — the facts only: the signature, the record's layout, the code). The
/// signature matched exactly once in the 2026.11.03 game executable.
/// </summary>
public sealed unsafe class AutoLoginService : IDisposable
{
    private const string LobbyErrorSig = "40 53 48 83 EC 30 48 8B D9 49 8B C8 E8 ?? ?? ?? ?? 8B D0";
    private const long HarmlessErrorCode = 0x3E80;

    /// <summary>The lobby error for "the login could not be verified": the session the launcher gave
    /// this client has expired. No amount of pressing Start gets past it — only a fresh start from
    /// the launcher does. (The value is from NoKillPlugin and VIWI, which both single it out.)</summary>
    private const uint AuthErrorCode = 0x332C;

    private const string AuthErrorReason =
        "the game could not verify the login (lobby error 13100) — the session from the launcher has expired, "
        + "so this client has to be closed and started again from the launcher";

    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ConflictInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(15);

    private delegate byte LobbyErrorDelegate(nint a1, nint a2, nint a3);

    private readonly IClientState _clientState;
    private readonly IGameGui _gameGui;
    private readonly IObjectTable _objectTable;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly CharonConfig _config;
    private readonly Func<ulong> _contentId;
    private readonly IPluginLog _log;
    private readonly AutoLoginPolicy _policy = new();
    private readonly Hook<LobbyErrorDelegate>? _lobbyErrorHook;
    private readonly string _hookProblem = string.Empty;

    private DateTime _nextUtc;
    private DateTime _nextConflictUtc;
    private DateTime _characterClickedUtc;
    private ulong _lastContentId;
    private string _lastName = string.Empty;
    private bool _viwiConflict;
    private int _recovered;
    private string _lastResult = string.Empty;
    private string _step = "idle";
    private bool _manual;
    private bool _titleShown;
    private bool _titleReady;
    private KnownCharacter? _remembered;
    private bool _authErrorSeen;
    private DateTime _nextRememberUtc;

    public AutoLoginService(IClientState clientState, IGameGui gameGui, IObjectTable objectTable,
        IGameInteropProvider interop, IDalamudPluginInterface pluginInterface, CharonConfig config,
        Func<ulong> contentId, IPluginLog log)
    {
        _clientState = clientState;
        _gameGui = gameGui;
        _objectTable = objectTable;
        _pluginInterface = pluginInterface;
        _config = config;
        _contentId = contentId;
        _log = log;
        Characters = new KnownCharacterStore(pluginInterface.ConfigDirectory.FullName, log);

        try
        {
            _lobbyErrorHook = interop.HookFromSignature<LobbyErrorDelegate>(LobbyErrorSig, LobbyErrorDetour);
            _lobbyErrorHook.Enable();
        }
        catch (Exception ex)
        {
            _hookProblem = "the lobby error handler was not found (game update?), so a lobby error can still close the client";
            _log.Warning(ex, "Auto login: lobby error hook not installed");
        }

        _clientState.Logout += OnLogout;
    }

    /// <summary>A run is under way.</summary>
    public bool Running => _policy.Armed;

    /// <summary>Every character that has logged in on this PC — what the login window offers.</summary>
    public KnownCharacterStore Characters { get; }

    /// <summary>The title menu is up and ready for a press.</summary>
    public bool TitleReady => _titleReady;

    public string RunName => _lastName;
    public ulong RunContentId => _lastContentId;
    public string Step => _step;

    /// <summary>Why the last run ended without logging in; empty after a login or a new start.</summary>
    public string LastFailure { get; private set; } = string.Empty;

    /// <summary>The login window has something to show: a run to stop, or the title menu with
    /// characters to offer.</summary>
    public bool WindowWanted(DateTime nowUtc) =>
        !_clientState.IsLoggedIn && (_policy.Armed || (_titleShown && Characters.Read(nowUtc).Count > 0));

    /// <summary>
    /// Logs a chosen character in from the title menu — the login window's buttons. The same steps as
    /// after a disconnect, started by a click, so it needs neither the auto login switch nor a
    /// character seen this session. Refused unless the title menu is ready and nothing is running.
    /// </summary>
    public bool StartManual(KnownCharacter character)
    {
        if (_clientState.IsLoggedIn || _policy.Armed || !_titleReady || character.ContentId == 0)
            return false;

        _lastContentId = character.ContentId;
        _lastName = character.Name;
        _lastResult = string.Empty;
        LastFailure = string.Empty;
        _manual = true;
        _policy.Arm(DateTime.UtcNow);
        _log.Info("Auto login: started from the login window");
        return true;
    }

    public string Status
    {
        get
        {
            if (_policy.Armed && _manual)
                return $"logging {_lastName} in from the login window — {_step}";
            if (!_config.AutoLoginEnabled)
                return _lastResult.Length > 0 ? $"off · last login from the window: {_lastResult}" : "off";
            if (_viwiConflict)
                return "standing down — VIWI's AutoLogin is switched on in this client; turn one of the two off";

            var who = _lastContentId != 0 ? $"will log {_lastName} back in" : "no character seen yet this session";
            var text = _policy.Armed
                ? $"logging {_lastName} back in — {_step}"
                : $"watching for a disconnect · {who}";
            if (_recovered > 0)
                text += $" · {_recovered} recovered this session";
            if (_lastResult.Length > 0 && !_policy.Armed)
                text += $" · last run: {_lastResult}";
            if (_config.AutoLoginKeepClientOpen && _hookProblem.Length > 0)
                text += $" · {_hookProblem}";
            return text;
        }
    }

    /// <summary>Stops a run under way. The next disconnect starts a new one.</summary>
    public void Stop()
    {
        if (!_policy.Armed)
            return;
        _policy.Disarm();
        _manual = false;
        _lastResult = "stopped by hand";
        _log.Info("Auto login: stopped by hand");
    }

    public void Dispose()
    {
        _clientState.Logout -= OnLogout;
        _lobbyErrorHook?.Dispose();
    }

    public void Update(DateTime nowUtc)
    {
        if (nowUtc < _nextUtc)
            return;
        _nextUtc = nowUtc + Interval;

        if (_clientState.IsLoggedIn && _contentId() is var id and not 0)
        {
            _lastContentId = id;
            _lastName = _objectTable.LocalPlayer?.Name.TextValue ?? _lastName;
            Remember(id, nowUtc);
        }
        else if (!_clientState.IsLoggedIn)
        {
            _remembered = null; // the next login refreshes its entry, and with it "last played"
        }

        _titleShown = !_clientState.IsLoggedIn && Shown("_TitleMenu") != null;
        _titleReady = _titleShown && TitleMenuReady();

        if (nowUtc >= _nextConflictUtc)
        {
            _nextConflictUtc = nowUtc + ConflictInterval;
            _viwiConflict = ViwiAutoLoginOn();
        }

        // An expired session ends the run at once, with the reason on the login window: trying again
        // for six hours would only raise the same error box every time.
        if (_authErrorSeen)
        {
            _authErrorSeen = false;
            _lastResult = LastFailure = AuthErrorReason;
            _log.Warning($"Auto login: {AuthErrorReason}");
            _policy.Disarm();
        }

        if (!_policy.Armed)
            _manual = false;

        // A run started from the login window is the player asking; the two gates below are about
        // runs that start by themselves. Two plugins pressing the same lobby buttons unasked is worse
        // than either alone.
        if (!_manual && (!_config.AutoLoginEnabled || _viwiConflict))
        {
            _policy.Disarm();
            return;
        }

        if (!_policy.Armed)
            return;

        var screen = Screen(nowUtc);
        var step = _policy.Next(screen, CharacterIndex() >= 0, nowUtc);
        _step = step.Reason;
        switch (step.Action)
        {
            case LoginAction.DismissError:
                Act("dismissed the error box", DismissError());
                break;
            case LoginAction.PressStart:
                Act("pressed Start", PressStart());
                break;
            case LoginAction.SelectCharacter:
                if (Act($"chose the character at list position {CharacterIndex() + 1}", SelectCharacter()))
                    _characterClickedUtc = nowUtc;
                break;
            case LoginAction.Confirm:
                Act("confirmed the login", Confirm());
                break;
            case LoginAction.Finished:
                if (_manual)
                    _lastResult = "logged in";
                else
                {
                    _recovered++;
                    _lastResult = step.Reason;
                }

                LastFailure = string.Empty;
                _log.Info($"Auto login: {_lastResult}");
                break;
            case LoginAction.GaveUp:
                _lastResult = step.Reason;
                LastFailure = step.Reason;
                _log.Warning($"Auto login: {step.Reason}");
                break;
        }
    }

    /// <summary>Keeps the logged-in character's entry on the login window's list current: written at
    /// each login, and again when its job or level changes.</summary>
    private void Remember(ulong contentId, DateTime nowUtc)
    {
        var player = _objectTable.LocalPlayer;
        if (player == null || nowUtc < _nextRememberUtc)
            return;

        var name = player.Name.TextValue;
        if (name.Length == 0)
            return;

        var job = player.ClassJob.ValueNullable;
        var character = new KnownCharacter(contentId, name,
            player.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty, nowUtc,
            job?.Abbreviation.ExtractText() ?? string.Empty,
            RealLevel(job?.ExpArrayIndex ?? -1, player.Level),
            job?.Role ?? 0);
        if (_remembered is { } r && r with { SeenUtc = nowUtc } == character)
            return;

        if (Characters.Remember(character))
            _remembered = character;
        else
            _nextRememberUtc = nowUtc + TimeSpan.FromSeconds(10); // the file is busy or unwritable; not every tick
    }

    /// <summary>The job's own level, not the level a duty has synced it down to.</summary>
    private static int RealLevel(int expArrayIndex, int shown)
    {
        var state = PlayerState.Instance();
        if (state == null || expArrayIndex < 0 || expArrayIndex >= state->ClassJobLevels.Length)
            return shown;
        var level = state->ClassJobLevels[expArrayIndex];
        return level > 0 ? level : shown;
    }

    private bool Act(string what, bool done)
    {
        _log.Info(done ? $"Auto login: {what}" : $"Auto login: could not — {what}");
        return done;
    }

    private void OnLogout(int type, int code)
    {
        if (!_config.AutoLoginEnabled || _viwiConflict || !AutoLoginPolicy.IsDisconnect(code))
            return;

        if (_lastContentId == 0)
        {
            _log.Warning($"Auto login: disconnected (code {code}) before any character was seen this session; nothing to log back in");
            return;
        }

        _log.Info($"Auto login: disconnected (type {type}, code {code}); logging back in");
        _lastResult = string.Empty;
        LastFailure = string.Empty;
        _policy.Arm(DateTime.UtcNow);
    }

    private LoginScreen Screen(DateTime nowUtc)
    {
        if (_clientState.IsLoggedIn)
            return LoginScreen.LoggedIn;
        if (Shown("Dialogue") != null)
            return LoginScreen.Error;

        if (Shown("_CharaSelectListMenu") != null)
        {
            if (Shown("SelectYesno") == null)
                return LoginScreen.CharacterSelect;

            // A Yes/No is only ours to answer straight after our own click on the character.
            return nowUtc - _characterClickedUtc < ConfirmWindow ? LoginScreen.Confirm : LoginScreen.Other;
        }

        return TitleMenuReady() ? LoginScreen.Title : LoginScreen.Other;
    }

    private AtkUnitBase* Shown(string name)
    {
        var addon = (AtkUnitBase*)_gameGui.GetAddonByName(name).Address;
        return addon != null && addon->IsReady && addon->IsVisible ? addon : null;
    }

    private bool Loaded(string name) => _gameGui.GetAddonByName(name).Address != 0;

    private bool TitleMenuReady()
    {
        var title = Shown("_TitleMenu");
        if (title == null || Loaded("TitleDCWorldMap") || Loaded("TitleConnect"))
            return false;
        if (title->UldManager.NodeListCount <= 7 || !title->UldManager.NodeList[7]->IsVisible())
            return false;
        var fade = title->GetNodeById(3);
        return fade != null && fade->Color.A == 0xFF;
    }

    /// <summary>Where the remembered character is in the character list on screen, or -1.</summary>
    private int CharacterIndex()
    {
        if (_lastContentId == 0 || Shown("_CharaSelectListMenu") == null)
            return -1;
        var agent = AgentLobby.Instance();
        if (agent == null)
            return -1;

        var entries = agent->LobbyData.CharaSelectEntries.AsSpan();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i].Value;
            if (entry != null && entry->ContentId == _lastContentId)
                return i;
        }

        return -1;
    }

    private bool DismissError()
    {
        var dialogue = Shown("Dialogue");
        return dialogue != null && AtkClickHelper.ClickButton(dialogue, dialogue->GetComponentButtonById(4));
    }

    private bool PressStart()
    {
        var title = Shown("_TitleMenu");
        if (title == null)
            return false;
        var start = title->GetComponentButtonById(4);
        return start != null && start->IsEnabled && AtkClickHelper.ClickButton(title, start);
    }

    private bool SelectCharacter()
    {
        var list = Shown("_CharaSelectListMenu");
        var index = CharacterIndex();
        if (list == null || index < 0)
            return false;
        AtkClickHelper.LobbyCharacterLogin(list, index);
        return true;
    }

    private bool Confirm()
    {
        var yesno = Shown("SelectYesno");
        return yesno != null && AtkClickHelper.ClickButton(yesno, ((AddonSelectYesno*)yesno)->YesButton);
    }

    /// <summary>VIWI is loaded and its own AutoLogin is switched on (read from its config file).</summary>
    private bool ViwiAutoLoginOn()
    {
        try
        {
            if (!_pluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "VIWI"))
                return false;
            var file = Path.Combine(_pluginInterface.ConfigFile.DirectoryName ?? string.Empty, "VIWI.json");
            if (!File.Exists(file))
                return false;
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            return json.RootElement.TryGetProperty("AutoLogin", out var module)
                   && module.TryGetProperty("Enabled", out var enabled)
                   && enabled.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false; // unreadable: assume it is not in the way
        }
    }

    /// <summary>
    /// Runs before the game handles a lobby error. The third argument points at the error record:
    /// its first byte's low nibble says a code is present, and the code is the 32-bit value 8 bytes
    /// in. Rewriting a present code to 0x3E80 makes the game show the error and return to the title
    /// instead of closing.
    /// </summary>
    private byte LobbyErrorDetour(nint a1, nint a2, nint a3)
    {
        try
        {
            if (a3 != 0 && (Marshal.ReadByte(a3) & 0xF) > 0)
            {
                var code = (uint)Marshal.ReadInt32(a3 + 8);
                if ((code & 0xFFFF) == AuthErrorCode)
                    _authErrorSeen = true;

                if (code > 0 && _config.AutoLoginEnabled && _config.AutoLoginKeepClientOpen && !_viwiConflict)
                {
                    Marshal.WriteInt64(a3 + 8, HarmlessErrorCode);
                    _log.Info($"Auto login: lobby error {code & 0xFFFF} — kept the client open");
                }
                else if (code > 0)
                {
                    _log.Info($"Auto login: lobby error {code & 0xFFFF} — left to the game");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Auto login: lobby error hook failed; the game handles this error as usual");
        }

        return _lobbyErrorHook!.Original(a1, a2, a3);
    }
}
