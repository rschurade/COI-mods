using System;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Buildings;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Buildings.Cargo.Ships.Modules;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.PathFinding;
using Mafi.Core.UiState;
using Mafi.Localization;
using Mafi.Serialization;
using ShippingPP.Terminals;

namespace ShippingPP.Ships;

/// <summary>
/// The brain of a locally-built cargo ship: haul products between local terminals, never leave
/// the map.
///
/// The cargo exchange itself is automatic — whenever a local ship is docked, the terminal's
/// modules load/unload every product both sides agree on (module direction × matching ship
/// module, see <see cref="LocalTerminalSim"/>). So this provider's whole job is routing: sail the
/// stops of the ship's assigned line in order, and at each one wait until the cranes go idle AND
/// the stop's departure rule is satisfied (see <see cref="Lines.StopRule"/>). A ship with no line
/// has no work — there is no automatic dispatch — and idles at its home anchor. Each departure
/// costs half a vanilla fuel journey; with insufficient fuel the ship stays docked (terminals
/// refuel docked ships from their fuel buffer).
///
/// Docks serve one ship at a time through the manager's per-dock queue: a ship may only
/// <c>NavigateToDock</c> when <see cref="ShippingManager.TryReserveDock"/> grants it the berth;
/// otherwise it HOLDS at an anchor point off the dock approach (spaced by queue position) and
/// retries. A docked ship yields its berth — moving to the anchor for free — when it has
/// nothing to do (or cannot proceed) while another ship is waiting for this dock, so fleets
/// larger than the dock count keep flowing instead of deadlocking.
/// </summary>
public class LocalShipJobProvider : ICargoShipJobProvider
{
    /// <summary>Version stamp of this provider's save data (bump when the format changes).</summary>
    private const int SAVE_VERSION = 6;

    /// <summary>Ticks of crane inactivity before the ship considers the exchange finished.</summary>
    private const int IDLE_SETTLE_TICKS = 30;
    /// <summary>Sim ticks per second, for turning a stop rule's timeout into ticks.</summary>
    private const int TICKS_PER_SECOND = 10;

    /// <summary>How close (tiles) a ship aims at / must get to a buoy waypoint. Generous because
    /// buoys occupy their tile and ships have large pathfinding clearance boxes.</summary>
    private const int WAYPOINT_TOLERANCE = 20;

    /// <summary>Holding anchor placement: first anchor this many tiles past the far edge of
    /// the dock's required ocean area, subsequent queue positions spaced a ship length
    /// further out.</summary>
    private const int ANCHOR_BASE_DIST = 14;
    private const int ANCHOR_GAP = 4;
    private const int ANCHOR_TOLERANCE = 10;

