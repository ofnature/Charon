using System;
using System.Text;

namespace Charon.Features.AutoLogin;

/// <summary>
/// Lobby boxes that only inform and are safe to close with their OK button.
///
/// A box is recognised by its TEXT, compared with the game's own sheet row in the client's
/// language, never by the window alone: the same window (<c>SelectOk</c>) may carry something
/// whose only button cancels it, and that must be left to the player.
/// </summary>
public static class LobbyNotice
{
    /// <summary>
    /// Lobby sheet row: "The character you last logged out with in this play environment could not
    /// be found on the current data center. Please connect to another data center from the Data
    /// Center Selection screen." (read with Lumina from the game files, 2026-11-21). Seen over the
    /// character list on a PC running several accounts: the game's own record of the last character
    /// is shared between them, so the box comes up although the character wanted is on the list.
    /// </summary>
    public const uint LastCharacterNotHereRow = 1237;

    /// <summary>
    /// True when the text on screen is the sheet's text. Line breaks and spacing are ignored (the
    /// window wraps the text its own way); empty on either side never matches.
    /// </summary>
    public static bool Same(string? shown, string? sheet)
    {
        var a = Squeeze(shown);
        var b = Squeeze(sheet);
        return a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string Squeeze(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
                builder.Append(c);
        }

        return builder.ToString();
    }
}
