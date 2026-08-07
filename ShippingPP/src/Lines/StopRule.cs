namespace ShippingPP.Lines;

/// <summary>Which cargo level a ship waits for at a stop before it may depart.</summary>
public enum StopWait : byte
{
    /// <summary>Leave as soon as the transfer goes idle — the behaviour of every stop before
    /// departure rules existed, and still the default.</summary>
    None = 0,
    /// <summary>Stay until the ship is loaded to at least the rule's percentage (export stop).
    /// </summary>
    LoadTo = 1,
    /// <summary>Stay until the ship is unloaded to at most the rule's percentage (import stop).
    /// </summary>
    UnloadTo = 2,
}

/// <summary>
/// When a ship may leave a line stop.
///
/// Leaving as soon as the cranes stop is wrong at both ends of a route: at a full import terminal
/// a ship unloads nothing and leaves still laden, at an empty export terminal it loads nothing and
/// leaves empty — then burns fuel bouncing between the two. A rule holds the ship until its cargo
/// reaches the level the stop is there to achieve.
///
/// The direction is explicit rather than inferred from the ship's cargo, because a terminal can
/// carry both import and export modules and the same percentage would then be ambiguous.
///
/// <see cref="TimeoutSec"/> is the escape hatch: a ship that has waited that long departs at
/// whatever level it reached, so a stop that never fills up cannot strand a ship — or block the
/// berth behind it — forever.
/// </summary>
public readonly struct StopRule
{
    public readonly StopWait Mode;

    /// <summary>Cargo level to load to / unload to, in percent of the ship's total capacity.
    /// </summary>
    public readonly int Percent;

    /// <summary>Seconds to wait at most before departing regardless, or 0 for no limit.</summary>
    public readonly int TimeoutSec;

    public StopRule(StopWait mode, int percent, int timeoutSec)
    {
        Mode = mode;
        Percent = percent < 0 ? 0 : (percent > 100 ? 100 : percent);
        TimeoutSec = timeoutSec < 0 ? 0 : timeoutSec;
    }

    public static StopRule Default => new StopRule(StopWait.None, 0, 0);

    public bool HasWait => Mode != StopWait.None;

    /// <summary>Whether a ship at this cargo level (percent of its total capacity) may depart.
    /// Only consulted once the transfer has gone idle, so "not satisfied" means the terminal
    /// cannot currently give or take any more.</summary>
    public bool IsSatisfiedAt(int cargoPercent)
    {
        switch (Mode)
        {
            case StopWait.LoadTo:
                return cargoPercent >= Percent;
            case StopWait.UnloadTo:
                return cargoPercent <= Percent;
            default:
                return true;
        }
    }
}