    private readonly CargoShipV2 m_ship;
    /// <summary>Current destination: a terminal (dock) or a navigation buoy (sail near).</summary>
    private StaticEntity m_target;
    private int m_idleTicks;
    /// <summary>Ticks spent docked waiting for a stop's departure rule; drives its timeout.
    /// </summary>
    private int m_waitedTicks;
    private bool m_lowFuel;
    /// <summary>Index of the line stop the ship is heading for (line-assigned ships only).</summary>
    private int m_lineStopIndex;
    /// <summary>Whether the fuel for the current leg (toward <see cref="m_target"/>) has been
    /// paid — legs can start with a hold at anchor and finish with the actual docking, and the
    /// fuel must be charged exactly once.</summary>
    private bool m_legFuelPaid;
    /// <summary>Sticky holding-anchor slot: assigned when the ship starts waiting for a dock
    /// and kept until it is granted the berth (or retargets). Without this, every queue shift
    /// would move ALL waiting ships one slot forward; with it, a freed dock moves exactly one
    /// ship and the rest ride their anchors unmoved. -1 = no slot.</summary>
    private int m_anchorSlot = -1;
    /// <summary>Entity id of the terminal the anchor slot belongs to (0 = none).</summary>
    private int m_anchorTerminalId;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((LocalShipJobProvider)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((LocalShipJobProvider)obj).DeserializeData(reader);

    public LocalShipJobProvider(CargoShipV2 ship)
    {
        m_ship = ship;
    }

    public bool IsValid()
    {
        return ShippingManager.IsLocalShip(m_ship);
    }

    /// <summary>Current target entity id, or null. Diagnostics only — see <see cref="Diag"/>.
    /// </summary>
    internal string TargetIdForDiag => m_target?.Id.ToString();

    /// <summary>Compact dump of the decision state. Diagnostics only — see <see cref="Diag"/>.
    /// </summary>
    internal string DebugState()
    {
        string target = "none";
        if (m_target != null)
        {
            target = m_target.IsDestroyed
                ? $"{m_target.Id}(destroyed)" : m_target.Id.ToString();
        }
        return $"target={target}, lineStop={m_lineStopIndex}, idleTicks={m_idleTicks}, "
            + $"fuelPaid={m_legFuelPaid}, lowFuel={m_lowFuel}, "
            + $"anchorSlot={m_anchorSlot}@{m_anchorTerminalId}";
    }

    public void SimUpdate()
    {
        if (m_ship.HasJobs)
        {
            return;
        }
        if (m_ship.IsAtWorld)
        {
            // A local ship must never be "at world"; recover it to the map edge near its home.
            m_ship.SetAtHomeAtMapEdge(m_ship.AssignedDockEntity);
            return;
        }
        ShippingManager manager = ShippingManager.Current;
        if (manager == null)
        {
            return;
        }

        // Sold: head for the map edge and stop taking part in anything. The manager removes the
        // ship once it is out at world; until then it must not hold a berth or a queue slot.
        if (manager.IsShipForSale(m_ship))
        {
            m_target = null;
            manager.ReleaseDockClaim(m_ship);
            // Only re-issue the departure while the ship still has somewhere to go. A ship that
            // has stopped at the map edge counts as gone and is removed by the manager's next
            // scan; asking it to leave again every tick would just churn navigation jobs.
            if (!ShippingManager.HasLeftTheMap(m_ship))
            {
                m_ship.LeaveToWorld();
            }
            return;
        }

        // A line is what puts a ship to work: there is no automatic dispatch to fall back on, so
        // a ship with no assignment (or one whose line has no usable route) is simply out of
        // service. It finishes nothing on its own, keeps no claims, and says so in its status
        // until the player assigns it to a line with at least two terminal stops.
        Lines.ShippingLine line = null;
        int? lineId = manager.GetLineIdFor(m_ship);
        if (lineId.HasValue)
        {
            line = manager.TryGetLine(lineId.Value);
            if (line != null && !line.HasUsableStops)
            {
                line = null;
            }
        }

        CargoDepot home = m_ship.AssignedDepot.ValueOrNull;
        if (!m_ship.IsDocked)
        {
            if (m_target != null && m_target.IsDestroyed)
            {
                m_target = null;
                m_legFuelPaid = false;
                m_anchorSlot = -1;
                m_anchorTerminalId = 0;
                manager.ReleaseDockClaim(m_ship);
            }
            // Buoy waypoint: arriving near it completes the leg; pick the next stop right away.
            if (m_target != null && m_target.Prototype is Lines.NavBuoyProto)
            {
                if (!isNearTile(m_target.Position2f.Tile2i, WAYPOINT_TOLERANCE + 8))
                {
                    navigateNear(m_target);
                    return;
                }
                m_target = null;
            }
            if (m_target == null && line != null)
            {
                stepAlongLine(line, null, manager);
                return;
            }
            // Sail to the current terminal target: dock when the berth is granted, hold at
            // anchor otherwise.
            if (m_target is CargoDepot targetTerminal)
            {
                if (targetTerminal.IsAccessBlocked)
                {
                    return;
                }
                if (manager.TryReserveDock(targetTerminal, m_ship))
                {
                    m_idleTicks = 0;
                    m_ship.NavigateToDock(targetTerminal);
                }
                else
                {
                    holdNear(targetTerminal, manager);
                }
                return;
            }
            // No target: idle on the water. Queue for the home berth only when it is actually
            // needed (cargo to unload, or fuel to top up) — an out-of-service ship parks at the
            // anchor instead of fighting a working sibling for the berth.
            if (home == null || home.IsDestroyed || home.IsAccessBlocked)
            {
                return;
            }
            if (!shipHasAnyCargo() && !m_lowFuel)
            {
                manager.ReleaseDockClaim(m_ship);
                holdNear(home, manager); // Park at the home anchor without claiming the berth.
                return;
            }
            if (manager.TryReserveDock(home, m_ship))
            {
                m_idleTicks = 0;
                m_ship.NavigateToDock(home);
            }
            else
            {
                holdNear(home, manager);
            }
            return;
        }

        CargoDepot dockedAt = m_ship.DockedAt.ValueOrNull as CargoDepot;
        if (dockedAt == null)
        {
            return;
        }
        // Docked: any paid leg has arrived (fuel is only paid while departing), and any
        // holding-anchor slot is given up.
        m_legFuelPaid = false;
        m_anchorSlot = -1;
        m_anchorTerminalId = 0;
        if (dockedAt == m_target)
        {
            m_target = null;
        }

        // "Depart now" from the ship window: the player overrules the stop's departure rule,
        // the crane transfer and the settle delay, exactly as it overrules a world ship's
        // wait-for-a-full-load. Consumed here because vanilla only clears the flag in
        // DepartToWorldForCargo, which a local ship never calls.
        bool departNow = m_ship.DepartureRequestedByPlayer;
        if (departNow)
        {
            clearDepartureRequest();
        }

        // Let the cranes finish (and settle) before any departure decision.
        if (!departNow)
        {
            if (isExchangeRunning(dockedAt))
            {
                m_idleTicks = 0;
                return;
            }
            if (++m_idleTicks < IDLE_SETTLE_TICKS)
            {
                return;
            }
        }

        // Dry at the berth: hold it until this terminal fills the tank. Leaving is impossible
        // (there is no fuel to pay a leg with) and yielding the berth would only move the ship
        // away from the one place its fuel can come from — so the ship deliberately blocks the
        // dock, and its status says which fuel to deliver. Claims elsewhere are dropped: a stuck
        // ship must not sit in another terminal's queue (or on its berth promise) meanwhile.
        if (!canPayLegFuel())
        {
            m_lowFuel = true;
            m_target = null;
            manager.ReleaseDockClaim(m_ship);
            return;
        }

        // Line mode: cycle the assigned line's stops.
        if (line != null)
        {
            // The stop's departure rule may hold the ship here even though the cranes have gone
            // idle — that idleness is exactly the "terminal is full / has nothing" case the rule
            // exists for. The wait timer runs while docked and releases the ship regardless, so
            // an unsatisfiable stop cannot strand it or block the berth behind it.
            if (!departNow && !mayDepartFrom(line, dockedAt))
            {
                m_waitedTicks++;
                return;
            }
            m_waitedTicks = 0;
            stepAlongLine(line, dockedAt, manager);
            return;
        }

        if (home != null && dockedAt != home)
        {
            // Visit finished — sail home (where the fetched cargo gets unloaded).
            if (home.IsDestroyed)
            {
                // Orphaned at a foreign berth: don't block it forever — yield to any waiting
                // ship and idle at the anchor until the player assigns a new home port.
                if (manager.DockHasWaiters(dockedAt, m_ship))
                {
                    holdNear(dockedAt, manager);
                }
                return;
            }
            if (home.IsAccessBlocked)
            {
                return;
            }
            if (manager.TryReserveDock(home, m_ship))
            {
                if (tryPayLegFuel())
                {
                    m_idleTicks = 0;
                    m_ship.NavigateToDock(home);
                }
            }
            else if (tryPayLegFuel())
            {
                // Home berth busy: clear this berth anyway and wait at home's anchor.
                m_target = home;
                m_idleTicks = 0;
                holdNear(home, manager);
            }
            return;
        }

        // At home with no line to serve: out of service. Yield the berth if another ship wants
        // it (a free hop) and park at the anchor — the idle logic only re-docks when there is a
        // reason to, so out-of-service siblings never ping-pong the berth between them.
        manager.ReleaseDockClaim(m_ship);
        if (manager.DockHasWaiters(dockedAt, m_ship))
        {
            m_idleTicks = 0;
            holdNear(dockedAt, manager);
        }
    }

    /// <summary>
    /// Whether the stop the ship is docked at lets it go yet. The rule belongs to the line stop
    /// the ship is serving: <see cref="m_lineStopIndex"/> already points at the NEXT stop, so the
    /// current one is the entry before it, and any other occurrence of this terminal on the line
    /// is a fallback for a ship that docked out of sequence.
    /// </summary>
    private bool mayDepartFrom(Lines.ShippingLine line, CargoDepot dockedAt)
    {
        int index = -1;
        int previous = m_lineStopIndex - 1;
        if (previous < 0)
        {
            previous = line.StopCount - 1;
        }
        if (line.StopAtOrNull(previous) == dockedAt)
        {
            index = previous;
        }
        else
        {
            for (int i = 0; i < line.StopCount; i++)
            {
                if (line.StopAtOrNull(i) == dockedAt)
                {
                    index = i;
                    break;
                }
            }
        }
        if (index < 0)
        {
            return true; // Not a stop of this line: nothing to wait for.
        }
        Lines.StopRule rule = line.RuleAt(index);
        if (!rule.HasWait)
        {
            return true;
        }
        if (rule.TimeoutSec > 0 && m_waitedTicks >= rule.TimeoutSec * TICKS_PER_SECOND)
        {
            return true;
        }
        return rule.IsSatisfiedAt(cargoPercent());
    }

    /// <summary>The ship's total cargo as a percentage of its total module capacity.</summary>
    private int cargoPercent()
    {
        Quantity stored = Quantity.Zero;
        Quantity capacity = Quantity.Zero;
        for (int i = 0; i < m_ship.Modules.Count; i++)
        {
            CargoShipModule module = m_ship.Modules[i].ValueOrNull;
            if (module != null)
            {
                stored += module.Quantity;
                capacity += module.Capacity;
            }
        }
        return capacity.IsPositive ? stored.Value * 100 / capacity.Value : 100;
    }

    private bool shipHasAnyCargo()
    {
        for (int i = 0; i < m_ship.Modules.Count; i++)
        {
            if (m_ship.Modules[i].HasValue && m_ship.Modules[i].Value.Quantity.IsPositive)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Pays the leg fuel once per leg (no-op if this leg is already paid).</summary>
    private bool tryPayLegFuel()
    {
        if (m_legFuelPaid)
        {
            return true;
        }
        if (tryConsumeLegFuel())
        {
            m_legFuelPaid = true;
            return true;
        }
        return false;
    }

    /// <summary>Sails to (or waits at) the holding anchor off the terminal's dock approach,
    /// spaced by the ship's queue position. The seaward direction is derived from the dock's
    /// own required ocean area (docking position → area center → beyond its far edge), which
    /// is correct for every building rotation — waiting ships ride at anchor in a line out on
    /// the open water. Near the anchor the ship idles; the caller retries the dock
    /// reservation every sim step.</summary>
    private void holdNear(CargoDepot terminal, ShippingManager manager)
    {
        if (m_anchorSlot < 0 || m_anchorTerminalId != terminal.Id.Value)
        {
            int queueIndex = manager.GetQueueIndex(terminal, m_ship);
            // Ships that could not even join the (full) queue park in the outermost slot.
            m_anchorSlot = queueIndex >= 0 ? queueIndex : 3;
            m_anchorTerminalId = terminal.Id.Value;
        }
        int index = m_anchorSlot;
        Tile2f dockPosition = terminal.GetShipDockingPosition();
        Tile2f areaCenter = terminal.OceanAreaRequired.CenterCoordF;
        float dx = (areaCenter.X - dockPosition.X).ToFloat();
        float dy = (areaCenter.Y - dockPosition.Y).ToFloat();
        float length = (float)Math.Sqrt(dx * dx + dy * dy);
        if (length < 1f)
        {
            // Degenerate area; fall back to the building's outward axis.
            Vector2f outward = terminal.Transform.TransformMatrix.Transform(new Vector2f(-1, 0));
            dx = outward.X.ToFloat();
            dy = outward.Y.ToFloat();
            length = 1f;
        }
        // Spacing scales with the ship's own pathfinding clearance so large ships (up to
        // 61x23 tiles for the 8-module tier) don't overlap their neighbors at anchor.
        float shipLength = 31f;
        if (m_ship.Prototype is CargoShipProto shipProto)
        {
            shipLength = shipProto.PathFindingParams.RequiredClearance.X;
        }
        float distance = 2f * length + ANCHOR_BASE_DIST + shipLength / 2f
            + (shipLength + ANCHOR_GAP) * index;
        var anchor = new Tile2f(
            Fix32.FromFloat(dockPosition.X.ToFloat() + dx / length * distance),
            Fix32.FromFloat(dockPosition.Y.ToFloat() + dy / length * distance));
        Tile2i anchorTile = ShipsClearancePathabilityProvider.GetFineChunkCornerTile(
            anchor.Tile2i);
        if (isNearTile(anchorTile, ANCHOR_TOLERANCE + 8))
        {
            return; // Riding at anchor; keep retrying the reservation.
        }
        var goal = m_ship.JobsContext.VehicleGoalsFactory.CreateGoal(anchorTile,
            ANCHOR_TOLERANCE.Tiles(), isShip: true);
        m_ship.JobsContext.NavigateToJobFactory.EnqueueJob(m_ship, goal);
    }

    /// <summary>Emergency refuel run: sail to the home dock free of charge (a ship without leg
    /// fuel can only refuel while docked; without this, a freshly built or dry line ship would
    /// be stranded at sea forever).</summary>
    private void sailHomeToRefuel(ShippingManager manager)
    {
        CargoDepot home = m_ship.AssignedDepot.ValueOrNull;
        if (home == null || home.IsDestroyed || home.IsAccessBlocked)
        {
            return;
        }
        if (manager.TryReserveDock(home, m_ship))
        {
            m_idleTicks = 0;
            m_ship.NavigateToDock(home);
        }
        else
        {
            holdNear(home, manager);
        }
    }

    /// <summary>Advances to the next live line stop (terminal or buoy) that is not the current
    /// dock and heads there — terminals require a berth grant (or the ship holds at the dock's
    /// anchor) and leg fuel, buoys are free fly-bys.</summary>
    private void stepAlongLine(Lines.ShippingLine line, CargoDepot dockedAt,
        ShippingManager manager)
    {
        StaticEntity target = null;
        for (int attempts = 0; attempts < line.StopCount; attempts++)
        {
            if (m_lineStopIndex >= line.StopCount)
            {
                m_lineStopIndex = 0;
            }
            StaticEntity stop = line.StopAtOrNull(m_lineStopIndex);
            if (stop == null || stop.IsDestroyed || (dockedAt != null && stop == dockedAt))
            {
                m_lineStopIndex++;
                continue;
            }
            target = stop;
            break;
        }
        if (target == null)
        {
            return;
        }
        if (target.Prototype is Lines.NavBuoyProto)
        {
            m_target = target;
            m_idleTicks = 0;
            m_lineStopIndex++;
            navigateNear(target);
            return;
        }
        var terminal = target as CargoDepot;
        if (terminal == null)
        {
            m_lineStopIndex++;
            return;
        }
        if (manager.TryReserveDock(terminal, m_ship))
        {
            if (tryPayLegFuel())
            {
                m_target = terminal;
                m_idleTicks = 0;
                m_lineStopIndex++;
                m_ship.NavigateToDock(terminal);
            }
            else if (dockedAt == null)
            {
                // Dry tank at sea. The emergency refuel run is free of charge, so when the
                // berth just granted IS the home berth, sail in and refuel there instead of
                // giving the claim back: releasing it re-queues the ship at the BACK of its own
                // home queue, and with a whole fleet dry every ship in turn would be granted the
                // berth, forfeit it and drop to the tail — a livelock in which the dock stays
                // empty and nobody ever reaches the fuel.
                if (m_ship.AssignedDepot.ValueOrNull == terminal)
                {
                    m_target = terminal;
                    m_idleTicks = 0;
                    m_lineStopIndex++;
                    m_ship.NavigateToDock(terminal);
                }
                else
                {
                    manager.ReleaseDockClaim(m_ship);
                    sailHomeToRefuel(manager);
                }
            }
            return;
        }
        // Berth busy: sail to its holding anchor and queue there (finished stops should not
        // loiter on their berth waiting for the next one). With a dry tank at sea, run home
        // to refuel instead. The stop index is only advanced once the berth is actually
        // granted (the arrival skips the current stop).
        if (tryPayLegFuel())
        {
            m_target = terminal;
            m_idleTicks = 0;
            holdNear(terminal, manager);
        }
        else if (dockedAt == null)
        {
            sailHomeToRefuel(manager);
        }
    }

    /// <summary>Sails toward a point near the buoy (goal snapped to the ship pathfinder's 4-tile
    /// grid, generous tolerance so the buoy's own footprint never blocks arrival).</summary>
    private void navigateNear(StaticEntity buoy)
    {
        Tile2i tile = ShipsClearancePathabilityProvider.GetFineChunkCornerTile(
            buoy.Position2f.Tile2i);
        var goal = m_ship.JobsContext.VehicleGoalsFactory.CreateGoal(tile,
            WAYPOINT_TOLERANCE.Tiles(), isShip: true);
        m_ship.JobsContext.NavigateToJobFactory.EnqueueJob(m_ship, goal);
    }

    private bool isNearTile(Tile2i tile, int reach)
    {
        Tile2i shipTile = m_ship.Position2f.Tile2i;
        int dx = shipTile.X - tile.X;
        int dy = shipTile.Y - tile.Y;
        return dx * dx + dy * dy <= reach * reach;
    }

    private bool isExchangeRunning(CargoDepot terminal)
    {
        foreach (Option<CargoDepotModule> slot in terminal.Modules)
        {
            if (slot.HasValue && slot.Value.IsMovingCargo())
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Charges half a vanilla fuel journey for the upcoming leg; false (and a status flag) when
    /// the tank does not cover it.
    /// </summary>
    /// <summary>Fuel the next leg costs, or false when this ship's fuel type charges nothing.
    /// </summary>
    private bool tryGetLegFuelNeeded(out Quantity needed)
    {
        needed = Quantity.Zero;
        CargoShipProto.FuelData fuelData = null;
        foreach (CargoShipProto.FuelData candidate in m_ship.Prototype.AvailableFuels)
        {
            if (candidate.FuelProto == m_ship.FuelProto)
            {
                fuelData = candidate;
                break;
            }
        }
        if (fuelData == null)
        {
            return false;
        }
        int nonEmptyModules = 0;
        for (int i = 0; i < m_ship.Modules.Count; i++)
        {
            if (m_ship.Modules[i].HasValue && m_ship.Modules[i].Value.Quantity.IsPositive)
            {
                nonEmptyModules++;
            }
        }
        needed = new Quantity((fuelData.FuelPerJourneyBase.Value
            + fuelData.FuelPerJourneyPerModule.Value * nonEmptyModules) / 2);
        return true;
    }

    /// <summary>Whether the tank covers the next leg, WITHOUT charging for it.</summary>
    private bool canPayLegFuel()
    {
        return m_legFuelPaid || !tryGetLegFuelNeeded(out Quantity needed)
            || m_ship.FuelBuffer.Quantity >= needed;
    }

    private bool tryConsumeLegFuel()
    {
        if (!tryGetLegFuelNeeded(out Quantity needed))
        {
            m_lowFuel = false;
            return true;
        }
        if (m_ship.FuelBuffer.Quantity < needed)
        {
            m_lowFuel = true;
            return false;
        }
        object buffer = ProtoUtils.GetField(typeof(CargoShipV2), m_ship, "m_fuelBuffer");
        if (buffer is Mafi.Core.Entities.Static.ProductBuffer fuelBuffer)
        {
            fuelBuffer.RemoveAsMuchAs(needed);
        }
        m_lowFuel = false;
        return true;
    }

    /// <summary>
    /// Whether the ship window's "depart now" button is offered. A docked local ship may always
    /// be sent on its way early — that is the manual override for a stop rule that is waiting
    /// for cargo which is not coming — provided it has a route to sail and the fuel to do it.
    /// </summary>
    public bool IsDepartNowAvailable(out LocStrFormatted reason)
    {
        reason = Mafi.Core.Tr.CargoShipCannotDepartNow__General.AsFormatted;
        ShippingManager manager = ShippingManager.Current;
        if (manager == null || !m_ship.IsDocked || manager.IsShipForSale(m_ship))
        {
            return false;
        }
        if (m_ship.DepartureRequestedByPlayer)
        {
            reason = Mafi.Core.Tr.CargoShipCannotDepartNow__WasRequested.AsFormatted;
            return false;
        }
        int? lineId = manager.GetLineIdFor(m_ship);
        Lines.ShippingLine line = lineId.HasValue ? manager.TryGetLine(lineId.Value) : null;
        if (line == null || !line.HasUsableStops)
        {
            reason = Txt.ShipStatus_LineUnusable;
            return false;
        }
        if (!canPayLegFuel())
        {
            reason = Txt.ShipStatus_LowFuel;
            return false;
        }
        reason = LocStrFormatted.Empty;
        return true;
    }

    /// <summary>Consumes the player's departure request. The flag has a private setter and
    /// vanilla only clears it when a ship leaves for the world map, which a local ship never
    /// does — left set, it would make the button read "Already requested" forever.</summary>
    private void clearDepartureRequest()
    {
        ProtoUtils.SetField(typeof(CargoShipV2), m_ship,
            "<DepartureRequestedByPlayer>k__BackingField", false);
    }

    public bool CanDepart(out LocStrFormatted reason)
    {
        reason = LocStrFormatted.Empty;
        return false;
    }

    public void Destroy()
    {
    }

    public LocStrFormatted GetShipStatus(out StateForUi state)
    {
        if (ShippingManager.IsShipOrphaned(m_ship)
            && ShippingManager.Current?.GetLineIdFor(m_ship) == null)
        {
            state = StateForUi.Danger;
            return Txt.ShipStatus_Orphaned;
        }
        if (m_ship.HasJobs)
        {
            state = StateForUi.Positive;
            return m_ship.CurrentJob.Value.JobInfo;
        }
        if (m_lowFuel)
        {
            state = StateForUi.Warning;
            // Docked and dry: the ship is holding this berth on purpose, so say so and name the
            // fuel — otherwise a blocked dock looks like the mod has hung.
            return m_ship.IsDocked
                ? Txt.ShipStatus_WaitingForFuel(m_ship.FuelProto.Strings.Name.AsFormatted.Value)
                : Txt.ShipStatus_LowFuel;
        }
        state = StateForUi.Positive;
        if (m_ship.IsDocked && m_ship.DockedAt.ValueOrNull is CargoDepot dockedAt
            && isExchangeRunning(dockedAt))
        {
            return Txt.ShipStatus_TransferringCargo;
        }
        if (!m_ship.IsDocked && m_target is CargoDepot waitingFor)
        {
            int queueIndex = ShippingManager.Current?.GetQueueIndex(waitingFor, m_ship) ?? -1;
            if (queueIndex > 0)
            {
                return Txt.ShipStatus_WaitingForBerth(waitingFor.GetTitle());
            }
        }
        int? lineId = ShippingManager.Current?.GetLineIdFor(m_ship);
        if (lineId.HasValue)
        {
            Lines.ShippingLine line = ShippingManager.Current?.TryGetLine(lineId.Value);
            if (line == null || !line.HasUsableStops)
            {
                state = StateForUi.Warning;
                return Txt.ShipStatus_LineUnusable;
            }
            return Txt.ShipStatus_OnLine(line.Name);
        }
        // No line, no work: there is no automatic dispatch to fall back on.
        state = StateForUi.Warning;
        return Txt.ShipStatus_Idle;
    }

    public static void Serialize(LocalShipJobProvider value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected virtual void SerializeData(BlobWriter writer)
    {
        writer.WriteInt(SAVE_VERSION);
        CargoShipV2.Serialize(m_ship, writer);
        writer.WriteBool(m_target != null);
        if (m_target != null)
        {
            writer.WriteGeneric(m_target);
        }
        writer.WriteInt(m_idleTicks);
        writer.WriteBool(m_lowFuel);
        writer.WriteInt(m_lineStopIndex);
        writer.WriteBool(m_legFuelPaid);
        writer.WriteInt(m_anchorSlot);
        writer.WriteInt(m_anchorTerminalId);
        writer.WriteInt(m_waitedTicks);
    }

    public static LocalShipJobProvider Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out LocalShipJobProvider obj,
            (Func<BlobReader, Type, LocalShipJobProvider>)null,
            (Func<BlobReader, string, LocalShipJobProvider>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected virtual void DeserializeData(BlobReader reader)
    {
        int version = reader.ReadInt();
        reader.SetField(this, "m_ship", CargoShipV2.Deserialize(reader));
        if (reader.ReadBool())
        {
            // v2 and older stored the target with the CargoDepot serializer directly.
            m_target = (version >= 3)
                ? reader.ReadGenericAs<StaticEntity>()
                : CargoDepot.Deserialize(reader);
        }
        m_idleTicks = reader.ReadInt();
        m_lowFuel = reader.ReadBool();
        if (version >= 2)
        {
            m_lineStopIndex = reader.ReadInt();
        }
        if (version >= 4)
        {
            m_legFuelPaid = reader.ReadBool();
        }
        else
        {
            // Pre-queue saves: a ship already sailing toward a terminal has paid its leg.
            m_legFuelPaid = m_target is CargoDepot;
        }
        if (version >= 5)
        {
            m_anchorSlot = reader.ReadInt();
            m_anchorTerminalId = reader.ReadInt();
        }
        else
        {
            m_anchorSlot = -1;
            m_anchorTerminalId = 0;
        }
        m_waitedTicks = version >= 6 ? reader.ReadInt() : 0;
    }
}
