using System;
using System.Collections.Generic;
using System.IO;
using Charon.Features.AutoLogin;
using Dalamud.Plugin.Services;

namespace Charon.Services.Game;

/// <summary>
/// The file behind the login window: every character that has logged in on this PC.
///
/// It is a file of its own (<c>login-characters.json</c>), not Charon.json, because every client on
/// the PC shares the config directory and each keeps its own copy of Charon.json in memory — a list
/// kept there would be whatever the last client to save happened to know. Here every change starts
/// from what is on disk now, and the write goes through a temporary file named for this process.
/// </summary>
public sealed class KnownCharacterStore
{
    private const string FileName = "login-characters.json";
    private static readonly TimeSpan ReadEvery = TimeSpan.FromSeconds(5);

    private readonly string _path;
    private readonly IPluginLog _log;
    private IReadOnlyList<KnownCharacter> _cached = Array.Empty<KnownCharacter>();
    private DateTime _nextReadUtc;

    public KnownCharacterStore(string configDirectory, IPluginLog log)
    {
        _path = Path.Combine(configDirectory, FileName);
        _log = log;
    }

    /// <summary>The list, re-read from disk at most every few seconds (another client may have added to it).</summary>
    public IReadOnlyList<KnownCharacter> Read(DateTime nowUtc)
    {
        if (nowUtc >= _nextReadUtc)
        {
            _nextReadUtc = nowUtc + ReadEvery;
            _cached = FromDisk() ?? _cached;
        }

        return _cached;
    }

    /// <summary>Adds the character, or replaces its entry with this newer one. False when the file
    /// could not be read or written just now; ask again later.</summary>
    public bool Remember(KnownCharacter character)
    {
        var onDisk = FromDisk();
        return onDisk != null && Write(KnownCharacters.Merge(onDisk, new[] { character }));
    }

    public bool Forget(ulong contentId)
    {
        var onDisk = FromDisk();
        return onDisk != null && Write(KnownCharacters.Without(onDisk, contentId));
    }

    /// <summary>Null when the file is there but unreadable this instant (another client is writing
    /// it). A change must never start from a guess at what the file holds: it would be written back
    /// without the other clients' characters.</summary>
    private IReadOnlyList<KnownCharacter>? FromDisk()
    {
        try
        {
            return File.Exists(_path) ? KnownCharacters.FromJson(File.ReadAllText(_path)) : Array.Empty<KnownCharacter>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool Write(IReadOnlyList<KnownCharacter> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = $"{_path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, KnownCharacters.ToJson(list));
            File.Move(temp, _path, overwrite: true);
            _cached = list;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Login window: could not save the character list");
            return false;
        }
    }
}
