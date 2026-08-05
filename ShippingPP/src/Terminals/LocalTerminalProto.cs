using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Prototypes;

namespace ShippingPP.Terminals;

/// <summary>
/// Proto of the local cargo terminal: a cargo depot whose ship serves OTHER terminals on the same
/// map instead of the world map.
///
/// The entity type stays the vanilla <see cref="CargoDepot"/> (inherited from
/// <see cref="CargoDepotProto"/>), so the whole depot machinery — module slots and their placement
/// validator, the crane/pump product exchange, the fuel buffer, docking geometry, ocean-area
/// reservation, inspectors — is reused unchanged. This proto class is the type switch the mod's
/// patches and the shipping dispatcher key on.
/// </summary>
public class LocalTerminalProto : CargoDepotProto
{
    /// <summary>The terminal gets its own entity subclass so the mod's inspector (and later the
    /// dispatcher) bind to terminals without affecting vanilla depots.</summary>
    public override System.Type EntityType => typeof(LocalTerminal);

    public LocalTerminalProto(ID id, Proto.Str strings, EntityLayout layout, EntityCosts costs,
        ImmutableArray<ModuleSlotPosition> moduleSlots, RelTile1f interfaceRange,
        Duration arriveDuration, Duration departDuration, RelTile2f dockOffset,
        EntityProto.ID cargoShipProtoId, Gfx graphics)
        : base(id, strings, layout, costs, moduleSlots, interfaceRange, arriveDuration,
            departDuration, dockOffset, cargoShipProtoId, graphics)
    {
    }
}
