using System.Collections.Generic;
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
/// Registers the "Local cargo terminal" tiers: one clone per cargo depot — the vanilla tiers
/// (2/4/6/8 module slots) and any added by other mods — re-purposed for local shipping. Which id
/// each terminal gets is pinned per save by <see cref="TerminalIdsConfig"/>. Model, layout, module slots, docking
/// geometry, costs and ship proto are all taken from the vanilla donor; only the proto class
/// differs (see <see cref="LocalTerminalProto"/>), which is what the mod's patches and dispatcher
/// key on. Each tier sits next to its donor in the toolbar and unlocks with the same research.
/// Bigger terminals build bigger ships (the donor's ship proto: 2/4/6/8 cargo modules).
/// </summary>
internal class LocalTerminalData : IModData
{
    /// <summary>The terminal id map of the game being built; set by the mod's constructor, which
    /// receives it from the save (or fresh for a new game / an older save).</summary>
    internal static TerminalIdsConfig IdsConfig;

    /// <summary>The map whose entries <see cref="registerTier"/> indexes into.</summary>
    private static TerminalIdsConfig s_registeringIds;

    /// <summary>The terminal cloned from the 2-slot vanilla depot (the navigation buoy sits next
    /// to it in the toolbar and shares its research).</summary>
    internal static LocalTerminalProto SmallestVanillaTerminal { get; private set; }

    private static readonly CargoDepotProto.ID[] VANILLA_DEPOT_IDS =
    {
        Mafi.Base.Ids.Buildings.CargoDepotT1, Mafi.Base.Ids.Buildings.CargoDepotT2,
        Mafi.Base.Ids.Buildings.CargoDepotT3, Mafi.Base.Ids.Buildings.CargoDepotT4,
    };

