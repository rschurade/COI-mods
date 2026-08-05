using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;

namespace ShippingPP.Terminals;

/// <summary>
/// Registers the "Local cargo terminal": the smallest vanilla cargo depot (2 module slots)
/// re-purposed for local shipping. Model, layout, module slots, docking geometry, costs and ship
/// proto are all taken from the vanilla donor; only the proto class differs (see
/// <see cref="LocalTerminalProto"/>), which is what the mod's patches and dispatcher key on.
/// Sits next to the cargo depot in the toolbar and unlocks with the same research.
/// </summary>
internal class LocalTerminalData : IModData
{
    public void RegisterData(ProtoRegistrator registrator)
    {
        ProtosDb db = registrator.PrototypesDb;

        // Donor: the smallest cargo depot tier (T1, 2 module slots).
        CargoDepotProto donor = null;
        foreach (CargoDepotProto depot in db.All<CargoDepotProto>())
        {
            if (!(depot is LocalTerminalProto)
                && (donor == null || depot.ModuleSlots.Length < donor.ModuleSlots.Length))
            {
                donor = depot;
            }
        }
        if (donor == null)
        {
            Log.Warning("Shipping++: no cargo depot proto found; local terminal not registered.");
            return;
        }

        // The donor's assigned cargo ship proto id (private; the resolved CargoShipProto property
        // is not populated until proto initialization, which runs after mod registration).
        object shipIdObj = ProtoUtils.GetField(typeof(CargoDepotProto), donor, "m_cargoShipProtoId");
        if (!(shipIdObj is EntityProto.ID shipProtoId))
        {
            Log.Warning("Shipping++: donor cargo ship proto id not found; "
                + "local terminal not registered.");
            return;
        }

        var id = new CargoDepotProto.ID("ShippingPP_LocalTerminalT1");
        Proto.Str strings = Proto.CreateStr((StaticEntityProto.ID)id, "Local cargo terminal",
            "A cargo terminal for local shipping: its ship carries products to and from other "
            + "local cargo terminals on this island instead of trading with the world. Terminal "
            + "modules set to export offer their product to the network; modules set to import "
            + "request it. The terminal's ship is built on site from delivered materials.");

        // Toolbar: same category as the cargo depots, ordered right after the donor.
        ImmutableArray<ToolbarEntryData> categories;
        if (donor.Graphics.Categories.IsNotEmpty)
        {
            ToolbarEntryData entry = donor.Graphics.Categories[0];
            categories = ImmutableArray.Create(
                new ToolbarEntryData(entry.CategoryProto, false, (entry.Order ?? 100) + 1));
        }
        else
        {
            categories = donor.Graphics.Categories;
        }
        var gfx = (LayoutEntityProto.Gfx)ProtoUtils.CloneGfxWithCategory(
            donor.Graphics, ProtoUtils.VanillaIconPath(donor), categories);

        var proto = new LocalTerminalProto(id, strings, donor.Layout, donor.Costs,
            donor.ModuleSlots, donor.InterfaceRange, donor.ArriveDuration, donor.DepartDuration,
            donor.DockOffset, shipProtoId, gfx);
        ProtoUtils.AddGated(db, proto, ProtoUtils.FindUnlockingNode(db, donor));

        // The dock-direction arrow shown while placing the building (same as the vanilla depots).
        proto.AddParam(new DrawArrowWileBuildingProtoParam(6f));

        // Materials to build the terminal's ship. The ship proto itself has an empty cost
        // (vanilla cargo ships are salvaged, never built). Priced like the vanilla vehicles
        // (tiered vehicle parts + one material: Truck T2 = 30 VP2 + 30 Rubber, Excavator T2 =
        // 60 VP2 + 30 Steel) at the terminal's own tech era — the cargo depot is a CP2-tier
        // building, so no CP3 anywhere. Twice an Excavator T2, plus a steel hull.
        Mafi.Core.Products.ProductProto vehicleParts =
            db.Get<Mafi.Core.Products.ProductProto>(Mafi.Base.Ids.Products.VehicleParts2)
                .ValueOrNull;
        Mafi.Core.Products.ProductProto steel =
            db.Get<Mafi.Core.Products.ProductProto>(Mafi.Base.Ids.Products.Steel).ValueOrNull;
        if (vehicleParts != null && steel != null)
        {
            ShippingManager.ShipBuildCost = new Mafi.Core.Economy.AssetValue(
                vehicleParts.WithQuantity(120.Quantity()), steel.WithQuantity(100.Quantity()));
        }
        else
        {
            Log.Warning("Shipping++: VehicleParts2/Steel not found; ship build cost left empty.");
        }

        // The module product-picker patch needs the protos db to enumerate all products.
        ModulePickerPatch.ProtosDb = db;

        Log.Info($"Shipping++: registered '{id}' (donor '{donor.Id}', ship '{shipProtoId}', "
            + $"ship cost {ShippingManager.ShipBuildCost}).");
    }
}
