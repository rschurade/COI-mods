using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Entities.Validators;
using Mafi.Core.PathFinding.Goals;
using Mafi.Core.Terrain;
using Mafi.Collections.ReadonlyCollections;

namespace ElevationPP.Platforms;

/// <summary>
/// Makes a concrete platform's deck count as the GROUND for the ordinary buildings standing on it
/// (see <see cref="PlatformSupport"/> for the rule). The engine ties a building to the terrain in
/// five places; each gets a narrowly gated Harmony patch that only fires for platform-supported
/// buildings (and deck-based pillars) and leaves everything else to vanilla:
///
///   1. Placement — <c>StaticEntitiesTerrainInteractionManager.CanAdd</c> compares every
///      ground vertex with the terrain height ("Terrain too low" for a building 6 tiles above
///      the ground). Prefix: a fully platform-supported request passes without the terrain check.
///      (Collision, off-map and all other validators still run unchanged; a building only
///      partly on a platform is not platform-supported and gets the vanilla verdict.)
///   1b. Water — <c>LayoutEntityTerrainValidator</c> rejects any ground vertex over an ocean tile
///      ("Cannot be built on ocean"): the vanilla rule for terrain-anchored buildings, which a
///      platform-borne building is not. Prefix: for a platform-supported request the ocean rules
///      are waived and only the validator's other checks (off-map, off-limits, blocked tile) run.
///      The platform itself may stand over water anyway (its pillar tiles are not ground tiles).
///   2. Construction — <c>ConstructionManager.SetTerrainUnderCustom</c> shapes the foundation
///      as construction progresses: it would raise a dirt column up to the deck (the layout's
///      terrain-height data is applied relative to the entity's Z) and lay a concrete floor
///      surface there. Prefix: skipped entirely for platform-supported buildings, on construction
///      and deconstruction alike (nothing was shaped, so nothing needs restoring).
///   3. The terrain watchdog — <c>processTileHeightChange</c> flags a constructed building
///      whose ground vertices are far above/below the terrain as "may collapse" and collapses
///      it. Prefix: skipped while the building stands on a platform. When the platform goes away
///      <see cref="PlatformSupport"/> re-runs this check and the building collapses vanilla-style.
///   4. Trucks — <c>StaticEntityVehicleGoal.ShouldCheckGoalHeights</c> keeps only goal tiles
///      whose vehicle surface is within the proto's placement range of the entity height, so
///      trucks could never reach a building 6 tiles up. Postfix: no height filter for
///      platform-supported buildings — trucks serve them from the ground beside/under the platform,
///      like they serve pillar-borne belts and elevated stations. (Trucks need the vanilla
///      vertical clearance to drive under a platform: 2 tiles for small, 4 for large trucks.)
/// </summary>
internal static class PlatformSupportPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.platesupport";

    private static bool s_applied;
    private static FieldInfo s_terrainField;   // LayoutEntityTerrainValidator.m_terrain
    private static bool s_runtimeErrorLogged;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodBase terrainCanAdd = AccessTools.Method(typeof(StaticEntitiesTerrainInteractionManager),
            "CanAdd", new[] { typeof(IEntityWithOccupiedTilesAddRequest) });
        MethodBase heightChange = AccessTools.Method(typeof(StaticEntitiesTerrainInteractionManager),
            "processTileHeightChange");
        MethodBase setTerrainUnder = AccessTools.Method(typeof(ConstructionManager),
            "SetTerrainUnderCustom");
        MethodBase goalHeights = AccessTools.Method(typeof(StaticEntityVehicleGoal),
            "ShouldCheckGoalHeights");
        // Explicit interface implementation: resolve it through the interface map.
        MethodBase oceanCanAdd = null;
        try
        {
            oceanCanAdd = typeof(LayoutEntityTerrainValidator)
                .GetInterfaceMap(typeof(IEntityAdditionValidator<ILayoutEntityAddRequest>))
                .TargetMethods[0];
        }
        catch (Exception)
        {
            // Reported below.
        }
        s_terrainField = AccessTools.Field(typeof(LayoutEntityTerrainValidator), "m_terrain");
        if (terrainCanAdd == null || heightChange == null || setTerrainUnder == null
            || goalHeights == null || oceanCanAdd == null || s_terrainField == null)
        {
            Log.Error("Elevation++: platform support internals not resolved; buildings cannot be "
                + "placed on concrete platforms.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(terrainCanAdd, prefix: new HarmonyMethod(typeof(PlatformSupportPatch),
                nameof(TerrainCanAddPrefix)));
            harmony.Patch(oceanCanAdd, prefix: new HarmonyMethod(typeof(PlatformSupportPatch),
                nameof(OceanCanAddPrefix)));
            harmony.Patch(heightChange, prefix: new HarmonyMethod(typeof(PlatformSupportPatch),
                nameof(HeightChangePrefix)));
            harmony.Patch(setTerrainUnder, prefix: new HarmonyMethod(typeof(PlatformSupportPatch),
                nameof(SetTerrainUnderPrefix)));
            harmony.Patch(goalHeights, postfix: new HarmonyMethod(typeof(PlatformSupportPatch),
                nameof(GoalHeightsPostfix)));
            Log.Info("Elevation++: platform support patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply platform support patch: {ex}");
        }
    }

    // 1. Placement: the deck is the ground.
    private static bool TerrainCanAddPrefix(IEntityWithOccupiedTilesAddRequest addRequest,
        ref EntityValidationResult __result)
    {
        try
        {
            if (!PlatformSupport.IsPlatformSupported(addRequest))
            {
                return true;
            }
            __result = EntityValidationResult.Success;
            return false;
        }
        catch (Exception ex)
        {
            logOnce(ex);
            return true;
        }
    }

    // 1b. Placement over water: only the ocean rules are waived, everything else the validator
    //     checks (off-map, off-limits ground, blocked tiles) is re-run here as vanilla does.
    private static bool OceanCanAddPrefix(LayoutEntityTerrainValidator __instance,
        ILayoutEntityAddRequest addRequest, ref EntityValidationResult __result)
    {
        try
        {
            if (!PlatformSupport.IsPlatformSupported(addRequest))
            {
                return true;
            }
            var terrain = (TerrainManager)s_terrainField.GetValue(__instance);
            Tile3i origin = addRequest.Transform.Position;
            ReadOnlyArray<OccupiedTileRelative> tiles = addRequest.OccupiedTiles;
            for (int i = 0; i < tiles.Length; i++)
            {
                Tile2i tile = origin.Xy + tiles[i].RelCoord;
                bool ok = terrain.IsValidCoord(tile)
                    && (!tiles[i].Constraint.HasAnyConstraints(LayoutTileConstraint.Ground)
                        || !terrain.IsOffLimits(terrain.GetTileIndex(tile)));
                if (!ok)
                {
                    if (addRequest.RecordTileErrorsAndMetadata)
                    {
                        addRequest.SetTileError(i);
                    }
                    __result = EntityValidationResult.CreateErrorFatal(Tr.AdditionError__OutsideOfMap.AsFormatted);
                    return false;
                }
            }
            for (int i = 0; i < tiles.Length; i++)
            {
                Tile2i tile = origin.Xy + tiles[i].RelCoord;
                if (terrain.IsBlockingBuildings(terrain.GetTileIndex(tile)))
                {
                    if (addRequest.RecordTileErrorsAndMetadata)
                    {
                        addRequest.SetTileError(i);
                    }
                    __result = EntityValidationResult.CreateError(Tr.AdditionError__SomethingInWay.AsFormatted);
                    return false;
                }
            }
            __result = EntityValidationResult.Success;
            return false;
        }
        catch (Exception ex)
        {
            logOnce(ex);
            return true;
        }
    }

    // 3. Watchdog: no "may collapse" while the platform is there.
    private static bool HeightChangePrefix(IStaticEntity entity)
    {
        try
        {
            return !PlatformSupport.IsPlatformSupported(entity);
        }
        catch (Exception ex)
        {
            logOnce(ex);
            return true;
        }
    }

    // 2. Construction: no foundation shaping over a deck.
    private static bool SetTerrainUnderPrefix(IStaticEntity staticEntity)
    {
        try
        {
            return !PlatformSupport.IsPlatformSupported(staticEntity);
        }
        catch (Exception ex)
        {
            logOnce(ex);
            return true;
        }
    }

    // 4. Trucks: reach platform-borne buildings from the ground.
    private static void GoalHeightsPostfix(StaticEntityVehicleGoal __instance, ref bool __result)
    {
        try
        {
            if (__result && PlatformSupport.IsPlatformSupported(__instance.GoalStaticEntity.ValueOrNull))
            {
                __result = false;
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"Elevation++: platform support patch failed (logged once): {ex}");
        }
    }
}
