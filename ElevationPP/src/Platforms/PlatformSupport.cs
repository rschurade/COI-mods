using System;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ReadonlyCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Entities.Validators;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Roads;
using Mafi.Core.Terrain;
using Mafi.Core.Trains;

namespace ElevationPP.Platforms;

/// <summary>
/// The rule book for "a building stands on a concrete platform", shared by every platform patch.
///
/// A building is PLATFORM-SUPPORTED when it is an ordinary ground building (a layout proto that
/// is not itself elevatable and needs no pillars — buildings, storages, machines; not the
/// mod's platforms or stations, not balancers/connectors, not tracks or roads) and EVERY tile of
/// its footprint bottom (the tiles at its own base height) sits directly on a platform deck:
/// the tile below it is occupied by a <see cref="ConcretePlatform"/> whose deck top is exactly
/// the building's base height. Partial overlap is not supported — a building hanging half off
/// the platform falls back to the vanilla terrain rules and is rejected as usual.
///
/// The engine binds buildings to the terrain in several places (placement validation, the
/// foundation shaping during construction, the may-collapse watchdog on terrain changes, and
/// truck goal heights); <see cref="PlatformSupportPatch"/> gates each of them on this rule so the
/// platform deck acts as the ground for the building. The answer for a placed entity is cached
/// (platform geometry only changes when platforms are added/removed, which flushes the cache).
///
/// Also keeps the registry of platform deck heights the placement cursor uses to snap onto a
/// platform (<see cref="PlatformSnapPatch"/>), and — when a platform goes away — re-runs the vanilla
/// terrain check on whatever stood on it, so those buildings get the standard "may collapse"
/// warning and collapse like any building that lost its ground.
/// </summary>
internal static class PlatformSupport
{
    private static TerrainOccupancyManager s_occupancy;
    private static IEntitiesManager s_entities;
    private static StaticEntitiesTerrainInteractionManager s_terrainInteraction;
    private static MethodInfo s_staticEntityConstructed;   // private, re-runs the terrain check
    private static Session s_session;
    private static bool s_runtimeErrorLogged;

    // The platform whose removal is being processed: treated as absent regardless of whether the
    // occupancy manager (another subscriber of the same removal event) already dropped it.
    private static ConcretePlatform s_platformBeingRemoved;

    // Cached per placed entity; flushed whenever a platform is added or removed.
    private static readonly Dictionary<IStaticEntity, bool> s_supportCache
        = new Dictionary<IStaticEntity, bool>();

    // Deck-top heights of all placed platforms (with counts), kept sorted descending for the
    // cursor snapping (the highest deck is the one nearest to the camera along a downward ray).
    private static readonly Dictionary<int, int> s_deckHeightCounts = new Dictionary<int, int>();
    private static readonly List<int> s_deckHeightsDesc = new List<int>();
    private static int[] s_deckHeightsSnapshot = new int[0];   // read by the UI thread

    /// <summary>Owner object for the entity-manager event subscriptions (one per session).</summary>
    private sealed class Session
    {
        public void OnAdded(IStaticEntity entity) => onEntityAdded(entity);
        public void OnRemoved(IStaticEntity entity) => onEntityRemoved(entity);
    }

    public static bool IsActive => s_occupancy != null;

    /// <summary>Deck-top heights of all placed platforms, highest first.</summary>
    public static IReadOnlyList<int> DeckTopHeightsDescending => s_deckHeightsSnapshot;

    /// <summary>Resolves the session's managers and (re)builds the platform registry. Called from
    /// the mod's Initialize for every game session (new or loaded).</summary>
    public static void Initialize(DependencyResolver resolver)
    {
        s_supportCache.Clear();
        s_deckHeightCounts.Clear();
        s_deckHeightsDesc.Clear();
        s_deckHeightsSnapshot = new int[0];
        s_session = null;
        s_occupancy = null;
        s_entities = null;
        s_terrainInteraction = null;

        s_occupancy = resolver.Resolve<TerrainOccupancyManager>();
        s_entities = resolver.Resolve<IEntitiesManager>();
        s_terrainInteraction = resolver.Resolve<StaticEntitiesTerrainInteractionManager>();
        s_staticEntityConstructed = typeof(StaticEntitiesTerrainInteractionManager).GetMethod(
            "staticEntityConstructed", BindingFlags.NonPublic | BindingFlags.Instance);
        if (s_staticEntityConstructed == null)
        {
            Log.Warning("Elevation++: terrain-interaction internals not resolved; buildings on a "
                + "collapsed platform will not be re-checked.");
        }

        // Platforms already in the world (loaded game).
        int count = 0;
        foreach (IEntity entity in s_entities.Entities)
        {
            if (entity is ConcretePlatform platform)
            {
                addDeckHeight(platform.DeckTopHeight.Value);
                count++;
            }
        }

        s_session = new Session();
        s_entities.StaticEntityAdded.AddNonSaveable(s_session, s_session.OnAdded);
        s_entities.StaticEntityRemoved.AddNonSaveable(s_session, s_session.OnRemoved);
        Log.Info($"Elevation++: platform support initialized ({count} platform(s) in the world).");
    }

