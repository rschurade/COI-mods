using System;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Buildings;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Buildings.Cargo.Ships;
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
/// there, let the exchange run, and sail home. Each departure costs half a vanilla fuel journey;
/// with insufficient fuel the ship stays docked (terminals refuel docked ships from their fuel
/// buffer).
/// </summary>
public class LocalShipJobProvider : ICargoShipJobProvider
{
    /// <summary>Version stamp of this provider's save data (bump when the format changes).</summary>
    private const int SAVE_VERSION = 3;

    /// <summary>Ticks of crane inactivity before the ship considers the exchange finished.</summary>
    private const int IDLE_SETTLE_TICKS = 30;

    /// <summary>How close (tiles) a ship aims at / must get to a buoy waypoint. Generous because
    /// buoys occupy their tile and ships have large pathfinding clearance boxes.</summary>
    private const int WAYPOINT_TOLERANCE = 20;

    private readonly CargoShipV2 m_ship;
    /// <summary>Current destination: a terminal (dock) or a navigation buoy (sail near).</summary>
    private StaticEntity m_target;
    private int m_idleTicks;
    private bool m_lowFuel;
    /// <summary>Index of the line stop the ship is heading for (line-assigned ships only).</summary>
    private int m_lineStopIndex;

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
            }
            // Buoy waypoint: arriving near it completes the leg; pick the next stop right away.
            if (m_target != null && m_target.Prototype is Lines.NavBuoyProto)
            {
                if (!isNearTarget())
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
            // Resume toward the current terminal target, else head home.
            CargoDepot destination = (m_target as CargoDepot)
                ?? (home != null && !home.IsDestroyed ? home : null);
            if (destination != null && !destination.IsAccessBlocked)
            {
                m_ship.NavigateToDock(destination);
            }
            return;
        }

        CargoDepot dockedAt = m_ship.DockedAt.ValueOrNull as CargoDepot;
        if (dockedAt == null)
        {
            return;
        }
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
            if (!home.IsDestroyed && !home.IsAccessBlocked && tryConsumeLegFuel())
            {
                m_idleTicks = 0;
                m_ship.NavigateToDock(home);
            }
            return;
        }

        // At home (or home is gone): ask the dispatcher for the next worthwhile trip.
        CargoDepot next = manager.FindTradeTargetFor(m_ship);
        if (next != null && tryConsumeLegFuel())
        {
            m_target = next;
            m_idleTicks = 0;
            m_ship.NavigateToDock(next);
        }
    }

    /// <summary>Advances to the next live line stop (terminal or buoy) that is not the current
    /// dock and heads there — terminals require a dock reservation and leg fuel, buoys are free
    /// fly-bys (waiting at the current position otherwise).</summary>
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
        if (manager.TryReserveDock(terminal, m_ship) && tryConsumeLegFuel())
        {
            m_target = terminal;
            m_idleTicks = 0;
            m_lineStopIndex++;
            m_ship.NavigateToDock(terminal);
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

    private bool isNearTarget()
    {
        Tile2i shipTile = m_ship.Position2f.Tile2i;
        Tile2i targetTile = m_target.Position2f.Tile2i;
        int dx = shipTile.X - targetTile.X;
        int dy = shipTile.Y - targetTile.Y;
        int reach = WAYPOINT_TOLERANCE + 8;
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
    }
}
