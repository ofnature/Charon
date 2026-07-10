using System;

namespace Charon.Features.AutoPillion;

/// <summary>
/// Immutable snapshot of the pillion settings, taken from <see cref="CharonConfig"/> each update.
/// <see cref="PillionManager"/> depends only on this record, keeping it free of Dalamud types.
/// </summary>
public sealed record PillionConfig(
    bool Enabled,
    float PillionDelaySeconds,
    float SeatTimeoutSeconds,
    bool LanMembersOnly)
{
    public TimeSpan PillionDelay => TimeSpan.FromSeconds(PillionDelaySeconds);
    public TimeSpan SeatTimeout => TimeSpan.FromSeconds(SeatTimeoutSeconds);

    public static PillionConfig From(CharonConfig config) => new(
        config.AutoPillionEnabled,
        config.PillionDelay,
        config.SeatTimeout,
        config.LanMembersOnly);
}
