using Mafi;
using Mafi.Base;
using Mafi.Base.Prototypes.Buildings;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Economy;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Mods;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using ShippingPP.Terminals;

namespace ShippingPP.Lines;

/// <summary>
/// Proto of the navigation buoy: a tiny, functionless building placed on ocean tiles that marks
/// a route point for shipping lines. The entity is the vanilla <see cref="BarrierEntity"/> (no
/// behavior, no power, no workers, existing serializers); this proto class is the type switch
/// the line system keys on. Ships never touch it — they sail NEAR it (tolerance goal), and a
/// buoy does not block ship pathing beyond its own tile.
/// </summary>
public class NavBuoyProto : BarrierProto
{
    public override bool ExcludeFromGlobalSearch => false;

    public NavBuoyProto(ID id, Proto.Str strings, EntityLayout layout, EntityCosts costs,
        Gfx graphics)
        : base(id, strings, layout, costs, graphics)
    {
    }
}

/// <summary>Registers the navigation buoy (beacon mast model, 1x1 ocean tile, cheap).</summary>
internal class NavBuoyData : IModData
{
    public const string BUOY_PROTO_ID = "ShippingPP_NavBuoy";

    public void RegisterData(ProtoRegistrator registrator)
    {
        ProtosDb db = registrator.PrototypesDb;

        var id = new StaticEntityProto.ID(BUOY_PROTO_ID);
        Proto.Str strings = Proto.CreateStr(id, "Navigation buoy",
            "A route marker for shipping lines: add it as a stop in the shipping lines manager "
            + "and line ships will sail past it on their way to the next stop — useful to route "
            + "ships around islands or through wide channels. Ships aim near the buoy, not at "
            + "it.");

        // Toolbar: next to the local cargo terminal; unlocks with the same research.
        var terminal = db.Get<LocalTerminalProto>(
            new StaticEntityProto.ID("ShippingPP_LocalTerminalT1")).ValueOrNull;
        ImmutableArray<ToolbarEntryData> categories;
        if (terminal != null && terminal.Graphics.Categories.IsNotEmpty)
        {
            ToolbarEntryData entry = terminal.Graphics.Categories[0];
            categories = ImmutableArray.Create(
                new ToolbarEntryData(entry.CategoryProto, false, (entry.Order ?? 100) + 1));
        }
        else
        {
            categories = ImmutableArray<ToolbarEntryData>.Empty;
        }

        // The vanilla beacon's mast model and generated icon.
        Option<string> icon = Option<string>.None;
        StaticEntityProto beacon = db.Get<StaticEntityProto>(
            new StaticEntityProto.ID("Beacon")).ValueOrNull;
        if (beacon != null)
        {
            icon = ProtoUtils.VanillaIconPath(beacon).SomeOption();
        }
        var gfx = new LayoutEntityProto.Gfx("Assets/Base/Buildings/Beacon.prefab",
            default(RelTile3f), icon, default(Mafi.ColorRgba), hideBlockedPortsIcon: false,
            null, categories, useInstancedRendering: true);

        // 1x1 ocean-only footprint, 2 tiles of own volume (the default "~2~" ocean token).
        EntityLayout layout = registrator.LayoutParser.ParseLayoutOrThrow("~2~");

        // Cheap: a handful of construction parts.
        ProductProto cp = db.Get<ProductProto>(Ids.Products.ConstructionParts).ValueOrNull;
        EntityCosts costs = cp != null
            ? new EntityCosts(new AssetValue(cp.WithQuantity(10)))
            : new EntityCosts(AssetValue.Empty);

        var proto = new NavBuoyProto(id, strings, layout, costs, gfx);
        ProtoUtils.AddGated(db, proto,
            terminal != null ? ProtoUtils.FindUnlockingNode(db, terminal) : null);
        Log.Info($"Shipping++: registered '{id}'.");
    }
}
