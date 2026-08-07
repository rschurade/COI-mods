using Mafi;
using Mafi.Collections;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Ships;

namespace ShippingPP;

/// <summary>
/// Berth and dispatch diagnostics, compiled in on demand.
///
/// Flip <see cref="ENABLED"/> to true and rebuild to trace why ships do or do not get a berth:
/// every dock reservation decision is logged (with the blocking ship named), plus a periodic
/// dump of each terminal's queue and each ship's decision state. Both log only when the state
/// CHANGES, so a stalled fleet shows up as its last state followed by silence.
///
/// Guard every call site with <c>if (Diag.ENABLED)</c> so the message building — the string
/// interpolation, and <see cref="DescribeShip"/>'s reflection — only happens when the switch is
/// on. The JIT folds a static readonly bool into a constant and drops the whole block, so a
/// shipped build pays nothing for them.
///
/// This found the dry-fleet livelock at the home berth (ships were granted the berth, forfeited
/// it to "run home for fuel", and dropped to the back of their own home queue).
/// </summary>
internal static class Diag
{
    /// <summary>The switch. False in shipped builds — flip it and rebuild.
    ///
    /// Deliberately <c>static readonly</c> rather than <c>const</c>: a const false makes every
    /// guarded block unreachable at compile time and buries the build in CS0162 warnings. The
    /// JIT eliminates the branch either way.</summary>
    internal static readonly bool ENABLED = false;

    /// <summary>Scan ticks between state samples (the manager scans every 30 ticks).</summary>
    internal const int PERIOD_SCANS = 4;
    /// <summary>Unchanged samples between "still stuck" heartbeats.</summary>
    internal const int HEARTBEAT_SAMPLES = 25;
    /// <summary>Cap on full state dumps per session (state CHANGES, not samples).</summary>
    internal const int MAX_DUMPS = 60;

    /// <summary>Last reservation outcome per ship, so the log gets one line per CHANGE instead
    /// of one per tick. Static on purpose: a manager restored from a save skips field
    /// initializers, and a static field is immune to that.</summary>
    private static readonly Dict<CargoShipV2, string> s_lastDecision =
        new Dict<CargoShipV2, string>();

    private static bool s_announced;

    internal static void Write(string message)
    {
        Log.Info("Shipping++ [diag] " + message);
    }

    /// <summary>One "diagnostics are compiled in" marker per session, so a log that lacks it
    /// immediately says the build is not the one you think it is.</summary>
    internal static void Announce()
    {
        if (s_announced)
        {
            return;
        }
        s_announced = true;
        Write("berth diagnostics active — every dock reservation decision is logged on change.");
    }

    /// <summary>Logs a dock reservation outcome, once per change of outcome per ship.</summary>
    internal static void DockDecision(CargoDepot terminal, CargoShipV2 ship, string outcome)
    {
        string current = terminal.Id + "|" + outcome;
        if (s_lastDecision.TryGetValue(ship, out string previous) && previous == current)
        {
            return;
        }
        s_lastDecision[ship] = current;
        Write($"ship {ship.Id} -> terminal {terminal.Id}: {outcome}");
    }

    /// <summary>Everything about a ship that bears on whether it can take a berth. The job is
    /// the decisive field: a ship holding an unfinished navigation job never ticks its provider
    /// again (SimUpdate bails on HasJobs), so it can never retry the dock.</summary>
    internal static string DescribeShip(CargoShipV2 ship, string line)
    {
        if (ship == null)
        {
            return "null";
        }
        if (ship.IsDestroyed)
        {
            return $"ship {ship.Id} DESTROYED";
        }
        string docked = ship.DockedAt.HasValue ? ship.DockedAt.Value.Id.ToString() : "none";
        string home = ship.AssignedDepot.HasValue
            ? ship.AssignedDepot.Value.Id.ToString() : "none";
        string job = "none";
        if (ship.CurrentJob.HasValue)
        {
            Mafi.Core.Vehicles.Jobs.IVehicleJobReadOnly current = ship.CurrentJob.Value;
            job = $"{current.GetType().Name}#{current.Id} '{current.JobInfo.Value}'";
        }
        Ships.LocalShipJobProvider provider = Ships.LocalShipProviderPatch.TryGetProviderOf(ship);
        string state = provider != null ? provider.DebugState() : "NOT A LOCAL PROVIDER";
        return $"ship {ship.Id} (line={line}, docked={docked}, home={home}, jobs={ship.HasJobs}, "
            + $"trueJob={ship.HasTrueJob}, navFails={ship.NavigationFailedStreak}, "
            + $"atWorld={ship.IsAtWorld}, job={job}, {state})";
    }

    /// <summary>Signature fragment for a ship: only decisions count as a change, so a ship
    /// idling at anchor (with ticking counters) does not look like progress.</summary>
    internal static string ShipSignature(CargoShipV2 ship)
    {
        if (ship.IsDestroyed)
        {
            return $"S{ship.Id}:dead;";
        }
        Ships.LocalShipJobProvider provider = Ships.LocalShipProviderPatch.TryGetProviderOf(ship);
        return $"S{ship.Id}:"
            + $"{(ship.DockedAt.HasValue ? ship.DockedAt.Value.Id.ToString() : "-")}/"
            + $"{ship.HasJobs}/{ship.CurrentJob.HasValue}/{provider?.TargetIdForDiag ?? "-"};";
    }
}
