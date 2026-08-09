using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ReadonlyCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Entities.Validators;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Ports;
using Mafi.Core.Terrain;

namespace ElevationPP.Stations;

/// <summary>
/// Stops elevated stations from raising a dirt plinth under themselves.
///
/// The engine supports elevated layout entities standing on ground that cannot take a support
/// pillar by TERRAFORMING instead: ILayoutEntityProtoWithElevationValidator.PrepareForAdd builds
/// a transport pillar under every UsingPillar tile — and where CanBuildOrExtendPillarAt said no,
/// it raises the terrain to the entity's base height as a substitute plinth. For a station
/// spliced onto an EXISTING elevated track, the tiles under the deck are blocked by the very
/// track span the station is about to replace, so vanilla raised a hill of dirt up to deck
/// height (which then slumped under terrain physics).
///
/// A prefix reimplements PrepareForAdd for the mod's station protos only: pillars are built
/// normally where possible, and every blocked tile is simply SKIPPED — no terrain raise, and no
/// forced pillar either. (Building a transport pillar on the blocked track-line tiles was tried
/// and backfired: the rail-pillar placement check tolerates decks and other rail pillars but
/// not transport pillars, so the station's own track-block pillar could no longer be placed and
/// the adjacent track spans lost their support.) Skipping restores the pre-Update-4 behaviour:
/// the track-line tiles carry the station's rail pillars via its ITrainTrackMayBeElevatedFriend
/// bookkeeping, and the building rests on the remaining ring pillars. Vanilla
/// elevation-capable protos (zippers, balancers, ...) keep their vanilla behaviour.
///
/// Belt-and-braces: a postfix on StaticEntity.DoNotAdjustTerrainDuringConstruction also opts the
/// station protos out of the construction-time foundation shaping (terrain-height layout data
/// applied relative to entity Z on build/deconstruct), which would similarly re-shape ground at
/// deck height.
///
/// Also enforces the terrain collision the hollow footprint otherwise loses: vanilla
/// elevation-capable protos put minTerrainHeight/maxTerrainHeight 0 on their UsingPillar tokens,
/// which is what StaticEntitiesTerrainInteractionManager's validator keys its "Terrain too
/// high"/pillar bookkeeping on — tokens without the bounds (like ours) are skipped entirely, so
/// a station could be lowered INTO the ground. We cannot simply add the vanilla bounds: the
/// minTerrainHeight side re-enables the "No pillars" rejection on tiles blocked by an existing
/// track span, which splicing a station onto a built line depends on. Instead a postfix on the
/// elevation validator's CanAdd re-adds just the too-high half for our protos: any footprint
/// tile whose bottom sits below the terrain surface (beyond the same vanilla tolerance) rejects
/// with the stock "Terrain too high" error, marking the offending tiles red.
///
/// Also fixes the pillars staying blueprint-blue under a finished station root: the vanilla
/// construction sync (TransportsManager.onEntityConstructed) makes the attached pillars fully
/// constructed when the elevated entity completes, but it early-returns for entities WITHOUT
/// ports — true for the station root (all vanilla elevation-capable protos have ports, so
/// vanilla never hits this). A postfix runs the same pillar promotion for portless station
/// entities.
/// </summary>
internal static class ElevatedStationTerrainPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.stationterrain";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    private static FieldInfo s_lastAddRequestField;
    private static FieldInfo s_transportsManagerField;
    private static FieldInfo s_terrainManagerField;
    private static MethodInfo s_findAttachedPillars;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodBase getter = AccessTools.PropertyGetter(typeof(StaticEntity),
            "DoNotAdjustTerrainDuringConstruction");
        MethodBase prepareForAdd = AccessTools.Method(
            typeof(ILayoutEntityProtoWithElevationValidator), "PrepareForAdd");
        MethodBase canAdd = AccessTools.Method(
            typeof(ILayoutEntityProtoWithElevationValidator), "CanAdd");
        s_lastAddRequestField = AccessTools.Field(
            typeof(ILayoutEntityProtoWithElevationValidator), "m_lastAddRequest");
        s_transportsManagerField = AccessTools.Field(
            typeof(ILayoutEntityProtoWithElevationValidator), "m_transportsManager");
        s_terrainManagerField = AccessTools.Field(
            typeof(ILayoutEntityProtoWithElevationValidator), "m_terrainManager");
        MethodBase onConstructed = AccessTools.Method(typeof(TransportsManager),
            "onEntityConstructed");
        s_findAttachedPillars = AccessTools.Method(typeof(TransportsManager),
            "FindAttachedPillars", new[] { typeof(LayoutEntity), typeof(Lyst<TransportPillar>) });
        if (getter == null || prepareForAdd == null || canAdd == null
            || s_lastAddRequestField == null || s_transportsManagerField == null
            || s_terrainManagerField == null || onConstructed == null
            || s_findAttachedPillars == null)
        {
            Log.Error("Elevation++: elevation validator internals not resolved; "
                + "elevated station terrain patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(prepareForAdd, prefix: new HarmonyMethod(
                typeof(ElevatedStationTerrainPatch), nameof(PrepareForAddPrefix)));
            harmony.Patch(canAdd, postfix: new HarmonyMethod(
                typeof(ElevatedStationTerrainPatch), nameof(CanAddPostfix)));
            harmony.Patch(getter, postfix: new HarmonyMethod(typeof(ElevatedStationTerrainPatch),
                nameof(DoNotAdjustTerrainPostfix)));
            harmony.Patch(onConstructed, postfix: new HarmonyMethod(
                typeof(ElevatedStationTerrainPatch), nameof(OnEntityConstructedPostfix)));
            Log.Info("Elevation++: elevated station terrain patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply elevated station terrain patch: {ex}");
        }
    }

    private static bool isOurStationProto(object proto)
    {
        return proto is ElevatedStationRootProto || proto is ElevatedStationModuleProto
            || proto is ElevatedStationFuelProto;
    }

    private static bool PrepareForAddPrefix(ILayoutEntityProtoWithElevationValidator __instance)
    {
        try
        {
            var pending = (Option<LayoutEntityAddRequest>)s_lastAddRequestField.GetValue(__instance);
            LayoutEntityAddRequest request = pending.ValueOrNull;
            if (request == null || !isOurStationProto(request.Proto))
            {
                return true;
            }
            s_lastAddRequestField.SetValue(__instance, Option<LayoutEntityAddRequest>.None);
            request.TryGetMetadata<CanBuildPillarValidationMetadata>(
                out CanBuildPillarValidationMetadata metadata);
            Set<Tile2i> possible = metadata?.TilesWithPossiblePillars;
            var transports = (TransportsManager)s_transportsManagerField.GetValue(__instance);
            Tile2i xy = request.Origin.Xy;
            HeightTilesI top = request.Origin.Height;
            ReadOnlyArray<OccupiedTileRelative>.Enumerator enumerator
                = request.OccupiedTiles.GetEnumerator();
            while (enumerator.MoveNext())
            {
                OccupiedTileRelative tileRel = enumerator.Current;
                if (!tileRel.Constraint.HasAnyConstraints(LayoutTileConstraint.UsingPillar))
                {
                    continue;
                }
                Tile2i tile = xy + tileRel.RelCoord;
                if (possible != null && possible.Contains(tile))
                {
                    transports.BuildOrExtendPillarNoChecks(tile, top, skipTallEnough: true);
                }
                // Blocked tile (the track span being spliced onto, a crossing track below, ...):
                // no pillar and — unlike vanilla — NO terrain raise. The track-line blocks get
                // the station's rail pillars instead, and the building rests on the ring.
            }
            return false;
        }
        catch (Exception ex)
        {
            logOnce(ex);
            return true;
        }
    }

    /// <summary>
    /// The terrain-too-high half of the vanilla terrain-collision check (see the class comment):
    /// rejects placement when any footprint tile's bottom sits below the terrain surface, so the
    /// station cannot be lowered into the ground. Runs after the validator's own CanAdd so its
    /// pillar metadata (which PrepareForAdd consumes) is already recorded.
    /// </summary>
    private static void CanAddPostfix(ILayoutEntityProtoWithElevationValidator __instance,
        LayoutEntityAddRequest addRequest, ref EntityValidationResult __result)
    {
        try
        {
            if (__result.IsError || !isOurStationProto(addRequest.Proto))
            {
                return;
            }
            if (!(s_terrainManagerField.GetValue(__instance) is TerrainManager terrain))
            {
                return;
            }

            Tile3i origin = addRequest.Origin;
            bool tooHigh = false;
            for (int i = 0; i < addRequest.OccupiedTiles.Length; i++)
            {
                OccupiedTileRelative tile = addRequest.OccupiedTiles[i];
                HeightTilesI tileBottom = origin.Height + tile.FromHeightRel;
                HeightTilesF terrainHeight = terrain.GetHeight(origin.Xy + tile.RelCoord);
                if (terrainHeight - StaticEntitiesTerrainInteractionManager.TOLERANCE
                    <= tileBottom.HeightTilesF)
                {
                    continue;
                }
                tooHigh = true;
                if (!addRequest.RecordTileErrorsAndMetadata)
                {
                    break;
                }
                addRequest.SetTileError(i);
            }

            if (tooHigh)
            {
                __result = EntityValidationResult.CreateError(Tr.AdditionError__TerrainTooHigh);
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    /// <summary>
    /// Vanilla promotes the support pillars of an elevation-capable entity to fully constructed
    /// when the entity finishes building — but only for entities with ports. The station root
    /// has none, leaving its pillars blueprint-blue forever; run the same promotion for it.
    /// </summary>
    private static void OnEntityConstructedPostfix(TransportsManager __instance, IStaticEntity e)
    {
        try
        {
            if (e is IEntityWithPorts || !isOurStationProto(e.Prototype))
            {
                return;
            }
            var layoutEntity = e as LayoutEntity;
            if (layoutEntity == null)
            {
                return;
            }
            var pillars = new Lyst<TransportPillar>();
            s_findAttachedPillars.Invoke(__instance, new object[] { layoutEntity, pillars });
            Lyst<TransportPillar>.Enumerator enumerator = pillars.GetEnumerator();
            while (enumerator.MoveNext())
            {
                enumerator.Current.MakeFullyConstructed();
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static void DoNotAdjustTerrainPostfix(StaticEntity __instance, ref bool __result)
    {
        if (!__result && isOurStationProto(__instance.Prototype))
        {
            __result = true;
        }
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"Elevation++: elevated station terrain patch failed (logged once): {ex}");
        }
    }
}
