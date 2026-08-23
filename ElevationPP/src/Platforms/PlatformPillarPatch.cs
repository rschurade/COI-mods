using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Factory.Transports;

namespace ElevationPP.Platforms;

/// <summary>
/// Lets belt/pipe support pillars stand ON a platform deck instead of passing through it.
///
/// Vanilla transport pillars always rise from the TERRAIN: <c>TransportsManager.CanBuildPillarAt</c>
/// takes the pillar base from the terrain tile and checks the whole column for collisions
/// (ignoring transports and elevatable entities that allow pass-through), and
/// <c>BuildOrExtendPillarNoChecks</c> creates the pillar from that terrain base. With the
/// platform NOT allowing pass-through, a belt running above a platform could get no pillar over
/// the deck at all. Three narrowly gated patches make the deck a valid pillar base:
///
///   - postfix on <c>CanBuildPillarAt</c>: when vanilla says no and the tile has a platform deck
///     at or below the wanted pillar top (and no pillar of its own yet), re-run the same checks
///     from the deck top as base — height cap and a collision scan of the column ABOVE the deck.
///   - prefix on <c>BuildOrExtendPillarNoChecks</c>: same situation → create the pillar with the
///     deck top as its base (a pillar already standing on that deck is replaced/extended like
///     vanilla does for ground pillars). Tiles that already carry a ground pillar reaching up
///     under the deck (a platform corner) are left to vanilla, which extends that pillar.
///   - postfix on <c>GetMaxPillarHeightAt</c> (the transport path-finder's pillar-height lookup):
///     the deck-based column counts, so the path-finder routes belts across platforms.
///
/// The deck-based pillars themselves are recognised by <see cref="PlatformSupport.IsDeckBasedPillar"/>
/// so the terrain watchdog leaves them alone (<see cref="PlatformSupportPatch"/>) and they collapse
/// with the platform.
/// </summary>
internal static class PlatformPillarPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.platformpillars";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    private static FieldInfo s_pillarsField;          // TransportsManager.m_pillars
    private static FieldInfo s_pillarsBuilderField;   // TransportsManager.m_pillarsBuilder
    private static FieldInfo s_entitiesManagerField;  // TransportsManager.m_entitiesManager
    private static FieldInfo s_occupancyField;        // TransportsManager.m_occupancyManager

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodBase canBuild = AccessTools.Method(typeof(TransportsManager), "CanBuildPillarAt");
        MethodBase build = AccessTools.Method(typeof(TransportsManager), "BuildOrExtendPillarNoChecks");
        MethodBase maxHeight = AccessTools.Method(typeof(TransportsManager), "GetMaxPillarHeightAt");
        s_pillarsField = AccessTools.Field(typeof(TransportsManager), "m_pillars");
        s_pillarsBuilderField = AccessTools.Field(typeof(TransportsManager), "m_pillarsBuilder");
        s_entitiesManagerField = AccessTools.Field(typeof(TransportsManager), "m_entitiesManager");
        s_occupancyField = AccessTools.Field(typeof(TransportsManager), "m_occupancyManager");
        if (canBuild == null || build == null || maxHeight == null || s_pillarsField == null
            || s_pillarsBuilderField == null || s_entitiesManagerField == null
            || s_occupancyField == null)
        {
            Log.Error("Elevation++: transport pillar internals not resolved; belts above platforms "
                + "cannot be supported on the deck.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(canBuild, postfix: new HarmonyMethod(typeof(PlatformPillarPatch),
                nameof(CanBuildPillarAtPostfix)));
            harmony.Patch(build, prefix: new HarmonyMethod(typeof(PlatformPillarPatch),
                nameof(BuildOrExtendPrefix)));
            harmony.Patch(maxHeight, postfix: new HarmonyMethod(typeof(PlatformPillarPatch),
                nameof(GetMaxPillarHeightAtPostfix)));
            Log.Info("Elevation++: platform pillar patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply platform pillar patch: {ex}");
        }
    }

    private static void CanBuildPillarAtPostfix(TransportsManager __instance, Tile2i position,
        HeightTilesI topTileHeight, ref HeightTilesI baseHeight, ref ThicknessTilesI newHeight,
        ref bool __result)
    {
        try
        {
            if (__result || !PlatformSupport.IsActive
                || !PlatformSupport.TryGetHighestDeckBelow(position, topTileHeight, out HeightTilesI deckTop))
            {
                return;
            }
            var pillars = (Dict<Tile2i, TransportPillar>)s_pillarsField.GetValue(__instance);
            if (pillars.ContainsKey(position))
            {
                // One pillar per tile: a ground pillar under the deck (a platform corner) blocks a
                // second, deck-based one; an existing deck-based pillar is handled by the extend path.
                return;
            }
            ThicknessTilesI height = topTileHeight - deckTop + ThicknessTilesI.One;
            if (height.IsNotPositive || height > TransportPillarProto.MAX_PILLAR_HEIGHT)
            {
                return;
            }
            var occupancy = (Mafi.Core.Terrain.TerrainOccupancyManager)s_occupancyField.GetValue(__instance);
            if (occupancy.TryGetAnyOccupyingEntityInRange(position.ExtendHeight(deckTop), height,
                    out EntityId _, __instance.IgnoreTransportsElevatedAndMiniZippersPredicate))
            {
                return;
            }
            baseHeight = deckTop;
            newHeight = height;
            __result = true;
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static bool BuildOrExtendPrefix(TransportsManager __instance, Tile2i position,
        HeightTilesI topHeight, bool skipTallEnough)
    {
        try
        {
            if (!PlatformSupport.IsActive
                || !PlatformSupport.TryGetHighestDeckBelow(position, topHeight, out HeightTilesI deckTop))
            {
                return true;
            }
            var pillars = (Dict<Tile2i, TransportPillar>)s_pillarsField.GetValue(__instance);
            TransportPillar existing = pillars.TryGetValue(position, out TransportPillar found) ? found : null;
            if (existing != null && existing.CenterTile.Height < deckTop)
            {
                // A ground pillar reaching up under the deck (platform corner): vanilla extends it.
                return true;
            }
            if (existing != null && existing.TopTileHeight >= topHeight && skipTallEnough)
            {
                return false;
            }
            var entities = (EntitiesManager)s_entitiesManagerField.GetValue(__instance);
            var builder = (TransportPillarsBuilder)s_pillarsBuilderField.GetValue(__instance);
            bool wasConstructed = existing != null && existing.IsConstructed;
            if (existing != null)
            {
                entities.RemoveAndDestroyEntityNoChecks(existing, EntityRemoveReason.Remove);
            }
            TransportPillar pillar = builder.Create(position.ExtendHeight(deckTop),
                topHeight - deckTop + ThicknessTilesI.One);
            entities.AddEntityNoChecks(pillar);
            if (wasConstructed)
            {
                pillar.MakeFullyConstructed(disableTerrainDisruption: true, doNotAdjustTerrainHeight: true);
            }
            return false;
        }
        catch (Exception ex)
        {
            logOnce(ex);
            return true;
        }
    }

    private static void GetMaxPillarHeightAtPostfix(TransportsManager __instance, Tile2i position,
        ref HeightTilesI? __result)
    {
        try
        {
            if (!PlatformSupport.IsActive)
            {
                return;
            }
            var pillars = (Dict<Tile2i, TransportPillar>)s_pillarsField.GetValue(__instance);
            if (pillars.ContainsKey(position))
            {
                // Vanilla already answers from the existing pillar (ground- or deck-based).
                return;
            }
            if (!PlatformSupport.TryGetHighestDeckBelow(position, HeightTilesI.MaxValue,
                    out HeightTilesI deckTop))
            {
                return;
            }
            var occupancy = (Mafi.Core.Terrain.TerrainOccupancyManager)s_occupancyField.GetValue(__instance);
            Predicate<EntityId> ignored = __instance.IgnoreTransportsElevatedAndMiniZippersPredicate;
            HeightTilesI? deckBased = null;
            for (int i = 0; i < TransportPillarProto.MAX_PILLAR_HEIGHT.Value; i++)
            {
                if (occupancy.TryGetAnyOccupyingEntityAt(
                        position.ExtendHeight(deckTop + new ThicknessTilesI(i)), out EntityId _, ignored))
                {
                    break;
                }
                deckBased = deckTop + new ThicknessTilesI(i + 1);
            }
            if (deckBased.HasValue && (!__result.HasValue || deckBased.Value > __result.Value))
            {
                __result = deckBased;
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
            Log.Error($"Elevation++: platform pillar patch failed (logged once): {ex}");
        }
    }
}
