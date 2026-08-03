using System;
using System.Reflection;
using Mafi;
using Mafi.Core;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core.Economy;
using Mafi.Core.Entities.Animations;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Prototypes;
using Mafi.Core.Trains;
using Mafi.Base.Prototypes.Trains;

namespace ElevationPP.Stations;

/// <summary>
/// Elevated variant of <see cref="TrainStationFuelProto"/> (coal / liquid / nuclear fuel modules).
/// Its entity <c>TrainStationFuel</c> is sealed, so — like the root — this proto keeps the vanilla
/// entity and elevates purely as a layout entity on its footprint pillars via
/// <see cref="ILayoutEntityProtoWithElevation"/> (it is not a track-elevation friend, which is safe
/// because every friend access in the engine is guarded by an <c>is</c> check).
///
/// The allowed fuels are not constructor arguments; the registrar copies them from the source proto
/// with <c>AddFuel</c> after construction (a fuel station with no fuels fails to initialize).
/// </summary>
public class ElevatedStationFuelProto : TrainStationFuelProto, ILayoutEntityProtoWithElevation
{
    public bool CanBeElevated => true;
    public bool CanPillarsPassThrough => true;

    public ElevatedStationFuelProto(ID id, Proto.Str strings, EntityLayout layout, EntityCosts costs,
        TrainTrackTrajectoryData trajectoryData, Duration transferPeriod, Electricity powerConsumption,
        ImmutableArray<AnimationParams> animationParams, TrainStationFuelProto.Gfx graphics,
        RelTile1f? maxSpeedTilesPerTick, Duration? constructionDurationPerProduct, Upoints? boostCost,
        bool cannotBeReflected, bool isElectrified, bool requiresAlignment)
        : base(id, strings, layout, costs, trajectoryData, transferPeriod, powerConsumption,
            animationParams, graphics, maxSpeedTilesPerTick, constructionDurationPerProduct, boostCost,
            cannotBeReflected, isElectrified, requiresAlignment)
    {
        // Exempt the track from the missing-support collapse check (see the module/root protos).
        FieldInfo backing = typeof(EntityWithTrainTrackBaseProto).GetField(
            "<IgnoreMissingSupport>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        backing?.SetValue(this, true);
    }

    protected override void OnInitialize(ProtosDb protosDb)
    {
        base.OnInitialize(protosDb);
        // Restore the source proto's animation-data path (see the module proto for details).
        ElevatedStationData.FixAnimationDataPath(this);
    }
}
