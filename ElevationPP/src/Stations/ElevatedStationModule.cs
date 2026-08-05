using System;
using Mafi;
using Mafi.Core;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Collections.ReadonlyCollections;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Animations;
using Mafi.Core.Entities.Static;
using Mafi.Core.Trains;
using Mafi.Base.Prototypes.Trains;
using Mafi.Serialization;

namespace ElevationPP.Stations;

/// <summary>
/// A cargo loading station module that can sit on elevated track. It inherits all of the vanilla
/// <see cref="TrainStationModule"/> loading/unloading/scheduling behaviour and adds the
/// <see cref="ITrainTrackMayBeElevatedFriend"/> pillar contract — the small amount of per-entity
/// pillar bookkeeping that lets the train-track graph build and track <see cref="TrainTrackPillar"/>s
/// beneath it, exactly as the plain <c>TrainTrack</c> entity does.
///
/// The pillar-management members below mirror <c>TrainTrack</c>'s implementation verbatim; the
/// actual pillar geometry comes from the proto via <c>GetTransformedPillarsData</c>.
/// </summary>
[GenerateSerializer(false, null, 0)]
public class ElevatedStationModule : TrainStationModule, ITrainTrackMayBeElevatedFriend
{
    private LystStruct<TrainTrackPillar> m_pillars;

    public uint PillarBlocksBitmap { get; private set; }

    public ReadOnlyArraySlice<TrainTrackPillar> Pillars => m_pillars.BackingArrayAsSlice;

    // ITrainTrackMayBeElevatedFriend re-declares TrackProto with the elevated proto type.
    public new ITrainTrackMayBeElevatedProto TrackProto => (ElevatedStationModuleProto)base.Prototype;

    public ElevatedStationModule(EntityId id, ElevatedStationModuleProto proto, TileTransform transform,
        EntityContext context, TrainTracksGraphManager graphManager,
        IAnimationStateFactory animationStateFactory, TrainStationManager stationManager)
        : base(id, proto, transform, context, graphManager, animationStateFactory, stationManager)
    {
    }

    public void AddPillar(TrainTrackPillar pillar)
    {
        m_pillars.Add(pillar);
        PillarBlocksBitmap |= (uint)(1 << pillar.PillarInfo.InfoRel.BlockIndex);
    }

    public void AddPillarOnlyInfo(TrainTrackPillarInfoRel pillarInfoRel)
    {
        PillarBlocksBitmap |= (uint)(1 << pillarInfoRel.BlockIndex);
    }

    public void RemovePillar(TrainTrackPillar pillar)
    {
        m_pillars.RemoveAndAssert(pillar);
        PillarBlocksBitmap &= (uint)(~(1 << pillar.PillarInfo.InfoRel.BlockIndex));
    }

    ImmutableArray<TrainTrackPillarInfoRel> ITrainTrackMayBeElevatedFriend.GetTransformedPillarsData(Transform90RotFlip transform)
    {
        return ((ElevatedStationModuleProto)base.Prototype).GetTransformedPillarsData(transform);
    }

    // ── Never collapse from "uneven terrain" ──
    // The station is held up by pillars, not by the ground beneath its footprint, so the
    // terrain-stability monitor must not tear it down. Suppressing canCollapse also stops the
    // "may collapse due to uneven terrain" warning before it appears.

    public override void NotifyUnevenTerrain(Mafi.Collections.IReadOnlySet<int> groundVerticesViolatingConstraints,
        int newIndex, bool wasAdded, out bool canCollapse)
    {
        canCollapse = false;
    }

    public override bool TryCollapseOnUnevenTerrain(Mafi.Collections.IReadOnlySet<int> groundVerticesViolatingConstraints,
        EntityCollapseHelper collapseHelper)
    {
        return false;
    }

    // ── Serialization (mirrors the hand-written pattern used by other modded entities) ──

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction;
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction;

    public static void Serialize(ElevatedStationModule value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
        LystStruct<TrainTrackPillar>.Serialize(m_pillars, writer);
        writer.WriteUInt(PillarBlocksBitmap);
    }

    public static new ElevatedStationModule Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out ElevatedStationModule obj, (Func<BlobReader, Type, ElevatedStationModule>)null))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
        reader.SetField(this, "m_pillars", LystStruct<TrainTrackPillar>.Deserialize(reader));
        PillarBlocksBitmap = reader.ReadUInt();
    }

    static ElevatedStationModule()
    {
        s_serializeDataDelayedAction = (obj, writer) => ((ElevatedStationModule)obj).SerializeData(writer);
        s_deserializeDataDelayedAction = (obj, reader) => ((ElevatedStationModule)obj).DeserializeData(reader);
    }
}