    // ---- Support rules ------------------------------------------------------------------------

    /// <summary>Whether entities of this proto can stand on a platform at all: an ordinary ground
    /// building. Elevatable protos (platforms, stations, balancers, connectors, lifts) keep their
    /// vanilla pillar logic; tracks and roads have their own placement rules.</summary>
    public static bool IsCandidateProto(IStaticEntityProto proto)
    {
        if (!(proto is LayoutEntityProto layoutProto) || proto is ConcretePlatformProto)
        {
            return false;
        }
        if (proto is ILayoutEntityProtoWithElevation elevatable && elevatable.CanBeElevated)
        {
            return false;
        }
        if (proto is IEntityWithTrainTrackBaseProto || proto is RoadEntityProtoBase
            || proto is TransportPillarProto || proto is TrainTrackPillarProto)
        {
            return false;
        }
        return layoutProto.Layout.PlacementHeightRange.From.Value == 0
            && layoutProto.Layout.PlacementHeightRange.To.Value == 0;
    }

    /// <summary>The platform whose deck the tile at <paramref name="bottomHeight"/> would rest on:
    /// a platform deck occupies the tile directly below (its top face is at bottomHeight).</summary>
    public static bool TryGetPlatformUnder(Tile2i xy, HeightTilesI bottomHeight, out ConcretePlatform platform)
    {
        platform = null;
        if (s_occupancy == null || !s_occupancy.TryGetOccupyingEntityAt(
                xy.ExtendHeight(bottomHeight - ThicknessTilesI.One), out platform))
        {
            return false;
        }
        if (platform.IsDestroyed || ReferenceEquals(platform, s_platformBeingRemoved))
        {
            platform = null;
            return false;
        }
        return true;
    }

    /// <summary>Whether a placement request describes a platform-supported building (see the class
    /// comment). Uncached — requests are transient.</summary>
    public static bool IsPlatformSupported(IEntityWithOccupiedTilesAddRequest request)
    {
        if (!IsActive || !IsCandidateProto(request.Proto))
        {
            return false;
        }
        return isFootprintOnPlatforms(request.Origin, request.OccupiedTiles);
    }

    /// <summary>Whether a placed entity is a platform-supported building (see the class comment).
    /// Cached until platforms change.</summary>
    public static bool IsPlatformSupported(IStaticEntity entity)
    {
        if (!IsActive || entity == null)
        {
            return false;
        }
        if (entity is TransportPillar pillar)
        {
            return IsDeckBasedPillar(pillar);
        }
        if (!IsCandidateProto(entity.Prototype))
        {
            return false;
        }
        if (s_supportCache.TryGetValue(entity, out bool cached))
        {
            return cached;
        }
        bool supported = isFootprintOnPlatforms(entity.CenterTile, entity.OccupiedTiles.AsReadOnlyArray);
        s_supportCache[entity] = supported;
        return supported;
    }

    /// <summary>A transport pillar standing ON a platform deck (its base tile is the deck top),
    /// as built for belts and pipes running above the platform (<see cref="PlatformPillarPatch"/>).
    /// Uncached — the query is a single occupancy lookup.</summary>
    public static bool IsDeckBasedPillar(TransportPillar pillar)
    {
        return TryGetPlatformUnder(pillar.CenterTile.Xy, pillar.CenterTile.Height,
                out ConcretePlatform platform)
            && platform.DeckTopHeight == pillar.CenterTile.Height;
    }

    /// <summary>The highest platform deck at <paramref name="xy"/> whose top is at or below
    /// <paramref name="maxTop"/> — the deck a pillar reaching up to <paramref name="maxTop"/>
    /// would stand on. Walks the registry of deck heights, highest first.</summary>
    public static bool TryGetHighestDeckBelow(Tile2i xy, HeightTilesI maxTop, out HeightTilesI deckTop)
    {
        int[] heights = s_deckHeightsSnapshot;
        for (int i = 0; i < heights.Length; i++)
        {
            if (heights[i] > maxTop.Value)
            {
                continue;
            }
            var candidate = new HeightTilesI(heights[i]);
            if (TryGetPlatformUnder(xy, candidate, out ConcretePlatform platform)
                && platform.DeckTopHeight == candidate)
            {
                deckTop = candidate;
                return true;
            }
        }
        deckTop = default(HeightTilesI);
        return false;
    }

