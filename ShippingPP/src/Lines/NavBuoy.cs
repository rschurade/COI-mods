using System;
using Mafi;
using Mafi.Base.Prototypes.Buildings;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Economy;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Serialization;
using ShippingPP.Terminals;

namespace ShippingPP.Lines;

/// <summary>
/// Proto of the navigation buoy: a tiny, functionless building placed on ocean tiles that marks
/// a route point for shipping lines. This proto class is the type switch the line system keys
/// on; the entity is the mod's <see cref="NavBuoy"/>. Ships never touch it — they sail NEAR it
/// (tolerance goal), and a buoy does not block ship pathing beyond its own tile.
/// </summary>
public class NavBuoyProto : BarrierProto
{
    public override bool ExcludeFromGlobalSearch => false;

    /// <summary>The buoy gets its own entity class (instead of the sealed vanilla
    /// <c>BarrierEntity</c>) so it is renameable and gets its own inspector: the entity base
    /// implements the custom-title interface, and the inspector manager and the vanilla
    /// rename command both key on the entity type.</summary>
    public override Type EntityType => typeof(NavBuoy);

    public NavBuoyProto(ID id, Proto.Str strings, EntityLayout layout, EntityCosts costs,
        Gfx graphics)
        : base(id, strings, layout, costs, graphics)
    {
    }
}

/// <summary>
/// The navigation buoy entity: no behavior of its own — the base <see cref="LayoutEntity"/>
/// carries the renameable custom title (serialized with the save, applied by the vanilla
/// <c>SetEntityNameCmd</c>), and the distinct type binds <see cref="NavBuoyInspector"/>.
/// Buoys placed before this class existed are plain barrier entities: they keep working as
/// line stops but cannot be renamed (re-place them, buoys are free).
/// </summary>
public class NavBuoy : LayoutEntity
{
    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((NavBuoy)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((NavBuoy)obj).DeserializeData(reader);

    public override bool CanBePaused => false;

    public NavBuoy(EntityId id, LayoutEntityProto proto, TileTransform transform,
        EntityContext context)
        : base(id, proto, transform, context)
    {
    }

    public static void Serialize(NavBuoy value, BlobWriter writer)
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

    public static NavBuoy Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out NavBuoy obj,
            (Func<BlobReader, Type, NavBuoy>)null,
            (Func<BlobReader, string, NavBuoy>)null, nullObjIsOk: false))
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

/// <summary>Registers the navigation buoy (procedural buoy model, 1x1 ocean tile, free).</summary>
internal class NavBuoyData : IModData
{
    public const string BUOY_PROTO_ID = "ShippingPP_NavBuoy";

    public void RegisterData(ProtoRegistrator registrator)
    {
        ProtosDb db = registrator.PrototypesDb;

        var id = new StaticEntityProto.ID(BUOY_PROTO_ID);
        Proto.Str strings = Proto.CreateStr(id,
            ModTranslations.Text("ShippingPP__NavBuoy_Name", "Navigation buoy"),
            ModTranslations.Text("ShippingPP__NavBuoy_Desc",
                "A route marker for shipping lines: add it as a stop in the shipping lines "
                + "manager and line ships will sail past it on their way to the next stop — "
                + "useful to route ships around islands or through wide channels. Ships aim "
                + "near the buoy, not at it."));

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

        // The model is the procedural buoy injected into the AssetsDb by NavBuoyModel; the icon
        // is rendered from that same model at game init (with the vanilla beacon icon as
        // fallback if rendering is unavailable). GameObject rendering (not instanced) is the
        // path the injected template supports.
        var gfx = new LayoutEntityProto.Gfx(NavBuoyModel.PREFAB_PATH,
            default(RelTile3f), NavBuoyModel.ICON_PATH.SomeOption(), default(Mafi.ColorRgba),
            hideBlockedPortsIcon: false, null, categories, useInstancedRendering: false);

        // 1x1 ocean-only footprint, 2 tiles of own volume (the default "~2~" ocean token).
        EntityLayout layout = registrator.LayoutParser.ParseLayoutOrThrow("~2~");

        // Free: a route marker should cost nothing to place or move around.
        EntityCosts costs = new EntityCosts(AssetValue.Empty);

        var proto = new NavBuoyProto(id, strings, layout, costs, gfx);
        ProtoUtils.AddGated(db, proto,
            terminal != null ? ProtoUtils.FindUnlockingNode(db, terminal) : null);
        Log.Info($"Shipping++: registered '{id}'.");
    }
}
