using System;
using Mafi;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.PathFinding;
using Mafi.Core.PropertiesDb;
using Mafi.Core.Vehicles;
using Mafi.Serialization;

namespace ShippingPP.Terminals;

/// <summary>
/// The local cargo terminal entity: a vanilla <see cref="CargoDepot"/> with its own concrete type.
///
/// The distinct type is what lets the mod plug into the game's type-keyed extension points without
/// touching vanilla depots: the inspector manager picks the most-derived registered inspector
/// (so <see cref="LocalTerminalInspector"/> replaces the vanilla depot window for terminals only),
/// and later the shipping dispatcher keys its per-terminal state here. All depot behavior —
/// modules, cranes, fuel, docking, ocean reservation — is inherited unchanged.
/// </summary>
public class LocalTerminal : CargoDepot
{
    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((LocalTerminal)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((LocalTerminal)obj).DeserializeData(reader);

    public LocalTerminal(EntityId id, CargoDepotProto cargoDepotProto, TileTransform transform,
        EntityContext context, CargoDepotManager cargoDepotManager,
        IVehicleBuffersRegistry vehicleBuffersRegistry, ICargoShipFactory cargoShipFactory,
        EntitiesManager entitiesManager, IPropertiesDb propsDb,
        EntityCollapseHelper collapseHelper, ShipsClearancePathabilityProvider pathabilityProvider,
        StaticEntityOceanReservationManagerV2 reservationManager)
        : base(id, cargoDepotProto, transform, context, cargoDepotManager, vehicleBuffersRegistry,
            cargoShipFactory, entitiesManager, propsDb, collapseHelper, pathabilityProvider,
            reservationManager)
    {
    }

    public static void Serialize(LocalTerminal value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
    }

    public new static LocalTerminal Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out LocalTerminal obj,
            (Func<BlobReader, Type, LocalTerminal>)null,
            (Func<BlobReader, string, LocalTerminal>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
    }
}