    private static bool isFootprintOnPlatforms(Tile3i origin, ReadOnlyArray<OccupiedTileRelative> tiles)
    {
        bool anyBottomTile = false;
        for (int i = 0; i < tiles.Length; i++)
        {
            OccupiedTileRelative tile = tiles[i];
            if (tile.Constraint.HasAnyConstraints(LayoutTileConstraint.UsingPillar))
            {
                return false;
            }
            if (tile.RelativeFrom > 0)
            {
                // Overhangs above the base need no support.
                continue;
            }
            if (tile.RelativeFrom < 0)
            {
                // Foundations dug below the base cannot sit on a deck.
                return false;
            }
            anyBottomTile = true;
            if (!TryGetPlatformUnder(origin.Xy + tile.RelCoord, origin.Height, out ConcretePlatform platform)
                || platform.DeckTopHeight != origin.Height)
            {
                return false;
            }
        }
        return anyBottomTile;
    }

    // ---- Registry -----------------------------------------------------------------------------

    private static void onEntityAdded(IStaticEntity entity)
    {
        try
        {
            if (entity is ConcretePlatform platform)
            {
                addDeckHeight(platform.DeckTopHeight.Value);
                s_supportCache.Clear();
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static void onEntityRemoved(IStaticEntity entity)
    {
        try
        {
            s_supportCache.Remove(entity);
            if (!(entity is ConcretePlatform platform))
            {
                return;
            }
            removeDeckHeight(platform.DeckTopHeight.Value);
            s_supportCache.Clear();
            s_platformBeingRemoved = platform;
            try
            {
                recheckBuildingsOn(platform);
            }
            finally
            {
                s_platformBeingRemoved = null;
                s_supportCache.Clear();
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    /// <summary>
    /// After a platform is gone, whatever stood on it has lost its ground. Re-run the vanilla
    /// post-construction terrain check on each of those buildings: with the platform no longer
    /// found under them the check sees a building far above the terrain, raises the standard
    /// "may collapse due to uneven terrain" warning and collapses it — the same fate as a
    /// building whose ground was dug away.
    /// </summary>
    private static void recheckBuildingsOn(ConcretePlatform platform)
    {
        if (s_staticEntityConstructed == null || s_terrainInteraction == null)
        {
            return;
        }
        HeightTilesI top = platform.DeckTopHeight;
        Tile2i origin = platform.CenterTile.Xy;
        var affected = new HashSet<IStaticEntity>();
        foreach (OccupiedTileRelative tile in platform.OccupiedTiles)
        {
            if (s_occupancy.TryGetOccupyingEntityAt(
                    (origin + tile.RelCoord).ExtendHeight(top), out IStaticEntity above)
                && !ReferenceEquals(above, platform) && !above.IsDestroyed
                && above.ConstructionState != ConstructionState.Invalid
                && (IsCandidateProto(above.Prototype) || above is TransportPillar))
            {
                affected.Add(above);
            }
        }
        foreach (IStaticEntity entity in affected)
        {
            s_staticEntityConstructed.Invoke(s_terrainInteraction, new object[] { entity });
        }
        if (affected.Count > 0)
        {
            Log.Info($"Elevation++: platform {platform.Id} removed with {affected.Count} building(s) on "
                + "it; they were handed to the terrain collapse check.");
        }
    }

    private static void addDeckHeight(int height)
    {
        s_deckHeightCounts.TryGetValue(height, out int count);
        s_deckHeightCounts[height] = count + 1;
        if (count == 0)
        {
            s_deckHeightsDesc.Add(height);
            s_deckHeightsDesc.Sort((a, b) => b.CompareTo(a));
            s_deckHeightsSnapshot = s_deckHeightsDesc.ToArray();
        }
    }

    private static void removeDeckHeight(int height)
    {
        if (!s_deckHeightCounts.TryGetValue(height, out int count))
        {
            return;
        }
        if (count <= 1)
        {
            s_deckHeightCounts.Remove(height);
            s_deckHeightsDesc.Remove(height);
            s_deckHeightsSnapshot = s_deckHeightsDesc.ToArray();
        }
        else
        {
            s_deckHeightCounts[height] = count - 1;
        }
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"Elevation++: platform support failed (logged once): {ex}");
        }
    }
}
