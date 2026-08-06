using System;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Buildings;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Buildings.Cargo.Ships;
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
/// module, see <see cref="LocalTerminalSim"/>). So this provider's whole job is routing: wait at
/// a dock until the cranes go idle, then — when at home — ask the dispatcher for the most
/// valuable terminal to visit (deliver what the ship carries, fetch what home requests), sail
/// there, let the exchange run, and sail home. Each trade departure costs half a vanilla fuel
/// journey; with insufficient fuel the ship stays docked (terminals refuel docked ships from
/// their fuel buffer).
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
    private const int SAVE_VERSION = 5;

    /// <summary>Ticks of crane inactivity before the ship considers the exchange finished.</summary>
    private const int IDLE_SETTLE_TICKS = 30;

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
            // No target: idle on the water. Take a trade job directly from the anchor; queue
            // for the home berth only when it is actually needed (cargo to unload, fuel to
            // top up, or already queued) — an empty idle ship parks at the anchor instead of
            // fighting its docked sibling for the berth.
            if (home == null || home.IsDestroyed || home.IsAccessBlocked)
            {
                return;
            }
            bool needsBerth = shipHasAnyCargo() || m_lowFuel
                || manager.GetQueueIndex(home, m_ship) >= 0;
            if (!needsBerth)
            {
                CargoDepot job = manager.FindTradeTargetFor(m_ship);
                if (job != null)
                {
                    if (!tryConsumeLegFuel())
                    {
                        manager.ReleaseDockClaim(m_ship);
                        return;
                    }
                    m_target = job;
                    m_legFuelPaid = true;
                    m_idleTicks = 0;
                    if (manager.TryReserveDock(job, m_ship))
                    {
                        m_ship.NavigateToDock(job);
                    }
                    else
                    {
                        holdNear(job, manager);
                    }
                    return;
                }
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

        // Let the cranes finish (and settle) before any departure decision.
        if (isExchangeRunning(dockedAt))
        {
            m_idleTicks = 0;
            return;
        }
        if (++m_idleTicks < IDLE_SETTLE_TICKS)
        {
            return;
        }

        // Line mode: cycle the assigned line's stops; the network dispatcher is not consulted.
        if (line != null)
        {
            stepAlongLine(line, dockedAt, manager);
            return;
        }

        if (home != null && dockedAt != home)
        {
            // Visit finished — sail home (where the fetched cargo gets unloaded).
            if (home.IsDestroyed || home.IsAccessBlocked)
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

        // At home (or home is gone): ask the dispatcher for the next worthwhile trip.
        CargoDepot next = manager.FindTradeTargetFor(m_ship);
        if (next != null)
        {
            if (!tryConsumeLegFuel())
            {
                manager.ReleaseDockClaim(m_ship);
                return;
            }
            m_target = next;
            m_legFuelPaid = true;
            m_idleTicks = 0;
            if (manager.TryReserveDock(next, m_ship))
            {
                m_ship.NavigateToDock(next);
            }
            else
            {
                holdNear(next, manager);
            }
        }
        else if (manager.DockHasWaiters(dockedAt, m_ship))
        {
            // Nothing to do while another ship waits for this berth: yield it (free hop) and
            // become an idle parker at the anchor — the idle logic re-docks or re-dispatches
            // the ship only when there is a reason to, so idle siblings never ping-pong the
            // berth between them.
            m_idleTicks = 0;
            holdNear(dockedAt, manager);
        }
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
                // Dry tank at sea: give the berth back and run home for fuel.
                manager.ReleaseDockClaim(m_ship);
                sailHomeToRefuel(manager);
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
    private bool tryConsumeLegFuel()
    {
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
            m_lowFuel = false;
            return true;
        }
        int nonEmptyModules = 0;
        for (int i = 0; i < m_ship.Modules.Count; i++)
        {
            if (m_ship.Modules[i].HasValue && m_ship.Modules[i].Value.Quantity.IsPositive)
            {
                nonEmptyModules++;
            }
        }
        var needed = new Quantity((fuelData.FuelPerJourneyBase.Value
            + fuelData.FuelPerJourneyPerModule.Value * nonEmptyModules) / 2);
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

    public bool IsDepartNowAvailable(out LocStrFormatted reason)
    {
        reason = LocStrFormatted.Empty;
        return false;
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
        if (m_ship.HasJobs)
        {
            state = StateForUi.Positive;
            return m_ship.CurrentJob.Value.JobInfo;
        }
        if (m_lowFuel)
        {
            state = StateForUi.Warning;
            return "Not enough fuel for the next trip".AsLoc();
        }
        state = StateForUi.Positive;
        if (m_ship.IsDocked && m_ship.DockedAt.ValueOrNull is CargoDepot dockedAt
            && isExchangeRunning(dockedAt))
        {
            return "Transferring cargo".AsLoc();
        }
        if (!m_ship.IsDocked && m_target is CargoDepot waitingFor)
        {
            int queueIndex = ShippingManager.Current?.GetQueueIndex(waitingFor, m_ship) ?? -1;
            if (queueIndex > 0)
            {
                return $"Waiting for a free berth at {waitingFor.GetTitle()}".AsLoc();
            }
        }
        int? lineId = ShippingManager.Current?.GetLineIdFor(m_ship);
        if (lineId.HasValue)
        {
            return $"On line {lineId.Value}".AsLoc();
        }
        return "Serving local terminals".AsLoc();
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
    }
}
