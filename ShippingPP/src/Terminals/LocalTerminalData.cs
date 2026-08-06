using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;

namespace ShippingPP.Terminals;

/// <summary>
/// Registers the "Local cargo terminal" tiers: one clone per vanilla cargo depot tier
/// (2/4/6/8 module slots), re-purposed for local shipping. Model, layout, module slots, docking
/// geometry, costs and ship proto are all taken from the vanilla donor; only the proto class
/// differs (see <see cref="LocalTerminalProto"/>), which is what the mod's patches and dispatcher
/// key on. Each tier sits next to its donor in the toolbar and unlocks with the same research.
/// Bigger terminals build bigger ships (the donor's ship proto: 2/4/6/8 cargo modules).
/// </summary>
internal class LocalTerminalData : IModData
{
    public void RegisterData(ProtoRegistrator registrator)
    {
        ProtosDb db = registrator.PrototypesDb;

        // All vanilla depot tiers, smallest first (T1 = 2 slots ... T4 = 8 slots).
        var donors = new Lyst<CargoDepotProto>();
        foreach (CargoDepotProto depot in db.All<CargoDepotProto>())
        {
            if (!(depot is LocalTerminalProto))
            {
                donors.Add(depot);
            }
        }
        donors.Sort((a, b) => a.ModuleSlots.Length.CompareTo(b.ModuleSlots.Length));
        if (donors.IsEmpty)
        {
            Log.Warning("Shipping++: no cargo depot protos found; local terminals not registered.");
            return;
        }

        // Materials to build one MODULE of a terminal's ship (a 2-module tier-1 ship costs
        // 120 VP2 + 100 Steel, the 8-module flagship four times that). The ship proto itself
        // has an empty cost (vanilla cargo ships are salvaged, never built). Priced like the
        // vanilla vehicles (tiered vehicle parts + one material: Truck T2 = 30 VP2 + 30 Rubber,
        // Excavator T2 = 60 VP2 + 30 Steel) at the terminal's own tech era — the cargo depot is
        // a CP2-tier building, so no CP3 anywhere.
        Mafi.Core.Products.ProductProto vehicleParts =
            db.Get<Mafi.Core.Products.ProductProto>(Mafi.Base.Ids.Products.VehicleParts2)
                .ValueOrNull;
        Mafi.Core.Products.ProductProto steel =
            db.Get<Mafi.Core.Products.ProductProto>(Mafi.Base.Ids.Products.Steel).ValueOrNull;
        if (vehicleParts != null && steel != null)
        {
            ShippingManager.ShipBuildCostPerModule = new Mafi.Core.Economy.AssetValue(
                vehicleParts.WithQuantity(60.Quantity()), steel.WithQuantity(50.Quantity()));
        }
        else
        {
            Log.Warning("Shipping++: VehicleParts2/Steel not found; ship build cost left empty.");
        }

        LocalTerminalProto previous = null;
        for (int tier = 1; tier <= donors.Count; tier++)
        {
            LocalTerminalProto proto = registerTier(db, donors[tier - 1], tier);
            if (proto == null)
            {
                continue;
            }
            // Chain the tiers so the toolbar groups them into one entry with a tier popup,
            // exactly like the vanilla cargo depots. Indirect only — no in-place upgrade
            // (an upgrade would replace the terminal entity under the fleet's bookkeeping).
            previous?.SetNextTierIndirect(proto);
            previous = proto;
        }

        // The module product-picker patch needs the protos db to enumerate all products.
        ModulePickerPatch.ProtosDb = db;
    }

    private static LocalTerminalProto registerTier(ProtosDb db, CargoDepotProto donor, int tier)
    {
        // The donor's assigned cargo ship proto id (private; the resolved CargoShipProto property
        // is not populated until proto initialization, which runs after mod registration).
        object shipIdObj = ProtoUtils.GetField(typeof(CargoDepotProto), donor, "m_cargoShipProtoId");
        if (!(shipIdObj is EntityProto.ID shipProtoId))
        {
            Log.Warning($"Shipping++: cargo ship proto id of donor '{donor.Id}' not found; "
                + "local terminal tier not registered.");
            return null;
        }

        // T1 keeps the id the mod has used since its first release (save compatibility).
        var id = new CargoDepotProto.ID($"ShippingPP_LocalTerminalT{tier}");
        int slots = donor.ModuleSlots.Length;
        Proto.Str strings = Proto.CreateStr((StaticEntityProto.ID)id,
            $"Local cargo terminal ({slots})",
            $"A cargo terminal with {slots} module slots for local shipping: its ships carry "
            + "products to and from other local cargo terminals on this island instead of trading "
            + "with the world. Terminal modules set to export offer their product to the network; "
            + "modules set to import request it. Ships are built on site from delivered materials "
            + $"and match the terminal's size ({slots} cargo modules); any local ship can dock "
            + "here regardless of its size.");

        // Toolbar: same category as the cargo depots, each tier ordered right after its donor.
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

        Log.Info($"Shipping++: registered '{id}' (donor '{donor.Id}', ship '{shipProtoId}').");
        return proto;
    }
}
