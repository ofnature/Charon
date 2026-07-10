using System;
using System.Collections.Generic;
using System.Linq;

namespace Charon.Services;

/// <summary>
/// Manages the manual trusted-character list stored in <see cref="CharonConfig.ManualWhitelist"/>.
/// All matching is case-insensitive on both name and world. Pure logic — no Dalamud types.
/// </summary>
public sealed class WhitelistService
{
    private readonly List<WhitelistEntry> _entries;
    private readonly Action _save;

    public WhitelistService(List<WhitelistEntry> entries, Action save)
    {
        _entries = entries;
        _save = save;
    }

    public IReadOnlyList<WhitelistEntry> Entries => _entries;

    /// <summary>True when an ENABLED entry matches the name + world (case-insensitive).</summary>
    public bool IsWhitelisted(string characterName, string world) =>
        Find(characterName, world) is { Enabled: true };

    public WhitelistEntry? Find(string characterName, string world) =>
        _entries.FirstOrDefault(e =>
            e.CharacterName.Equals(characterName, StringComparison.OrdinalIgnoreCase)
            && e.World.Equals(world, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a new entry; returns false (no change) if one already exists for name + world.</summary>
    public bool Add(string characterName, string world)
    {
        characterName = characterName.Trim();
        world = world.Trim();
        if (characterName.Length == 0 || world.Length == 0)
            return false;
        if (Find(characterName, world) != null)
            return false;

        _entries.Add(new WhitelistEntry { CharacterName = characterName, World = world, Enabled = true });
        _save();
        return true;
    }

    public bool Remove(string characterName, string world)
    {
        var entry = Find(characterName, world);
        if (entry == null)
            return false;

        _entries.Remove(entry);
        _save();
        return true;
    }

    /// <summary>Enable/disable an entry without removing it from the list.</summary>
    public bool SetEnabled(string characterName, string world, bool enabled)
    {
        var entry = Find(characterName, world);
        if (entry == null || entry.Enabled == enabled)
            return false;

        entry.Enabled = enabled;
        _save();
        return true;
    }

    /// <summary>One-click import: adds every LAN toon not already listed. Returns how many were added.</summary>
    public int ImportFromLan(IEnumerable<LanToonInfo> lanToons)
    {
        var added = 0;
        foreach (var toon in lanToons)
        {
            if (string.IsNullOrWhiteSpace(toon.CharacterName))
                continue;
            if (Find(toon.CharacterName, toon.World) != null)
                continue;

            _entries.Add(new WhitelistEntry
            {
                CharacterName = toon.CharacterName.Trim(),
                World = toon.World.Trim(),
                Enabled = true,
            });
            added++;
        }

        if (added > 0)
            _save();
        return added;
    }
}
