using System;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Entities.Validators;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Prototypes;
using Mafi.Localization;
using Mafi.Serialization;

namespace ElevationPP.Platforms;

/// <summary>
/// Proto of the concrete platform: a flat, one-tile-thick slab that stands on auto-built transport
/// pillars (elevatable exactly like the mod's elevated stations — raise it with the placement
/// height keys) and whose deck other buildings can be built on. The proto class is the type
/// switch every platform patch keys on; the entity is <see cref="ConcretePlatform"/>.
///
/// Elevation-capable so the vanilla pillar machinery serves it: the pillar tiles of its layout
/// (<see cref="LayoutTileConstraint.UsingPillar"/>) get transport pillars from the deck down to
/// the ground; the other deck tiles carry no constraint and float between them. Pillars of
/// higher structures (belts, pipes) do not pass through the deck — they stand on it.
/// </summary>
public class ConcretePlatformProto : LayoutEntityProto, ILayoutEntityProtoWithElevation
{
    public bool CanBeElevated => true;
    /// <summary>Pillars of other structures do NOT pass through the platform: a belt above the
    /// deck gets a pillar standing ON the deck instead (see <see cref="PlatformPillarPatch"/>).
    /// Side effect: vanilla skips the pillar re-check on removal for such protos, so the
    /// platform's own support pillars are handed to that re-check by the same patch.</summary>
    public bool CanPillarsPassThrough => false;

    public override Type EntityType => typeof(ConcretePlatform);

    /// <summary>Footprint size in tiles (square).</summary>
    public int Size { get; }

    public ConcretePlatformProto(ID id, Proto.Str strings, EntityLayout layout, EntityCosts costs,
        Gfx graphics, int size)
        : base(id, strings, layout, costs, graphics, cannotBeReflected: true)
    {
        Size = size;
    }
}

/// <summary>
/// The concrete platform entity. It has no behavior of its own — the base <see cref="LayoutEntity"/>
/// gives it construction, the renameable title and the standard inspector; the distinct type is
/// what <see cref="PlatformSupport"/> looks for under other buildings.
///
/// The one rule it enforces itself: it cannot be deconstructed while anything stands on its
/// deck (the vanilla remove tool asks <see cref="CanStartDeconstruction"/>). Removing the platform
/// from under a building would otherwise leave that building hanging in the air until the
/// terrain watchdog collapses it — remove the buildings first, then the platform. A COLLAPSE of
/// the platform (its pillars losing their ground) is not gated; the buildings on it are then
/// re-checked and collapse in turn, see <see cref="PlatformSupport"/>.
/// </summary>
public class ConcretePlatform : LayoutEntity
{
    private static readonly LocStr OCCUPIED_ERROR = Loc.Str("ElevationPP_PlatformOccupied",
        "Remove the buildings standing on the platform first.",
        "error shown when trying to remove a concrete platform that has buildings on it");

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((ConcretePlatform)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((ConcretePlatform)obj).DeserializeData(reader);

    public override bool CanBePaused => false;

    public ConcretePlatform(EntityId id, LayoutEntityProto proto, TileTransform transform,
        EntityContext context)
        : base(id, proto, transform, context)
    {
    }

    /// <summary>The height of the deck's top face: buildings on the platform stand at this height.
    /// (The deck token occupies exactly the tile at the platform's origin height.)</summary>
    public HeightTilesI DeckTopHeight => CenterTile.Height + ThicknessTilesI.One;

    /// <summary>True if any static entity occupies the tile row directly above the deck — i.e.
    /// something is built on (or runs across, or has a pillar standing on) the platform. The
    /// platform's own support pillars end at the deck bottom, so they never count.</summary>
    public bool HasAnythingOnDeck()
    {
        HeightTilesI top = DeckTopHeight;
        Tile2i origin = CenterTile.Xy;
        foreach (OccupiedTileRelative tile in OccupiedTiles)
        {
            if (Context.OccupancyManager.TryGetOccupyingEntityAt<IStaticEntity>(
                    (origin + tile.RelCoord).ExtendHeight(top), out IStaticEntity above)
                && !ReferenceEquals(above, this))
            {
                return true;
            }
        }
        return false;
    }

    public override EntityValidationResult CanStartDeconstruction()
    {
        if (HasAnythingOnDeck())
        {
            return EntityValidationResult.CreateError(OCCUPIED_ERROR.AsFormatted);
        }
        return base.CanStartDeconstruction();
    }

    public static void Serialize(ConcretePlatform value, BlobWriter writer)
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

    public static ConcretePlatform Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out ConcretePlatform obj,
            (Func<BlobReader, Type, ConcretePlatform>)null,
            (Func<BlobReader, string, ConcretePlatform>)null, nullObjIsOk: false))
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
