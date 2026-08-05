using System;
using System.Reflection;
using Mafi;
using Mafi.Core;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Economy;
using Mafi.Core.Entities.Animations;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Core.Terrain;
using Mafi.Core.Trains;
using Mafi.Base.Prototypes.Trains;

namespace ElevationPP.Stations;

/// <summary>
/// Elevated variant of <see cref="TrainStationModuleProto"/>. It reuses the full vanilla station
/// configuration (model, costs, capacity, products, …) but is built from an <em>elevated</em> track
/// trajectory and implements <see cref="ITrainTrackMayBeElevatedProto"/> so the train-track graph
/// treats it as elevatable and auto-builds pillars under it.
///
/// This is a standalone elevatable station (no ground/elevated proto pairing), so
/// <see cref="ElevationFlippedProto"/> is <c>None</c> and <see cref="SelectBetweenElevatedAndGround"/>
/// always returns itself.
/// </summary>
public class ElevatedStationModuleProto : TrainStationModuleProto, ITrainTrackMayBeElevatedProto, ILayoutEntityProtoWithElevation
{
    public override Type EntityType => typeof(ElevatedStationModule);

    public Option<ITrainTrackMayBeElevatedProto> ElevationFlippedProto => Option<ITrainTrackMayBeElevatedProto>.None;

    // ILayoutEntityProtoWithElevation — drives the transport-pillar support under the building
    // footprint (UsingPillar tiles), exactly like balancers/lifts. The track itself is supported
    // separately via the train-track pillar system (ITrainTrackMayBeElevatedProto above).
    public bool CanBeElevated => true;
    public bool CanPillarsPassThrough => true;

    public ElevatedStationModuleProto(ID id, Proto.Str strings, EntityLayout layout, EntityCosts costs,
        TrainTrackTrajectoryData trajectoryData, ProductType productType, Quantity capacity,
        Duration transferPeriod, Quantity transferQuantity, Percent connectionCompletionPerStep,
        Electricity powerConsumption, ImmutableArray<AnimationParams> animationParams,
        TrainStationModuleProto.Gfx graphics, RelTile1f? maxSpeedTilesPerTick,
        Duration? constructionDurationPerProduct, Upoints? boostCost, bool cannotBeReflected,
        Predicate<ProductProto> isProductSupportedOverride, bool isElectrified)
        : base(id, strings, layout, costs, trajectoryData, productType, capacity, transferPeriod,
            transferQuantity, connectionCompletionPerStep, powerConsumption, animationParams, graphics,
            maxSpeedTilesPerTick, constructionDurationPerProduct, boostCost, cannotBeReflected,
            isProductSupportedOverride, isElectrified)
    {
        // The station rests on its own footprint pillars, not on train-track pillars, so exempt
        // its track from TrainTracksCollapseHelper's "missing support -> collapse" check (the only
        // thing that reads IgnoreMissingSupport). It is a get-only auto-property set by the base
        // constructor to false; flip its backing field to true here.
        FieldInfo backing = typeof(EntityWithTrainTrackBaseProto).GetField(
            "<IgnoreMissingSupport>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        if (backing != null)
        {
            backing.SetValue(this, true);
        }
        else
        {
            Log.Error("Elevation++: IgnoreMissingSupport backing field not found; elevated station may collapse.");
        }
    }

    protected override void OnInitialize(ProtosDb protosDb)
    {
        base.OnInitialize(protosDb);
        // Gfx.Initialize just rebuilt AnimationDataAssetPathBase from OUR mod's asset root, which
        // is the "TODO" placeholder — restore the source proto's path so the baked semi-instanced
        // animations (e.g. the fluid module's extending loading pipe) are found and play.
        ElevatedStationData.FixAnimationDataPath(this);
    }

    public T SelectBetweenElevatedAndGround<T>(TerrainManager terrainManager, TileTransform transform)
        where T : class, ITrainTrackMayBeElevatedProto
    {
        return this as T;
    }
}