    public void RegisterData(ProtoRegistrator registrator)
    {
        ProtosDb db = registrator.PrototypesDb;
        SmallestVanillaTerminal = null;

        // Every cargo depot (vanilla or from another mod) gets one local terminal.
        var donors = new Lyst<CargoDepotProto>();
        foreach (CargoDepotProto depot in db.All<CargoDepotProto>())
        {
            if (!(depot is LocalTerminalProto))
            {
                donors.Add(depot);
            }
        }
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

        // Terminal ids are saved with the entities, so an id must mean the same donor for the
        // whole life of a save, whatever depot mods are installed. The save carries the
        // id -> donor map (see TerminalIdsConfig); only donors not in it yet get a new id.
        TerminalIdsConfig ids = IdsConfig ?? new TerminalIdsConfig();
        assignIds(ids, donors);

        var terminalByDonor = new Dict<CargoDepotProto, LocalTerminalProto>();
        for (int index = 0; index < ids.Entries.Count; index++)
        {
            KeyValuePair<string, string> entry = ids.Entries[index];
            CargoDepotProto donor = db.Get<CargoDepotProto>(new CargoDepotProto.ID(entry.Value))
                .ValueOrNull;
            if (donor == null || donor is LocalTerminalProto)
            {
                // Kept in the map: the id comes back with its depot if the depot's mod returns.
                Log.Warning($"Shipping++: cargo depot '{entry.Value}' of local terminal "
                    + $"'{entry.Key}' is not installed; that terminal is not registered.");
                continue;
            }
            if (terminalByDonor.ContainsKey(donor))
            {
                Log.Warning($"Shipping++: '{entry.Key}' maps to depot '{entry.Value}', which "
                    + "already has a local terminal; skipped.");
                continue;
            }
            s_registeringIds = ids;
            LocalTerminalProto proto = registerTier(db, donor, index);
            s_registeringIds = null;
            if (proto == null)
            {
                continue;
            }
            // Same one-way rules as the vanilla depots: never downgrade, never move.
            proto.Upgrade.SetCannotDowngrade();
            proto.Upgrade.SetCannotMove();
            terminalByDonor[donor] = proto;
        }

        // Chain the terminals so the toolbar groups them into one entry with a tier popup —
        // mirroring the donors' own topology, so the vanilla in-place upgrade button appears
        // exactly where the depots have one. The upgrade is safe for the fleet bookkeeping:
        // CargoDepot's TryReplaceSelf swaps the proto on the SAME entity (id preserved) and
        // re-slots the existing module entities, so queues, berth grants, home ports, line stops
        // and module directions all carry over.
        //
        // The vanilla tiers form one fixed chain, smallest first: 2→4 and 6→8 slots are direct
        // upgrades with an identical footprint; 4→6 is indirect only because the quay doubles in
        // length there.
        LocalTerminalProto previous = null;
        CargoDepotProto previousDonor = null;
        foreach (CargoDepotProto.ID vanillaId in VANILLA_DEPOT_IDS)
        {
            CargoDepotProto donor = db.Get<CargoDepotProto>(vanillaId).ValueOrNull;
            if (donor == null || !terminalByDonor.TryGetValue(donor, out LocalTerminalProto proto))
            {
                continue;
            }
            if (previous == null)
            {
                SmallestVanillaTerminal = proto;
            }
            else if (ReferenceEquals(previousDonor.Upgrade.NextTier.ValueOrNull, donor))
            {
                previous.SetNextTier(proto);
            }
            else
            {
                previous.SetNextTierIndirect(proto);
            }
            previous = proto;
            previousDonor = donor;
        }
        // Other mods' depots are chained among themselves exactly like their donors are (never
        // into the vanilla tiers, whose chain is fixed above).
        foreach (KeyValuePair<CargoDepotProto, LocalTerminalProto> pair in terminalByDonor)
        {
            CargoDepotProto donor = pair.Key;
            if (isVanilla(donor))
            {
                continue;
            }
            if (donor.Upgrade.NextTier.ValueOrNull is CargoDepotProto direct && !isVanilla(direct)
                && terminalByDonor.TryGetValue(direct, out LocalTerminalProto directNext))
            {
                pair.Value.SetNextTier(directNext);
            }
            else if (donor.TierData.NextTierIndirect.ValueOrNull is CargoDepotProto indirect
                && !isVanilla(indirect)
                && terminalByDonor.TryGetValue(indirect, out LocalTerminalProto indirectNext))
            {
                pair.Value.SetNextTierIndirect(indirectNext);
            }
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

    /// <summary>
    /// Fills in the terminal id of every donor that has none yet. A map that is empty and not
    /// from a save (new game, or a save from 0.6.4 or older) is first filled exactly the way
    /// those versions numbered the terminals — all depots sorted by slot count, T1..Tn — which
    /// keeps every old save meaning what it meant (a save with only the vanilla depots gets
    /// T1..T4 = 2/4/6/8 slots, as always). From then on the map is frozen in the save, and depots
    /// that appear later get an id derived from their own donor id.
    /// </summary>
    private static void assignIds(TerminalIdsConfig ids, Lyst<CargoDepotProto> donors)
    {
        if (!ids.IsFromSave && ids.Entries.IsEmpty)
        {
            // Must stay identical to the 0.6.4 code: same donor list, same sort call.
            var legacy = new Lyst<CargoDepotProto>(donors);
            legacy.Sort((a, b) => a.ModuleSlots.Length.CompareTo(b.ModuleSlots.Length));
            for (int tier = 1; tier <= legacy.Count; tier++)
            {
                ids.Entries.Add(Make.Kvp($"ShippingPP_LocalTerminalT{tier}",
                    legacy[tier - 1].Id.Value));
            }
            Log.Info("Shipping++: no local terminal id map in this game yet; numbered the "
                + "terminals the way 0.6.4 and older did.");
        }

        foreach (CargoDepotProto donor in donors)
        {
            string donorId = donor.Id.Value;
            if (ids.Entries.Any(e => e.Value == donorId))
            {
                continue;
            }
            string id = $"ShippingPP_LocalTerminal_{donorId}";
            for (int n = 2; ids.Entries.Any(e => e.Key == id); n++)
            {
                id = $"ShippingPP_LocalTerminal_{donorId}_{n}";
            }
            ids.Entries.Add(Make.Kvp(id, donorId));
            Log.Info($"Shipping++: new cargo depot '{donorId}' gets local terminal id '{id}'.");
        }

        foreach (KeyValuePair<string, string> entry in ids.Entries)
        {
            Log.Info($"Shipping++: local terminal id '{entry.Key}' = depot '{entry.Value}'.");
        }
    }

    private static bool isVanilla(CargoDepotProto donor)
    {
        return System.Array.IndexOf(VANILLA_DEPOT_IDS, donor.Id) >= 0;
    }

    /// <summary>
    /// Registers the terminal of one donor under the id at <paramref name="entryIndex"/> of the
    /// id map being registered. Returns null if the terminal is not registered.
    /// </summary>
    /// <remarks>
    /// Other mods hook this method by name and exact signature (ProtosDb, CargoDepotProto, int)
    /// with a Harmony prefix to keep their depots out of Shipping++ — Beacon &amp; Government
    /// excludes its Regional Export Yards this way, letting the prefix return false so this
    /// returns null. Keep the name and the parameter types stable.
    /// </remarks>
    private static LocalTerminalProto registerTier(ProtosDb db, CargoDepotProto donor,
        int entryIndex)
    {
        var id = new CargoDepotProto.ID(s_registeringIds.Entries[entryIndex].Key);
        // The donor's assigned cargo ship proto id (private; the resolved CargoShipProto property
        // is not populated until proto initialization, which runs after mod registration).
        object shipIdObj = ProtoUtils.GetField(typeof(CargoDepotProto), donor, "m_cargoShipProtoId");
        if (!(shipIdObj is EntityProto.ID shipProtoId))
        {
            Log.Warning($"Shipping++: cargo ship proto id of donor '{donor.Id}' not found; "
                + "local terminal tier not registered.");
            return null;
        }

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
