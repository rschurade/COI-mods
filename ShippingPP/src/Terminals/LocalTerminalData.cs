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
        CargoDepotProto previousDonor = null;
        for (int tier = 1; tier <= donors.Count; tier++)
        {
            CargoDepotProto donor = donors[tier - 1];
            LocalTerminalProto proto = registerTier(db, donor, tier);
            if (proto == null)
            {
                continue;
            }
            // Chain the tiers so the toolbar groups them into one entry with a tier popup —
            // mirroring the donors' own topology, so the vanilla in-place upgrade button appears
            // exactly where the vanilla depots have one (2→4 and 6→8 slots are direct upgrades
            // with an identical footprint; 4→6 is indirect only because the quay doubles in
            // length there). The upgrade is safe for the fleet bookkeeping: CargoDepot's
            // TryReplaceSelf swaps the proto on the SAME entity (id preserved) and re-slots the
            // existing module entities, so queues, berth grants, home ports, line stops and
            // module directions all carry over.
            if (previous != null)
            {
                if (ReferenceEquals(previousDonor.Upgrade.NextTier.ValueOrNull, donor))
                {
                    previous.SetNextTier(proto);
                }
                else
                {
                    previous.SetNextTierIndirect(proto);
                }
            }
            // Same one-way rules as the vanilla depots: never downgrade, never move.
            proto.Upgrade.SetCannotDowngrade();
            proto.Upgrade.SetCannotMove();
            previous = proto;
            previousDonor = donor;
        }

        // The module product-picker patch needs the protos db to enumerate all products.
        ModulePickerPatch.ProtosDb = db;

        // The "ship has no home port" warning, raised for ships whose home terminal was
        // destroyed (they can't take jobs or refuel until re-homed via the ship window).
        new Mafi.Core.Notifications.NotificationProtoBuilder(registrator)
            .Start(ModTranslations.Text("ShippingPP__Notif_ShipHasNoHomePort",
                    "Cargo ship has no home port"),
                ShippingManager.ShipHasNoHomeNotifId)
            .SetType(Mafi.Core.Notifications.NotificationType.Continuous)
            .SetStyle(Mafi.Core.Notifications.NotificationStyle.Critical)
            .AddIcon("Assets/Unity/UserInterface/Toolbar/CargoShip.svg")
            .AddEntityIcon("Assets/Unity/UserInterface/EntityIcons/Blocked.png")
            .BuildAndAdd();
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
        // One translatable pattern for all four tiers; the slot count is filled in per tier.
        Proto.Str strings = Proto.CreateStr((StaticEntityProto.ID)id,
            ModTranslations.Fmt(ModTranslations.Text("ShippingPP__LocalTerminal_Name_Fmt",
                "Local cargo terminal ({0})"), slots).Value,
            ModTranslations.Fmt(ModTranslations.Text("ShippingPP__LocalTerminal_Desc_Fmt",
                "A cargo terminal with {0} module slots for local shipping: its ships carry "
                + "products to and from other local cargo terminals on this island instead of "
                + "trading with the world. Terminal modules set to export offer their product to "
                + "the network; modules set to import request it. Ships are built on site from "
                + "delivered materials and match the terminal's size ({0} cargo modules); any "
                + "local ship can dock here regardless of its size."), slots).Value);

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
