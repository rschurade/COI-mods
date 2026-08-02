using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Trains;

namespace ElevationPP;

/// <summary>
/// Lets a NEW elevated track be dragged over an existing track (or any other obstacle a centre
/// pillar cannot stand on): wherever the planned support pillar is blocked, the flanking portal
/// pair from <see cref="SideTrackPillarsPatch"/> is substituted automatically. Stacked lines can
/// therefore be built in any order — previously the upper line had to exist first (portals could
/// only be added to an already-built track), because the new track's plan failed with no valid
/// pillar spots.
///
/// Vanilla decides supportability in two places, and both must accept the crossing:
/// 1. During route search, TrainTrackPathFinder.Node.ComputeSupport prunes candidate pieces
///    whose unsupported span exceeds twice the support distance.
/// 2. When a plan is finalized, tryComputeSupportLocationsForSteps picks the concrete pillar
///    blocks; a stretch with no placeable centre pillar fails the whole plan (the red ghost).
/// Both funnel into TrainTracksPillarManager.CanPlacePillar. A postfix on it — active only
/// while one of those two methods runs (thread-static depth counter, bracketed via
/// prefix/finalizer) — turns a "no" into a "yes" when the portal pair would fit at that block,
/// validated with the same outward search and deck classification as a portal click. The check
/// is memoized per pathfinding run (cache cleared in StartPathFinding) because the A* search
/// asks about the same spots over and over.
///
/// The support computation initially records an ordinary centre pillar for such blocks; a
/// postfix on tryComputeSupportLocationsForSteps then rewrites the computed pillar list,
/// replacing every blocked-centre stand-in with the two real column infos (the plan-step
/// assembly and the drag ghost consume that list verbatim, so both show and build the actual
/// portal). At build time each column is a valid ordinary pillar add; a prefix on TryAddPillar
/// remains as a safety net — if the world changed between plan and click and a planned centre
/// (or column) spot is blocked at materialization, the pair is placed instead (adopting spanned
/// same-level tracks, exactly like a portal click). The crossbeam renderer picks the new pairs
/// up automatically.
/// </summary>
internal static class AutoPortalPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.autoportal";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    // Depth of vanilla support-planning calls on the current thread; the CanPlacePillar postfix
    // only acts while it is positive.
    [ThreadStatic] private static int s_planDepth;

    // Reentrancy guard: the feasibility check and the pair placement themselves call the patched
    // CanPlacePillar/TryAddPillar. Also settable by SideTrackPillarsPatch to keep the
    // substitution out of its manual command flows (their failure paths must fail plainly).
    [ThreadStatic] private static bool s_busy;

    internal static bool SuppressSubstitution
    {
        get => s_busy;
        set => s_busy = value;
    }

    private struct PlanAnswer
    {
        public bool Feasible;
        public ThicknessTilesF Height;
        public HeightTilesF Ground;
    }

    private readonly struct PlanKey : IEquatable<PlanKey>
    {
        private readonly Tile3i m_pos;
        private readonly RelTile2f m_relPos;
        private readonly RelTile2f m_dir;
        private readonly Fix32 m_relHeight;

        public PlanKey(Tile3i pos, TrainTrackPillarInfoRel rel)
        {
            m_pos = pos;
            m_relPos = rel.Position;
            m_dir = rel.Direction;
            m_relHeight = rel.RelHeight.Value;
        }

        public bool Equals(PlanKey other)
            => m_pos == other.m_pos && m_relPos == other.m_relPos
                && m_dir == other.m_dir && m_relHeight == other.m_relHeight;

        public override bool Equals(object obj) => obj is PlanKey other && Equals(other);

        public override int GetHashCode()
        {
            int hash = m_pos.GetHashCode();
            hash = hash * 397 ^ m_relPos.GetHashCode();
            hash = hash * 397 ^ m_dir.GetHashCode();
            return hash * 397 ^ m_relHeight.GetHashCode();
        }
    }

    // Per-thread because pathfinding may run off the main thread; cleared per pathfinding run.
    [ThreadStatic] private static Dictionary<PlanKey, PlanAnswer> s_planCache;

    // TrainTrackPathFinder.m_tracksPillarManager — needed by the plan-rewrite postfix.
    private static FieldInfo s_pathFinderPillarManagerField;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;
        if (!SideTrackPillarsPatch.IsFunctional)
        {
            Log.Warning("Elevation++: side pillars patch inactive; auto-portal patch skipped.");
            return;
        }

        MethodBase computeSupport
            = AccessTools.Method(typeof(TrainTrackPathFinder.Node), "ComputeSupport");
        MethodBase planSupports = AccessTools.Method(typeof(TrainTrackPathFinder),
            "tryComputeSupportLocationsForSteps");
        MethodBase startPathFinding
            = AccessTools.Method(typeof(TrainTrackPathFinder), "StartPathFinding");
        MethodBase canPlace = AccessTools.Method(typeof(TrainTracksPillarManager),
            "CanPlacePillar", new[]
            {
                typeof(Tile3i), typeof(TrainTrackPillarInfoRel),
                typeof(ThicknessTilesF).MakeByRefType(), typeof(HeightTilesF).MakeByRefType(),
            });
        MethodBase tryAdd = AccessTools.Method(typeof(TrainTracksPillarManager), "TryAddPillar");
        s_pathFinderPillarManagerField
            = AccessTools.Field(typeof(TrainTrackPathFinder), "m_tracksPillarManager");
        if (computeSupport == null || planSupports == null || startPathFinding == null
            || canPlace == null || tryAdd == null || s_pathFinderPillarManagerField == null)
        {
            Log.Error("Elevation++: pathfinder internals not resolved; auto-portal patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            var planEnter = new HarmonyMethod(typeof(AutoPortalPatch), nameof(PlanEnterPrefix));
            var planExit = new HarmonyMethod(typeof(AutoPortalPatch), nameof(PlanExitFinalizer));
            harmony.Patch(computeSupport, prefix: planEnter, finalizer: planExit);
            harmony.Patch(planSupports, prefix: planEnter,
                postfix: new HarmonyMethod(typeof(AutoPortalPatch), nameof(PlanSupportsPostfix)),
                finalizer: planExit);
            harmony.Patch(startPathFinding,
                prefix: new HarmonyMethod(typeof(AutoPortalPatch), nameof(ClearCachePrefix)));
            harmony.Patch(canPlace,
                postfix: new HarmonyMethod(typeof(AutoPortalPatch), nameof(CanPlacePillarPostfix)));
            harmony.Patch(tryAdd,
                prefix: new HarmonyMethod(typeof(AutoPortalPatch), nameof(TryAddPillarPrefix)));
            Log.Info("Elevation++: auto-portal placement patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply auto-portal patch: {ex}");
        }
    }

    private static void PlanEnterPrefix()
    {
        s_planDepth++;
    }

    private static void PlanExitFinalizer()
    {
        if (s_planDepth > 0)
        {
            s_planDepth--;
        }
    }

    private static void ClearCachePrefix()
    {
        s_planCache?.Clear();
    }

    /// <summary>
    /// After the support locations were computed successfully, rewrites the pillar list:
    /// every centre pillar that only validated thanks to the portal-feasibility postfix (its
    /// spot is genuinely blocked) is replaced by the pair's two real column infos, both keyed to
    /// the same plan step. The plan-step assembly copies list entries per step index (duplicates
    /// are fine), and the drag ghost renders the plan's pillar infos verbatim — so preview and
    /// build both get the actual portal instead of a centre pillar clipping the obstacle.
    /// Skipped for the electrification pass, which reuses the pillar list of the non-electrified
    /// pass with a different index list.
    /// </summary>
    private static void PlanSupportsPostfix(TrainTrackPathFinder __instance,
        Lyst<TrainTrackPathFinder.Node> outNodes, Lyst<TrainTrackPillarInfo> newPillars,
        Lyst<int> newSupportStepIndices, bool forElectrification, bool __result)
    {
        if (!__result || forElectrification || newPillars.Count == 0
            || newPillars.Count != newSupportStepIndices.Count)
        {
            return;
        }
        try
        {
            var manager
                = s_pathFinderPillarManagerField.GetValue(__instance) as TrainTracksPillarManager;
            if (manager == null)
            {
                return;
            }
            var infos = new List<TrainTrackPillarInfo>(newPillars.Count + 2);
            var indices = new List<int>(newPillars.Count + 2);
            bool changed = false;
            s_busy = true;
            try
            {
                for (int i = 0; i < newPillars.Count; i++)
                {
                    TrainTrackPillarInfo info = newPillars[i];
                    int stepIdx = newSupportStepIndices[i];
                    if (info.Height.IsPositive && !info.InfoRel.Direction.IsZero
                        && stepIdx >= 0 && stepIdx < outNodes.Count)
                    {
                        Tile3i trackPos
                            = outNodes[stepIdx].GetTransform(__instance.Start).Position;
                        bool centreOk = manager.CanPlacePillar(trackPos, info.InfoRel,
                            out ThicknessTilesF height, out _) && height.IsPositive;
                        if (!centreOk && SideTrackPillarsPatch.TryPlanPortalPairInfos(manager,
                            trackPos, info.InfoRel,
                            out TrainTrackPillarInfo left, out TrainTrackPillarInfo right))
                        {
                            infos.Add(left);
                            indices.Add(stepIdx);
                            infos.Add(right);
                            indices.Add(stepIdx);
                            changed = true;
                            continue;
                        }
                    }
                    infos.Add(info);
                    indices.Add(stepIdx);
                }
            }
            finally
            {
                s_busy = false;
            }
            if (!changed)
            {
                return;
            }
            newPillars.Clear();
            newSupportStepIndices.Clear();
            for (int i = 0; i < infos.Count; i++)
            {
                newPillars.Add(infos[i]);
                newSupportStepIndices.Add(indices[i]);
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    /// <summary>
    /// While vanilla support planning runs, a centre spot that cannot take a pillar still counts
    /// as supportable when the portal pair would fit there. The reported height/ground are the
    /// left column's, giving the plan a plausible stand-in pillar info; the build-time prefix
    /// below swaps it for the real pair.
    /// </summary>
    private static void CanPlacePillarPostfix(TrainTracksPillarManager __instance,
        Tile3i trackPosition, TrainTrackPillarInfoRel pillarInfoRel,
        ref ThicknessTilesF pillarHeight, ref HeightTilesF groundHeight, ref bool __result)
    {
        if (__result || s_planDepth <= 0 || s_busy)
        {
            return;
        }
        try
        {
            if (pillarInfoRel.Direction.IsZero)
            {
                return;
            }
            Dictionary<PlanKey, PlanAnswer> cache = s_planCache
                ?? (s_planCache = new Dictionary<PlanKey, PlanAnswer>());
            var key = new PlanKey(trackPosition, pillarInfoRel);
            if (!cache.TryGetValue(key, out PlanAnswer answer))
            {
                s_busy = true;
                try
                {
                    answer.Feasible = SideTrackPillarsPatch.TryPlanPortalPair(__instance,
                        trackPosition, pillarInfoRel, out answer.Height, out answer.Ground);
                }
                finally
                {
                    s_busy = false;
                }
                // Runaway backstop; a normal drag stays far below this.
                if (cache.Count > 8192)
                {
                    cache.Clear();
                }
                cache[key] = answer;
            }
            if (answer.Feasible)
            {
                pillarHeight = answer.Height;
                groundHeight = answer.Ground;
                __result = true;
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    /// <summary>
    /// When a planned centre pillar of a plain train track is materialized but its spot is
    /// blocked, places the portal pair instead and reports success. Anything else (valid centre
    /// spots, station support pillars, our own pre-validated flows) takes the vanilla path.
    /// </summary>
    private static bool TryAddPillarPrefix(TrainTracksPillarManager __instance,
        TrainTrackPillarInfo info, ITrainTrackMayBeElevatedFriend trainTrack, bool isFree,
        ref string error, ref bool __result)
    {
        if (s_busy)
        {
            return true;
        }
        var track = trainTrack as TrainTrack;
        if (track == null)
        {
            return true;
        }
        try
        {
            if (__instance.CanPlacePillar(track.TrackCenterTile, info.InfoRel,
                out ThicknessTilesF height, out _) && height.IsPositive)
            {
                return true;
            }
            bool placed;
            s_busy = true;
            try
            {
                placed = SideTrackPillarsPatch.TryPlacePortalPair(__instance, track,
                    info.InfoRel, isFree, out _);
            }
            finally
            {
                s_busy = false;
            }
            if (placed)
            {
                error = "";
                __result = true;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            logOnce(ex);
            return true;
        }
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"Elevation++: auto-portal patch failed (logged once): {ex}");
        }
    }
}
