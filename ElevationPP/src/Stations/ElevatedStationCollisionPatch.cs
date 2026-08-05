using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Entities.Validators;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Terrain;
using Mafi.Core.Trains;

namespace ElevationPP.Stations;

/// <summary>
/// Lets a placed elevated station be electrified in-place with the vanilla "Electrify track" tool.
///
/// Electrification upgrades a track entity to its <c>Upgrade.NextTier</c> proto (the same tier chain
/// we use to pair the normal/electrified variants in the toolbar). To validate the swap, the engine
/// re-places the electrified proto at the same transform as the existing station (a <c>Move</c> with
/// the original id as <c>UpgradingId</c>) and runs the full collision check through
/// <c>EntitiesManager.CanAdd</c>, which fans out to every addition validator. Two of them reject the
/// station's own support pillars, so the tool turns red with "Collision with pillar":
///
///   1. <see cref="TrainTracksGraphManager"/>'s validator. For a station proto (not a plain
///      TrainTrackProto) it runs a dedicated occupancy scan that skips only the upgrading entity,
///      tracks and poles — NOT pillars. So both the train-track pillars and the transport-style
///      footprint pillars (<see cref="TransportPillar"/>) our stations rest on are flagged. This is
///      the one that also fires for a ground-level station (a zero-height pillar is still an entity).
///   2. <see cref="TerrainOccupancyManager"/>'s validator. The train-track add-request factory bakes
///      in an ignore predicate that skips tracks, train-track pillars and poles, but again not
///      <see cref="TransportPillar"/>, so an elevated station's footprint pillars collide here too.
///
/// Vanilla never hits either: ground stations have no pillars, elevated *tracks* have no layout
/// pillars, and elevated *layout* entities (balancers) go through the transport factory whose
/// predicate already ignores pillars.
///
/// Execution then re-validates again (with no UpgradingId) and, finally, the engine tries to grow
/// the catenary support masts — both of which also trip over the station's own footprint/pillars.
///
/// Fix — four narrowly-scoped Harmony patches, gated on protos/entities that are tied to our elevated
/// stations (a train-track entity that is also an elevatable pillar-passthrough layout entity — a
/// combination unique to them), so no vanilla entity changes behaviour:
///   1. A postfix on the track-graph validator that, for an in-place upgrade whose error is a
///      "Collision with ..." caused solely by pillars (no real blocker present), clears the error.
///   2. A prefix on the occupancy validator that augments the request's ignore predicate to also
///      skip TransportPillar/TrainTrackPillar, like the engine's own factories do for elevated protos.
///   3. A postfix on <c>CanReplaceExisting</c> (the no-UpgradingId execution path) that recognises the
///      in-place same-type station replacement the pillar column otherwise hides ("Cannot move this").
///   4. A postfix on <c>CanPlacePole</c> that lets the catenary masts place when their tile is blocked
///      only by our elevated stations and their pillars, so the masts land on the platform.
/// </summary>
internal static class ElevatedStationCollisionPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.stations";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    // Occupancy-validator (patch 2) internals.
    private static FieldInfo s_occEntitiesManagerField;   // TerrainOccupancyManager.m_entitiesManager
    private static FieldInfo s_ignoreBackingField;        // LayoutEntityAddRequest.<IgnoreForCollisions>k__BackingField

    // Track-graph-validator (patch 1) internals.
    private static FieldInfo s_graphEntitiesManagerField; // TrainTracksGraphManagerBase.EntitiesManager
    private static FieldInfo s_graphTerrainField;         // TrainTracksGraphManagerBase.TerrainManager
    private static FieldInfo s_graphOccupancyField;       // TrainTracksGraphManagerBase.OccupancyManager
    private static string s_collisionPrefix;              // localized "Collision with " prefix

    // Pole-placement-patch (patch 4) internals.
    private static FieldInfo s_poleTerrainField;       // TrainTracksPolesManager.m_terrainManager
    private static FieldInfo s_poleOccField;           // TrainTracksPolesManager.m_occupancyManager
    private static FieldInfo s_poleEmField;            // TrainTracksPolesManager.m_entitiesManager
    private static FieldInfo s_poleRegularPolesField;  // TrainTracksPolesManager.m_regularPoles

    private static readonly Lyst<EntityId> s_tmpIds = new Lyst<EntityId>();

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        var harmony = new Harmony(HARMONY_ID);
        applyOccupancyPatch(harmony);
        applyTrackGraphPatch(harmony);
        applyReplaceExistingPatch(harmony);
        applyPolePlacementPatch(harmony);
    }

    // ---- Patch 2: TerrainOccupancyManager occupancy validator ----------------------------------

    private static void applyOccupancyPatch(Harmony harmony)
    {
        MethodBase target = findValidatorCanAdd(typeof(TerrainOccupancyManager),
            typeof(IEntityWithOccupiedTilesAddRequest));
        if (target == null)
        {
            Log.Error("Elevation++: TerrainOccupancyManager request-level CanAdd not found; "
                + "elevated station electrification fix (occupancy) skipped.");
            return;
        }

        s_occEntitiesManagerField = typeof(TerrainOccupancyManager).GetField("m_entitiesManager",
            BindingFlags.NonPublic | BindingFlags.Instance);
        s_ignoreBackingField = typeof(LayoutEntityAddRequest).GetField(
            "<IgnoreForCollisions>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);

        if (s_occEntitiesManagerField == null || s_ignoreBackingField == null)
        {
            Log.Error("Elevation++: occupancy collision-patch internals not resolved; skipped.");
            return;
        }

        try
        {
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(ElevatedStationCollisionPatch), nameof(OccupancyCanAddPrefix)));
            Log.Info("Elevation++: elevated station occupancy collision patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply occupancy collision patch: {ex}");
        }
    }

    private static void OccupancyCanAddPrefix(object __instance, IEntityWithOccupiedTilesAddRequest request)
    {
        try
        {
            if (!(request is LayoutEntityAddRequest layoutRequest) || !isElevatedStationProto(layoutRequest.Proto))
            {
                return;
            }
            if (!(s_occEntitiesManagerField.GetValue(__instance) is IEntitiesManager entitiesManager))
            {
                return;
            }

            Predicate<EntityId> original = layoutRequest.IgnoreForCollisions.ValueOrNull;
            Predicate<EntityId> augmented = id =>
                (original != null && original(id))
                || entitiesManager.TryGetEntity<TransportPillar>(id, out _)
                || entitiesManager.TryGetEntity<TrainTrackPillar>(id, out _);

            // The pooled request resets IgnoreForCollisions on each reuse, so this only affects the
            // current validation pass.
            s_ignoreBackingField.SetValue(layoutRequest, Option.Some(augmented));
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    // ---- Patch 1: TrainTracksGraphManager track-graph validator --------------------------------

    private static void applyTrackGraphPatch(Harmony harmony)
    {
        MethodBase target = findValidatorCanAdd(typeof(TrainTracksGraphManager), typeof(LayoutEntityAddRequest));
        if (target == null)
        {
            Log.Error("Elevation++: TrainTracksGraphManager CanAdd(LayoutEntityAddRequest) not found; "
                + "elevated station electrification fix (track graph) skipped.");
            return;
        }

        Type baseType = typeof(TrainTracksGraphManager).BaseType;   // TrainTracksGraphManagerBase<...>
        s_graphEntitiesManagerField = baseType?.GetField("EntitiesManager", BindingFlags.NonPublic | BindingFlags.Instance);
        s_graphTerrainField = baseType?.GetField("TerrainManager", BindingFlags.NonPublic | BindingFlags.Instance);
        s_graphOccupancyField = baseType?.GetField("OccupancyManager", BindingFlags.NonPublic | BindingFlags.Instance);

        if (s_graphEntitiesManagerField == null || s_graphTerrainField == null || s_graphOccupancyField == null)
        {
            Log.Error("Elevation++: track-graph collision-patch internals not resolved; skipped.");
            return;
        }

        try
        {
            s_collisionPrefix = Tr.AdditionError__CollisionWith.Format(string.Empty).Value;
        }
        catch
        {
            s_collisionPrefix = null;
        }

        try
        {
            harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(ElevatedStationCollisionPatch), nameof(TrackGraphCanAddPostfix)));
            Log.Info("Elevation++: elevated station track-graph collision patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply track-graph collision patch: {ex}");
        }
    }

    private static void TrackGraphCanAddPostfix(object __instance, LayoutEntityAddRequest addRequest,
        ref EntityValidationResult __result)
    {
        try
        {
            if (!__result.IsError || !isElevatedStationProto(addRequest.Proto))
            {
                return;
            }
            // Only suppress the player-facing "Collision with ..." error, never a reserved-block,
            // terrain or out-of-map error.
            if (string.IsNullOrEmpty(s_collisionPrefix) || __result.ErrorMessageForPlayer.Value == null
                || !__result.ErrorMessageForPlayer.Value.StartsWith(s_collisionPrefix, StringComparison.Ordinal))
            {
                return;
            }
            // Only for an in-place upgrade (electrification / replace), where the footprint is
            // identical to the already-placed station.
            if (!tryGetUpgradingId(addRequest, out EntityId upgradingId))
            {
                return;
            }

            var entitiesManager = (IEntitiesManager)s_graphEntitiesManagerField.GetValue(__instance);
            var terrain = (TerrainManager)s_graphTerrainField.GetValue(__instance);
            var occupancy = (TerrainOccupancyManager)s_graphOccupancyField.GetValue(__instance);

            bool realBlocker = false;
            bool pillarBlocker = false;
            Tile3i origin = addRequest.Origin;

            foreach (OccupiedTileRelative tile in addRequest.OccupiedTiles)
            {
                s_tmpIds.Clear();
                Tile2iIndex tileIndex = terrain.GetTileIndex(origin.Xy + tile.RelCoord);
                HeightTilesI from = origin.Height + tile.FromHeightRel;
                occupancy.GetAllOccupyingEntitiesInRange(tileIndex, from, tile.VerticalSize, s_tmpIds);

                foreach (EntityId id in s_tmpIds)
                {
                    if (id == upgradingId)
                    {
                        continue;
                    }
                    if (!entitiesManager.TryGetEntity<StaticEntity>(id, out StaticEntity entity))
                    {
                        continue;
                    }
                    if (entity is TrainTrack || entity is TrainTrackPole)
                    {
                        continue;
                    }
                    if (entity is TrainTrackPillar || entity is TransportPillar)
                    {
                        pillarBlocker = true;
                        continue;
                    }
                    realBlocker = true;
                    break;
                }

                if (realBlocker)
                {
                    break;
                }
            }

            // Collision was caused solely by the station's own support pillars — let the upgrade pass.
            if (!realBlocker && pillarBlocker)
            {
                __result = EntityValidationResult.Success;
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    // ---- Patch 3: TrainTracksGraphManager.CanReplaceExisting -----------------------------------

    private static void applyReplaceExistingPatch(Harmony harmony)
    {
        MethodBase target = AccessTools.Method(typeof(TrainTracksGraphManager), "CanReplaceExisting");
        if (target == null)
        {
            Log.Error("Elevation++: TrainTracksGraphManager.CanReplaceExisting not found; "
                + "elevated station electrification fix (replace-existing) skipped.");
            return;
        }

        try
        {
            harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(ElevatedStationCollisionPatch), nameof(CanReplaceExistingPostfix)));
            Log.Info("Elevation++: elevated station replace-existing patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply replace-existing patch: {ex}");
        }
    }

    /// <summary>
    /// <c>CanReplaceExisting</c> decides whether a <c>Move</c> add-request (used by the electrify
    /// tool's execution path, which carries no UpgradingId) is replacing an identical entity in
    /// place. It probes <c>TryGetAnyOccupyingEntityAt(Origin)</c>, which for an elevated station can
    /// return the station's own pillar column instead of the station, so the in-place replace is
    /// rejected ("Cannot move this") and electrification silently no-ops. Re-probe for the station
    /// itself (TryGetAnyEntityAt scans every occupant at the tile, not just one) and accept when a
    /// matching same-type station sits at the request transform.
    /// </summary>
    private static void CanReplaceExistingPostfix(LayoutEntityAddRequest request,
        TerrainOccupancyManager occupancyManager, IEntitiesManager entitiesManager, ref bool __result)
    {
        try
        {
            if (__result || !isElevatedStationProto(request.Proto))
            {
                return;
            }
            if (occupancyManager.TryGetAnyEntityAt(request.Origin, out TrainStationBase station)
                && station.Transform == request.Transform
                && station.Prototype.Layout.LayoutSize.Xy == request.Proto.Layout.LayoutSize.Xy
                && station.Prototype.GetType() == request.Proto.GetType())
            {
                __result = true;
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    // ---- Patch 4: TrainTracksPolesManager.CanPlacePole -----------------------------------------

    private static void applyPolePlacementPatch(Harmony harmony)
    {
        // The poles manager lives in the (optional) Trains DLC assembly, so resolve it by name and
        // skip gracefully when the DLC is not loaded — exactly like the Unity render patch.
        Type t = AccessTools.TypeByName("Mafi.TrainsDlc.Poles.TrainTracksPolesManager");
        if (t == null)
        {
            Log.Info("Elevation++: TrainTracksPolesManager not present (Trains DLC not loaded?); "
                + "pole-placement patch skipped.");
            return;
        }

        MethodBase target = AccessTools.Method(t, "CanPlacePole",
            new[] { typeof(Tile3i), typeof(TrainTrackPoleInfoRel), typeof(IEntityWithTrainTrack) });
        if (target == null)
        {
            Log.Error("Elevation++: TrainTracksPolesManager.CanPlacePole not found; "
                + "elevated station catenary masts will not place.");
            return;
        }

        s_poleTerrainField = t.GetField("m_terrainManager", BindingFlags.NonPublic | BindingFlags.Instance);
        s_poleOccField = t.GetField("m_occupancyManager", BindingFlags.NonPublic | BindingFlags.Instance);
        s_poleEmField = t.GetField("m_entitiesManager", BindingFlags.NonPublic | BindingFlags.Instance);
        s_poleRegularPolesField = t.GetField("m_regularPoles", BindingFlags.NonPublic | BindingFlags.Instance);

        if (s_poleTerrainField == null || s_poleOccField == null || s_poleEmField == null
            || s_poleRegularPolesField == null)
        {
            Log.Error("Elevation++: pole-placement-patch internals not resolved; skipped.");
            return;
        }

        try
        {
            harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(ElevatedStationCollisionPatch), nameof(CanPlacePolePostfix)));
            Log.Info("Elevation++: elevated station pole-placement patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply pole-placement patch: {ex}");
        }
    }

    /// <summary>
    /// Catenary masts (<c>TrainTrackPole</c>) are rejected by <c>CanPlacePole</c> when their tile is
    /// occupied by anything that is not a track pillar/pole/bridge — which for our elevated stations
    /// means the station's own footprint, its <c>TransportPillar</c> support columns, or an adjacent
    /// elevated module. Vanilla stations avoid this by geometry; our rebuilt (elevated) trajectory
    /// puts the mast onto the station deck. Re-check the rejected tile and allow the mast when its
    /// only obstructions are our own elevated stations and their pillars, so the masts place on the
    /// platform. A genuine foreign obstruction, an existing pole, or invalid terrain still blocks.
    /// </summary>
    private static void CanPlacePolePostfix(object __instance, Tile3i trackBasePosition,
        TrainTrackPoleInfoRel poleInfoRel, IEntityWithTrainTrack ignoreEntity, ref bool __result)
    {
        try
        {
            if (__result)
            {
                return;
            }

            var terrain = (TerrainManager)s_poleTerrainField.GetValue(__instance);
            var occupancy = (TerrainOccupancyManager)s_poleOccField.GetValue(__instance);
            var entitiesManager = (IEntitiesManager)s_poleEmField.GetValue(__instance);

            Tile3i poleTile = trackBasePosition + poleInfoRel.Position.RelTile3i;

            // A pole already sitting here, or genuinely unbuildable terrain, is a real rejection.
            if (s_poleRegularPolesField.GetValue(__instance) is Dict<Tile3i, TrainTrackPole> regularPoles
                && regularPoles.ContainsKey(poleTile))
            {
                return;
            }
            Tile2iIndex tileIndex = terrain.GetTileIndex(poleTile.Xy);
            if (!terrain.IsValidIndex(tileIndex) || terrain.IsOffLimits(tileIndex)
                || terrain.IsBlockingBuildings(tileIndex))
            {
                return;
            }

            s_tmpIds.Clear();
            occupancy.GetAllOccupyingEntitiesInRange(tileIndex, trackBasePosition.Height,
                TrainTrackConstants.POLE_HEIGHT, s_tmpIds);

            bool ourBlocker = false;
            foreach (EntityId id in s_tmpIds)
            {
                if (ignoreEntity != null && id == ((IEntity)ignoreEntity).Id)
                {
                    continue;
                }
                if (!entitiesManager.TryGetEntity<IEntity>(id, out IEntity entity))
                {
                    continue;
                }
                if (entity is TrainTrackPole || entity is TrainTrackPillar)
                {
                    continue;   // engine already treats these as non-blocking
                }
                if (entity is TransportPillar)
                {
                    ourBlocker = true;
                    continue;
                }
                if (entity is TrainStationBase station && isElevatedStationProto(station.Prototype))
                {
                    ourBlocker = true;
                    continue;
                }
                return;   // a real, foreign obstruction — keep the rejection
            }

            if (ourBlocker)
            {
                __result = true;
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    // ---- shared helpers ------------------------------------------------------------------------

    private static bool isElevatedStationProto(ILayoutEntityProto proto)
    {
        return proto is IEntityWithTrainTrackBaseProto
            && proto is ILayoutEntityProtoWithElevation { CanBeElevated: true, CanPillarsPassThrough: true };
    }

    private static bool tryGetUpgradingId(LayoutEntityAddRequest addRequest, out EntityId upgradingId)
    {
        upgradingId = default;
        Lyst<IAddRequestMetadata> metadata = addRequest.Metadata.ValueOrNull;
        if (metadata == null)
        {
            return false;
        }
        foreach (IAddRequestMetadata m in metadata)
        {
            if (m is TrainTracksGraphManager.TrainTrackAddRequestMetaData meta && meta.UpgradingId.HasValue)
            {
                upgradingId = meta.UpgradingId.Value;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Finds an explicit-interface <c>IEntityAdditionValidator&lt;T&gt;.CanAdd(T)</c> structurally —
    /// one request argument of the given type, returning <see cref="EntityValidationResult"/>.
    /// </summary>
    private static MethodBase findValidatorCanAdd(Type declaringType, Type requestType)
    {
        foreach (MethodInfo m in declaringType.GetMethods(
                     BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!m.Name.EndsWith("CanAdd") || m.ReturnType != typeof(EntityValidationResult))
            {
                continue;
            }
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == requestType)
            {
                return m;
            }
        }
        return null;
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"Elevation++: elevated station collision patch failed (logged once): {ex}");
        }
    }
}
