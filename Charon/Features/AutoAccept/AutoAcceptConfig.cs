namespace Charon.Features.AutoAccept;

/// <summary>
/// Immutable snapshot of the auto-accept settings, taken from <see cref="CharonConfig"/> each update.
/// <see cref="GroupInviteManager"/> depends only on this record, keeping it free of Dalamud types.
/// </summary>
public sealed record AutoAcceptConfig(
    bool Enabled,
    bool LanAutoWhitelist)
{
    /// <summary>Accept-delay window (seconds) — a small random pause so accepts don't look scripted.</summary>
    public const double MinAcceptDelaySeconds = 0.3;
    public const double MaxAcceptDelaySeconds = 0.8;

    public static AutoAcceptConfig From(CharonConfig config) => new(
        config.AutoAcceptEnabled,
        config.LanAutoWhitelist);
}
