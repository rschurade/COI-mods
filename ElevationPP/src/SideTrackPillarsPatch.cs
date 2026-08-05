using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Collections.ReadonlyCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Validators;
using Mafi.Core.Input;
using Mafi.Core.Terrain;
using Mafi.Core.Trains;
using Mafi.Unity.Entities.Static;
using Mafi.Unity.InputControl;
using Mafi.Unity.Trains;
using Mafi.Unity.Ui.Controllers.Trains;
using UnityEngine;
using EntityId = Mafi.Core.EntityId;

namespace ElevationPP;

/// <summary>
/// Lets a train track cross under an elevated track by placing "portal" pillars beside the
/// crossing instead of the (blocked) pillar directly on it.
///
/// Vanilla refuses to place a rail pillar wherever a ground track (or anything else) crosses the
/// pillar's column, so an elevated track over a crossing keeps an unsupported block. Shift- or
/// ctrl-clicking that block with the vanilla "add/remove pillars" tool now places TWO pillars
/// beside the elevated track — one left, one right, perpendicular to the track direction — at
/// the first valid spot on each side, found by sliding outward from the block in half-tile
/// steps. The modifier combo picks the stance: shift = the closest valid spot, then ctrl +2,
/// ctrl+shift +4, alt +6 and alt+shift +8 tiles further out. Both pillars register under the clicked block's index; the engine's support
/// bookkeeping is keyed purely on block index (never on the pillar's position), so the block
/// counts as fully supported even though nothing stands directly beneath it. The outward search
/// is capped at PILLAR_SUPPORT_DISTANCE so the portal can't grow absurd. Works per track, so a
/// dual elevated line gets its columns with one modifier-click per track.
///
/// The flanking pair is treated as a unit: removing either pillar (vanilla tool) removes all
/// pillars of that block, keeping the per-block pillar bitmap consistent.
///
/// Plumbing: the vanilla tool schedules AddTrainTrackPillarCmd(trackId, blockIdx), which can only
/// express the proto-defined pillar position. A modifier-click stores the computed flanking
/// placements in a static pending slot and schedules the same command; a prefix on the command
/// processor consumes the pending slot and places the pair via the manager's public
/// CanPlacePillar/TryAddPillar instead (fine in COI's single-process, single-player command loop).
/// Candidate search and validation run in a postfix of the controller's simUpdate (sim thread,
/// same place vanilla validates), published to the main thread as an immutable proposal that also
/// drives the two ghost-pillar previews shown while the modifier is held.
/// </summary>
internal static class SideTrackPillarsPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.sidepillars";

    // Start the outward search 1.5 tiles from the track centreline — the column tucks right
    // against (partly under) the deck edge. Modifier combos widen the stance beyond the
    // closest valid spot: shift +0, ctrl +2, ctrl+shift +4, alt +6, alt+shift +8 tiles.
    // Half-tile search steps.
    private const int MIN_OFFSET_HALF_TILES = 3;

    // Recompute the proposal every N sim ticks even when the hovered block is unchanged, so the
    // preview tracks terrain/entity changes.
    private const int RECOMPUTE_TICKS = 10;

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    /// <summary>True once all internals resolved and patches applied; AutoPortalPatch builds on
    /// the helpers of this class and must not activate without it.</summary>
    internal static bool IsFunctional { get; private set; }

    // TrainTrackPillarBuildController privates.
    private static FieldInfo s_isActiveField;
    private static FieldInfo s_lockField;
    private static FieldInfo s_selectedObjectField;
    private static FieldInfo s_selectedDirtyField;
    private static FieldInfo s_previewDataField;
    private static FieldInfo s_needsHighlightField;
    private static FieldInfo s_ongoingCmdField;
    private static FieldInfo s_pillarManagerField;
    private static FieldInfo s_pillarsRendererField;
    private static FieldInfo s_inputSchedulerField;
    private static FieldInfo s_shortcutsField;
    private static FieldInfo s_createSoundField;
    private static FieldInfo s_invalidSoundField;

    private static FieldInfo s_previewPillarField;
    private static FieldInfo s_graphManagerField;

    // The graph manager captured from the live controller (needed by the removal command prefix
    // for the per-track support-removal safety check); refreshed every controller update so it
    // always belongs to the current game session.
    private static volatile TrainTracksGraphManager s_graphManager;

    // TrainTracksPillarManager privates.
    private static FieldInfo s_entitiesManagerField;
    private static FieldInfo s_occupancyManagerField;
    private static FieldInfo s_terrainManagerField;

    // UnityEngine.EventSystems.EventSystem (lives in an assembly the mod does not reference).
    private static PropertyInfo s_eventSystemCurrentProp;
    private static MethodInfo s_isPointerOverGameObject;

    /// <summary>
    /// A validated flanking-pillar pair for one hovered (track, block). Computed on the sim
    /// thread, read on the main thread; instances are never mutated after publication.
    /// </summary>
    private sealed class SideProposal
    {
        public EntityId TrackId;
        public int BlockIdx;
        public int ExtraTiles;
        public bool Valid;
        // Set when the proposal replaces an existing pillar (hover over a pillar instead of a
        // free track block); ReplacePillarId identifies the hovered pillar.
        public bool Replace;
        public EntityId ReplacePillarId;
        public TrainTrackPillarInfoRel RelLeft;
        public TrainTrackPillarInfoRel RelRight;
        public TrainTrackPillarInfo InfoLeft;
        public TrainTrackPillarInfo InfoRight;
    }

    private static volatile SideProposal s_proposal;
    private static int s_ticksSinceCompute;

    // Extra stance width in tiles from the held modifier combo (shift 0, ctrl 2, ctrl+shift 4,
    // alt 6, alt+shift 8). Written on the main thread, read by the sim-thread proposal
    // computation.
    private static volatile int s_extraTiles;

    /// <summary>
    /// Payload for the next AddTrainTrackPillarCmd; set on the main thread right before
    /// scheduling, consumed by the command-processor prefix on the sim thread.
    /// </summary>
    private sealed class PendingSideAdd
    {
        public EntityId TrackId;
        public int BlockIdx;
        public TrainTrackPillarInfoRel RelLeft;
        public TrainTrackPillarInfoRel RelRight;
    }

    private static volatile PendingSideAdd s_pending;

    /// <summary>
    /// Payload for the next RemoveTrainTrackPillarCmd when it should REPLACE the removed
    /// pillar's block with a flanking pair instead of just removing it.
    /// </summary>
    private sealed class PendingReplace
    {
        public EntityId PillarId;
        public TrainTrackPillarInfoRel RelLeft;
        public TrainTrackPillarInfoRel RelRight;
    }

    private static volatile PendingReplace s_pendingReplace;

    // Ghost previews shown while the modifier is held (main thread only).
    private static SideProposal s_shownProposal;
    private static RenderedPillarData? s_ghostLeft;
    private static RenderedPillarData? s_ghostRight;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        Type controller = typeof(TrainTrackPillarBuildController);
        MethodBase inputUpdate = AccessTools.Method(controller, "InputUpdate");
        MethodBase deactivate = AccessTools.Method(controller, "Deactivate");
        MethodBase simUpdate = AccessTools.Method(controller, "simUpdate");
        MethodBase addInvoke = getExplicitInvoke(typeof(IAction<AddTrainTrackPillarCmd>));
        MethodBase removeInvoke = getExplicitInvoke(typeof(IAction<RemoveTrainTrackPillarCmd>));

        s_isActiveField = AccessTools.Field(controller, "m_isActive");
        s_lockField = AccessTools.Field(controller, "m_lock");
        s_selectedObjectField = AccessTools.Field(controller, "m_selectedObject");
        s_selectedDirtyField = AccessTools.Field(controller, "m_selectedObjectDirty");
        s_previewDataField = AccessTools.Field(controller, "m_previewPillarData");
        s_needsHighlightField = AccessTools.Field(controller, "m_needsHighlight");
        s_ongoingCmdField = AccessTools.Field(controller, "m_ongoingCmd");
        s_pillarManagerField = AccessTools.Field(controller, "m_pillarManager");
        s_pillarsRendererField = AccessTools.Field(controller, "m_pillarsRenderer");
        s_inputSchedulerField = AccessTools.Field(controller, "m_inputScheduler");
        s_shortcutsField = AccessTools.Field(controller, "m_shortcutsManager");
        s_createSoundField = AccessTools.Field(controller, "m_createSound");
        s_invalidSoundField = AccessTools.Field(controller, "m_invalidSound");
        s_previewPillarField = AccessTools.Field(controller, "m_previewPillar");
        s_graphManagerField = AccessTools.Field(controller, "m_trainTracksGraphManager");
        s_entitiesManagerField = AccessTools.Field(typeof(TrainTracksPillarManager), "m_entitiesManager");
        s_occupancyManagerField = AccessTools.Field(typeof(TrainTracksPillarManager), "m_occupancyManager");
        s_terrainManagerField = AccessTools.Field(typeof(TrainTracksPillarManager), "m_terrainManager");

        if (inputUpdate == null || deactivate == null || simUpdate == null
            || addInvoke == null || removeInvoke == null
            || s_isActiveField == null || s_lockField == null || s_selectedObjectField == null
            || s_selectedDirtyField == null || s_previewDataField == null
            || s_needsHighlightField == null || s_ongoingCmdField == null
            || s_pillarManagerField == null || s_pillarsRendererField == null
            || s_inputSchedulerField == null || s_shortcutsField == null
            || s_previewPillarField == null || s_graphManagerField == null
            || s_entitiesManagerField == null
            || s_occupancyManagerField == null || s_terrainManagerField == null)
        {
            Log.Error("Elevation++: pillar tool internals not resolved; side pillars patch skipped.");
            return;
        }

        Type eventSystem = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
        if (eventSystem != null)
        {
            s_eventSystemCurrentProp = eventSystem.GetProperty("current",
                BindingFlags.Public | BindingFlags.Static);
            s_isPointerOverGameObject = eventSystem.GetMethod("IsPointerOverGameObject",
                Type.EmptyTypes);
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(inputUpdate,
                prefix: new HarmonyMethod(typeof(SideTrackPillarsPatch), nameof(InputUpdatePrefix)));
            harmony.Patch(simUpdate,
                postfix: new HarmonyMethod(typeof(SideTrackPillarsPatch), nameof(SimUpdatePostfix)));
            harmony.Patch(deactivate,
                postfix: new HarmonyMethod(typeof(SideTrackPillarsPatch), nameof(DeactivatePostfix)));
            harmony.Patch(addInvoke,
                prefix: new HarmonyMethod(typeof(SideTrackPillarsPatch), nameof(AddPillarCmdPrefix)));
            harmony.Patch(removeInvoke,
                prefix: new HarmonyMethod(typeof(SideTrackPillarsPatch), nameof(RemovePillarCmdPrefix)));
            IsFunctional = true;
            Log.Info("Elevation++: side track pillars patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply side track pillars patch: {ex}");
        }
    }

    private static MethodBase getExplicitInvoke(Type interfaceType)
    {
        try
        {
            InterfaceMapping map = typeof(TrainTracksPillarManager).GetInterfaceMap(interfaceType);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == "Invoke")
                {
                    return map.TargetMethods[i];
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to map {interfaceType.Name}.Invoke: {ex.Message}");
        }
        return null;
    }

    private static bool isModifierHeld()
    {
        bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        if (!ctrl && !shift && !alt)
        {
            return false;
        }
        s_extraTiles = alt ? (shift ? 8 : 6) : ctrl ? (shift ? 4 : 2) : 0;
        return true;
    }

    private static bool isPointerOverUi()
    {
        try
        {
            if (s_eventSystemCurrentProp == null || s_isPointerOverGameObject == null)
            {
                return false;
            }
            object current = s_eventSystemCurrentProp.GetValue(null);
            return current != null && (bool)s_isPointerOverGameObject.Invoke(current, null);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ UI side (main thread)

    private static bool InputUpdatePrefix(TrainTrackPillarBuildController __instance, ref bool __result)
    {
        try
        {
            if (!(bool)s_isActiveField.GetValue(__instance))
            {
                return true;
            }
            s_graphManager = (TrainTracksGraphManager)s_graphManagerField.GetValue(__instance);
            SideProposal proposal = s_proposal;
            if (!isModifierHeld())
            {
                hidePreviews(__instance);
                if (s_centerGhostSuppressed)
                {
                    // Let simUpdate recompute the vanilla centre-pillar preview data so its
                    // ghost comes back once the modifier is released.
                    s_centerGhostSuppressed = false;
                    s_selectedDirtyField.SetValue(__instance, true);
                }
                return true;
            }
            updatePreviews(__instance, proposal);
            suppressCenterGhost(__instance);

            var shortcuts = (ShortcutsManager)s_shortcutsField.GetValue(__instance);
            if (!shortcuts.IsPrimaryActionUp || isPointerOverUi())
            {
                return true;
            }
            var ongoing = (Option<InputCommand>)s_ongoingCmdField.GetValue(__instance);
            if (ongoing.HasValue && !ongoing.Value.IsProcessedAndSynced)
            {
                return true;
            }
            var selected = (Pair<Option<IStaticEntity>, int?>)s_selectedObjectField.GetValue(__instance);
            InputCommand cmd = null;
            if (selected.First.ValueOrNull is TrainTrackPillar clickedPillar)
            {
                // Modifier-click on an existing pillar: replace its block's pillar(s) with the
                // flanking pair (plain clicks still remove, handled by vanilla).
                if (proposal == null || !proposal.Valid || !proposal.Replace
                    || proposal.ReplacePillarId != clickedPillar.Id)
                {
                    playSound(__instance, s_invalidSoundField);
                    __result = true;
                    return false;
                }
                s_pendingReplace = new PendingReplace
                {
                    PillarId = clickedPillar.Id,
                    RelLeft = proposal.RelLeft,
                    RelRight = proposal.RelRight,
                };
                var scheduler = (IInputScheduler)s_inputSchedulerField.GetValue(__instance);
                cmd = scheduler.ScheduleInputCmd(new RemoveTrainTrackPillarCmd(clickedPillar.Id));
            }
            else if (selected.First.ValueOrNull is TrainTrack track && selected.Second.HasValue)
            {
                // With the modifier held the click is ours either way — never let the vanilla
                // handler place the centre pillar on a modifier-click.
                if (proposal == null || !proposal.Valid || proposal.Replace
                    || proposal.TrackId != track.Id || proposal.BlockIdx != selected.Second.Value)
                {
                    playSound(__instance, s_invalidSoundField);
                    __result = true;
                    return false;
                }
                s_pending = new PendingSideAdd
                {
                    TrackId = proposal.TrackId,
                    BlockIdx = proposal.BlockIdx,
                    RelLeft = proposal.RelLeft,
                    RelRight = proposal.RelRight,
                };
                var scheduler = (IInputScheduler)s_inputSchedulerField.GetValue(__instance);
                cmd = scheduler.ScheduleInputCmd(
                    new AddTrainTrackPillarCmd(proposal.TrackId, proposal.BlockIdx));
            }
            else
            {
                return true;
            }

            // Mirror the vanilla post-click state reset so the controller waits for the command
            // and refreshes its selection/highlight.
            s_ongoingCmdField.SetValue(__instance, (Option<InputCommand>)cmd);
            s_selectedObjectField.SetValue(__instance,
                new Pair<Option<IStaticEntity>, int?>(Option<IStaticEntity>.None, null));
            s_selectedDirtyField.SetValue(__instance, true);
            s_previewDataField.SetValue(__instance, null);
            s_needsHighlightField.SetValue(__instance, true);
            hidePreviews(__instance);
            playSound(__instance, s_createSoundField);
            __result = true;
            return false;
        }
        catch (Exception ex)
        {
            logOnce(ex);
            return true;
        }
    }

    private static void DeactivatePostfix(TrainTrackPillarBuildController __instance)
    {
        try
        {
            hidePreviews(__instance);
            s_proposal = null;
            s_pending = null;
            s_pendingReplace = null;
            s_centerGhostSuppressed = false;
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static bool s_centerGhostSuppressed;

    /// <summary>
    /// While the modifier is held the centre-pillar ghost is just noise (the click will place
    /// the side pillars, never the centre one), so null out the vanilla preview data and let the
    /// vanilla needs-highlight pass remove any ghost it already shows.
    /// </summary>
    private static void suppressCenterGhost(TrainTrackPillarBuildController controller)
    {
        object previewData = s_previewDataField.GetValue(controller);
        object shownGhost = s_previewPillarField.GetValue(controller);
        if (previewData != null || shownGhost != null)
        {
            s_previewDataField.SetValue(controller, null);
            s_needsHighlightField.SetValue(controller, true);
        }
        s_centerGhostSuppressed = true;
    }

    private static void updatePreviews(TrainTrackPillarBuildController controller, SideProposal proposal)
    {
        if (ReferenceEquals(proposal, s_shownProposal))
        {
            return;
        }
        hidePreviews(controller);
        s_shownProposal = proposal;
        if (proposal == null || !proposal.Valid)
        {
            return;
        }
        var renderer = (TrainTrackPillarsRenderer)s_pillarsRendererField.GetValue(controller);
        ColorRgba color = InstancedChunkBasedLayoutEntitiesRenderer.BLUEPRINT_CONSTRUCTION_COLOR;
        s_ghostLeft = renderer.AddPillarPreviewVisualImmediate(proposal.InfoLeft, color);
        s_ghostRight = renderer.AddPillarPreviewVisualImmediate(proposal.InfoRight, color);
    }

    private static void hidePreviews(TrainTrackPillarBuildController controller)
    {
        s_shownProposal = null;
        if (!s_ghostLeft.HasValue && !s_ghostRight.HasValue)
        {
            return;
        }
        var renderer = (TrainTrackPillarsRenderer)s_pillarsRendererField.GetValue(controller);
        if (s_ghostLeft.HasValue)
        {
            renderer.RemovePillarVisualImmediate(s_ghostLeft.Value);
            s_ghostLeft = null;
        }
        if (s_ghostRight.HasValue)
        {
            renderer.RemovePillarVisualImmediate(s_ghostRight.Value);
            s_ghostRight = null;
        }
    }

    private static void playSound(object controller, FieldInfo soundField)
    {
        try
        {
            object source = soundField?.GetValue(controller);
            source?.GetType().GetMethod("Play", Type.EmptyTypes)?.Invoke(source, null);
        }
        catch
        {
            // Sound is best-effort only.
        }
    }

    // ------------------------------------------------------------ proposal side (sim thread)

    private static void SimUpdatePostfix(TrainTrackPillarBuildController __instance)
    {
        try
        {
            if (!(bool)s_isActiveField.GetValue(__instance))
            {
                s_proposal = null;
                return;
            }
            object lockObj = s_lockField.GetValue(__instance);
            if (!System.Threading.Monitor.TryEnter(lockObj))
            {
                return;
            }
            try
            {
                var selected = (Pair<Option<IStaticEntity>, int?>)s_selectedObjectField.GetValue(__instance);
                TrainTrack track = null;
                int blockIdx = -1;
                bool replace = false;
                EntityId replacePillarId = default(EntityId);
                if (selected.First.ValueOrNull is TrainTrack hoveredTrack && selected.Second.HasValue)
                {
                    track = hoveredTrack;
                    blockIdx = selected.Second.Value;
                }
                else if (selected.First.ValueOrNull is TrainTrackPillar hoveredPillar
                    && hoveredPillar.TrainTrack is TrainTrack pillarTrack)
                {
                    // Hovering an existing pillar: propose replacing its block with the pair.
                    track = pillarTrack;
                    blockIdx = hoveredPillar.BlockIndex;
                    replace = true;
                    replacePillarId = hoveredPillar.Id;
                }
                if (track == null || track.IsDestroyed)
                {
                    s_proposal = null;
                    return;
                }
                int extraTiles = s_extraTiles;
                SideProposal current = s_proposal;
                if (current != null && current.TrackId == track.Id && current.BlockIdx == blockIdx
                    && current.ExtraTiles == extraTiles && current.Replace == replace
                    && current.ReplacePillarId == replacePillarId
                    && ++s_ticksSinceCompute < RECOMPUTE_TICKS)
                {
                    return;
                }
                s_ticksSinceCompute = 0;
                var manager = (TrainTracksPillarManager)s_pillarManagerField.GetValue(__instance);
                s_proposal = computeProposal(track, blockIdx, manager, extraTiles, replace, replacePillarId);
            }
            finally
            {
                System.Threading.Monitor.Exit(lockObj);
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static SideProposal computeProposal(TrainTrack track, int blockIdx,
        TrainTracksPillarManager manager, int extraTiles, bool replace, EntityId replacePillarId)
    {
        var result = new SideProposal
        {
            TrackId = track.Id,
            BlockIdx = blockIdx,
            ExtraTiles = extraTiles,
            Replace = replace,
            ReplacePillarId = replacePillarId,
        };
        ITrainTrackMayBeElevatedFriend friend = track;
        // A block that already has pillars only qualifies in replace mode.
        if (blockIdx < 0 || (!replace && (friend.PillarBlocksBitmap & (uint)(1 << blockIdx)) != 0))
        {
            return result;
        }
        ImmutableArray<TrainTrackPillarInfoRel> pillarsData
            = friend.GetTransformedPillarsData(friend.TrackTransform.Transform90RotFlip);
        if (blockIdx >= pillarsData.Length)
        {
            return result;
        }
        TrainTrackPillarInfoRel baseRel = pillarsData[blockIdx];
        if (baseRel.Direction.IsZero)
        {
            return result;
        }
        result.Valid =
            trySide(track.TrackCenterTile, track.Id, manager, baseRel, extraTiles, left: true,
                out result.RelLeft, out result.InfoLeft)
            && trySide(track.TrackCenterTile, track.Id, manager, baseRel, extraTiles, left: false,
                out result.RelRight, out result.InfoRight);
        return result;
    }

    /// <summary>
    /// Finds the pillar spot for one side: the first valid spot sliding outward, plus the
    /// modifier combo's extra distance beyond it — so the extra clearance applies both on open
    /// ground and after sliding past an obstacle (e.g. the neighbouring track of a dual line),
    /// keeping each stance consistent.
    /// </summary>
    private static bool trySide(Tile3i trackCenter, EntityId exemptTrackId,
        TrainTracksPillarManager manager, TrainTrackPillarInfoRel baseRel, int extraTiles,
        bool left, out TrainTrackPillarInfoRel rel, out TrainTrackPillarInfo info)
    {
        if (!findSpot(trackCenter, exemptTrackId, manager, baseRel, MIN_OFFSET_HALF_TILES, left,
            out int foundHalfSteps, out rel, out info))
        {
            return false;
        }
        if (extraTiles <= 0)
        {
            return true;
        }
        return findSpot(trackCenter, exemptTrackId, manager, baseRel,
            foundHalfSteps + extraTiles * 2, left, out _, out rel, out info);
    }

    // --------------------------------------------- auto-portal support (used by AutoPortalPatch)

    /// <summary>
    /// Plan-time feasibility check for a track that does not exist yet (no entity to exempt):
    /// finds the closest-stance flanking pair for this block, returning the full pillar infos of
    /// both columns (used to rewrite the plan's pillar list so ghost and build get the real
    /// pair).
    /// </summary>
    internal static bool TryPlanPortalPairInfos(TrainTracksPillarManager manager,
        Tile3i trackCenter, TrainTrackPillarInfoRel baseRel,
        out TrainTrackPillarInfo leftInfo, out TrainTrackPillarInfo rightInfo)
    {
        leftInfo = default(TrainTrackPillarInfo);
        rightInfo = default(TrainTrackPillarInfo);
        if (!IsFunctional || baseRel.Direction.IsZero)
        {
            return false;
        }
        return trySide(trackCenter, default(EntityId), manager, baseRel, 0, left: true,
                out _, out leftInfo)
            && trySide(trackCenter, default(EntityId), manager, baseRel, 0, left: false,
                out _, out rightInfo);
    }

    /// <summary>
    /// Boolean form of <see cref="TryPlanPortalPairInfos"/>: would the pair fit? On success
    /// returns the left column's height/ground so the caller can report plausible values for the
    /// (blocked) centre placement it is standing in for.
    /// </summary>
    internal static bool TryPlanPortalPair(TrainTracksPillarManager manager, Tile3i trackCenter,
        TrainTrackPillarInfoRel baseRel, out ThicknessTilesF pillarHeight,
        out HeightTilesF groundHeight)
    {
        if (!TryPlanPortalPairInfos(manager, trackCenter, baseRel,
            out TrainTrackPillarInfo leftInfo, out _))
        {
            pillarHeight = default(ThicknessTilesF);
            groundHeight = default(HeightTilesF);
            return false;
        }
        pillarHeight = leftInfo.Height;
        groundHeight = new HeightTilesF(leftInfo.Offset.Z);
        return true;
    }

    /// <summary>
    /// Build-time substitution: places the closest-stance flanking pair for the given block of a
    /// freshly built track whose planned centre pillar cannot be placed, adopting spanned
    /// same-level tracks exactly like a portal click. Never leaves half a portal behind.
    /// </summary>
    internal static bool TryPlacePortalPair(TrainTracksPillarManager manager, TrainTrack track,
        TrainTrackPillarInfoRel baseRel, bool isFree, out string error)
    {
        error = "Cannot place pillar here";
        ITrainTrackMayBeElevatedFriend entity = track;
        if (!IsFunctional || baseRel.Direction.IsZero
            || (entity.PillarBlocksBitmap & (uint)(1 << baseRel.BlockIndex)) != 0)
        {
            return false;
        }
        if (!trySide(track.TrackCenterTile, track.Id, manager, baseRel, 0, left: true,
                out TrainTrackPillarInfoRel relLeft, out _)
            || !trySide(track.TrackCenterTile, track.Id, manager, baseRel, 0, left: false,
                out TrainTrackPillarInfoRel relRight, out _))
        {
            return false;
        }
        if (!tryAddOne(manager, entity, relLeft, isFree,
            out TrainTrackPillar first, out TrainTrackPillarInfo leftInfo, out error))
        {
            return false;
        }
        if (!tryAddOne(manager, entity, relRight, isFree,
            out _, out TrainTrackPillarInfo rightInfo, out error))
        {
            if (first != null)
            {
                manager.TryRemovePillar(first);
            }
            return false;
        }
        try
        {
            var entitiesManager = (EntitiesManager)s_entitiesManagerField.GetValue(manager);
            supportSpannedTracks(manager, entity, entitiesManager, leftInfo, rightInfo);
        }
        catch (Exception ex)
        {
            // Best-effort: the new track's own portal is already placed and valid.
            logOnce(ex);
        }
        error = "";
        return true;
    }

    /// <summary>
    /// Slides outward from the block's default pillar position perpendicular to the track
    /// direction in half-tile steps until a placeable, deck-free spot is found, capped at
    /// PILLAR_SUPPORT_DISTANCE. The candidate keeps the block's index (support is registered per
    /// block, not per position), its relative height (so the pillar top still meets deck level)
    /// and the track-aligned orientation.
    /// </summary>
    private static bool findSpot(Tile3i trackCenter, EntityId exemptTrackId,
        TrainTracksPillarManager manager, TrainTrackPillarInfoRel baseRel, int minHalfSteps,
        bool left, out int foundHalfSteps,
        out TrainTrackPillarInfoRel rel, out TrainTrackPillarInfo info)
    {
        foundHalfSteps = -1;
        rel = default(TrainTrackPillarInfoRel);
        info = default(TrainTrackPillarInfo);
        Fix32 maxOffset = TrainTrackConstants.PILLAR_SUPPORT_DISTANCE.Value;
        // Left orthogonal of the track tangent; the pillar itself stays track-aligned.
        var perpendicular = new RelTile2f(-baseRel.Direction.Y, baseRel.Direction.X);
        for (int halfSteps = minHalfSteps; ; halfSteps++)
        {
            Fix32 k = Fix32.FromInt(halfSteps).HalfFast;
            if (k > maxOffset)
            {
                return false;
            }
            RelTile2f offset = perpendicular * k;
            if (!left)
            {
                offset = -offset;
            }
            TrainTrackPillarInfoRel candidate;
            try
            {
                candidate = TrainTrackPillarInfoRel.FromPositionAndDirection(
                    baseRel.Position + offset, baseRel.RelHeight.Value, baseRel.Direction,
                    baseRel.BlockIndex);
            }
            catch
            {
                continue;
            }
            // Classify foreign decks at this spot BEFORE the placement check. A deck at the same
            // level is an obstacle to slide past (and later adopt); a deck ≥3.5 tiles below is
            // the crossing the portal exists for; anything in between — where the (future)
            // crossbeam would block trains below or leave a gap above — makes the whole side
            // invalid. The clicked track's own deck is exempt.
            int deckClass = classifyDecks(manager, exemptTrackId, trackCenter, baseRel, candidate);
            if (deckClass == DECK_CLASH)
            {
                return false;
            }
            if (!manager.CanPlacePillar(trackCenter, candidate,
                out ThicknessTilesF height, out HeightTilesF ground) || height.IsNotPositive)
            {
                continue;
            }
            if (deckClass == DECK_SAME_LEVEL)
            {
                // Standing under a same-level deck defeats the point — with a dual line the
                // inner candidate would stop underneath the neighbouring track (CanPlacePillar
                // tolerates a deck at the top of the column). Keep sliding.
                continue;
            }
            foundHalfSteps = halfSteps;
            rel = candidate;
            info = new TrainTrackPillarInfo(
                trackCenter.Xy.CornerTile2f.ExtendHeight(ground), height, candidate);
            return true;
        }
    }

    private const int DECK_NONE = 0;
    private const int DECK_SAME_LEVEL = 1;
    private const int DECK_CLASH = 2;

    /// <summary>
    /// Scans the candidate pillar's footprint from the ground up past the clicked track's deck
    /// level and classifies every foreign track deck found: DECK_SAME_LEVEL when it runs at the
    /// clicked deck's level (±1.5 tiles — an obstacle to slide past and later adopt), DECK_NONE
    /// when it is ≥3.5 tiles below (a proper crossing underneath, with train clearance under the
    /// portal), and DECK_CLASH for the awkward in-between band and decks slightly above, where a
    /// portal crossbeam could never fit. Mirrors the tile iteration of the vanilla placement
    /// check; the clicked track itself (exemptTrackId; may be a default id when the track being
    /// planned does not exist yet) is ignored.
    /// </summary>
    private static int classifyDecks(TrainTracksPillarManager manager, EntityId exemptTrackId,
        Tile3i trackCenter, TrainTrackPillarInfoRel baseRel, TrainTrackPillarInfoRel candidate)
    {
        var terrain = (TerrainManager)s_terrainManagerField.GetValue(manager);
        var occupancy = (TerrainOccupancyManager)s_occupancyManagerField.GetValue(manager);
        var entitiesManager = (EntitiesManager)s_entitiesManagerField.GetValue(manager);
        Fix32 clickedDeck = trackCenter.Z + baseRel.RelHeight.Value;
        Fix32 sameLevelTolerance = Fix32.FromInt(3).HalfFast;   // 1.5 tiles
        Fix32 crossingClearance = Fix32.FromInt(7).HalfFast;    // 3.5 tiles
        int result = DECK_NONE;
        var occupantIds = new Lyst<EntityId>();
        int maskSize = TrainTrackPillarProto.OCCUPANCY_MASK_SIZE.Value;
        for (int i = 0; i < maskSize; i++)
        {
            for (int j = 0; j < maskSize; j++)
            {
                if ((candidate.OccupancyMask & (1 << i * maskSize + j)) == 0L)
                {
                    continue;
                }
                Tile2f tile = trackCenter.Xy.CornerTile2f
                    + (candidate.Position + new RelTile2f(j, i) - TrainTrackPillarProto.OCCUPANCY_MASK_CENTRE);
                var tileIndex = terrain.GetTileIndex(tile.Tile2i);
                if (!terrain.IsValidIndex(tileIndex))
                {
                    continue;
                }
                HeightTilesI fromHeight = terrain.GetHeight(tile).TilesHeightFloored;
                // Span from the ground to a bit above the clicked deck, so flat decks whose
                // occupied cells start at deck level are seen too.
                int spanTiles = (clickedDeck + Fix32.FromInt(3) - Fix32.FromInt(fromHeight.Value))
                    .ToIntCeiled().Max(1);
                occupantIds.Clear();
                occupancy.GetAllOccupyingEntitiesInRange(tileIndex, fromHeight,
                    new ThicknessTilesI(spanTiles), occupantIds);
                Lyst<EntityId>.Enumerator enumerator = occupantIds.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    EntityId occupantId = enumerator.Current;
                    if (occupantId == exemptTrackId)
                    {
                        continue;
                    }
                    if (entitiesManager.TryGetEntity(occupantId, out TrainTrack otherTrack))
                    {
                        Fix32 otherDeck = deckLevelNear(otherTrack, tile);
                        Fix32 delta = otherDeck - clickedDeck;
                        if (delta.Abs() <= sameLevelTolerance)
                        {
                            result = DECK_SAME_LEVEL;
                        }
                        else if (delta > -crossingClearance)
                        {
                            return DECK_CLASH;
                        }
                        // else: far enough below — the crossing the portal is for.
                    }
                    else if (entitiesManager.TryGetEntity(occupantId, out ITrainTrackMayBeElevatedFriend _))
                    {
                        // Stations and other track-bearing layouts: treat as same-level obstacle.
                        result = DECK_SAME_LEVEL;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>Deck level (absolute Z of the rails) of the given track near a position,
    /// estimated from its nearest per-block pillar data.</summary>
    private static Fix32 deckLevelNear(TrainTrack track, Tile2f position)
    {
        ITrainTrackMayBeElevatedFriend friend = track;
        ImmutableArray<TrainTrackPillarInfoRel> pillarsData
            = friend.GetTransformedPillarsData(friend.TrackTransform.Transform90RotFlip);
        Tile2f corner = track.TrackCenterTile.Xy.CornerTile2f;
        Fix32 relHeight = Fix32.Zero;
        Fix64 bestDistance = Fix64.MaxValue;
        for (int i = 0; i < pillarsData.Length; i++)
        {
            Fix64 distance = ((corner + pillarsData[i].Position) - position).LengthSqr;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                relHeight = pillarsData[i].RelHeight.Value;
            }
        }
        return track.TrackCenterTile.Z + relHeight;
    }

    // ------------------------------------------------------- command side (sim thread)

    private static bool AddPillarCmdPrefix(TrainTracksPillarManager __instance, AddTrainTrackPillarCmd cmd)
    {
        PendingSideAdd pending = s_pending;
        if (pending == null || pending.TrackId != cmd.EntityId || pending.BlockIdx != cmd.BlockIdx)
        {
            return true;
        }
        s_pending = null;
        try
        {
            // Keep the auto-portal TryAddPillar substitution out of this manual flow — every add
            // below is pre-validated, and its failure paths must fail plainly, not recurse.
            AutoPortalPatch.SuppressSubstitution = true;
            var entitiesManager = (EntitiesManager)s_entitiesManagerField.GetValue(__instance);
            if (!entitiesManager.TryGetEntity(cmd.EntityId, out ITrainTrackMayBeElevatedFriend entity))
            {
                cmd.SetResultError("Failed to find entity");
                return false;
            }
            if (!tryAddOne(__instance, entity, pending.RelLeft, entity.IsTrackEnabled,
                out TrainTrackPillar first, out TrainTrackPillarInfo leftInfo, out string error))
            {
                cmd.SetResultError(error);
                return false;
            }
            if (!tryAddOne(__instance, entity, pending.RelRight, entity.IsTrackEnabled,
                out _, out TrainTrackPillarInfo rightInfo, out error))
            {
                // Never leave half a portal behind.
                if (first != null)
                {
                    __instance.TryRemovePillar(first);
                }
                cmd.SetResultError(error);
                return false;
            }
            try
            {
                supportSpannedTracks(__instance, entity, entitiesManager, leftInfo, rightInfo);
            }
            catch (Exception ex)
            {
                // Best-effort: the clicked track's portal is already placed and valid.
                logOnce(ex);
            }
            cmd.SetResultSuccess();
            return false;
        }
        catch (Exception ex)
        {
            logOnce(ex);
            cmd.SetResultError("Elevation++: side pillar placement failed.");
            return false;
        }
        finally
        {
            AutoPortalPatch.SuppressSubstitution = false;
        }
    }

    private static bool tryAddOne(TrainTracksPillarManager manager, ITrainTrackMayBeElevatedFriend entity,
        TrainTrackPillarInfoRel rel, bool isFree, out TrainTrackPillar added,
        out TrainTrackPillarInfo info, out string error)
    {
        added = null;
        if (!manager.CanPlacePillar(entity.TrackCenterTile, rel,
            out ThicknessTilesF height, out HeightTilesF ground) || height.IsNotPositive)
        {
            info = default(TrainTrackPillarInfo);
            error = "Cannot place pillar here";
            return false;
        }
        info = new TrainTrackPillarInfo(
            entity.TrackCenterTile.Xy.CornerTile2f.ExtendHeight(ground), height, rel);
        int countBefore = entity.Pillars.Length;
        if (!manager.TryAddPillar(info, entity, isFree, out error))
        {
            return false;
        }
        if (entity.Pillars.Length > countBefore)
        {
            added = entity.Pillars[countBefore];
        }
        return true;
    }

    /// <summary>
    /// After the clicked track's portal is placed, gives every other elevated track spanned by
    /// it (e.g. the neighbouring track of a dual line) its own pillars at the exact same two
    /// column positions, so one click supports the whole portal. Support bookkeeping is
    /// per-track, so co-located duplicates are the clean way to share a column — identical
    /// geometry at an identical transform renders without artifacts, and each track keeps its
    /// own removal safety validation. Tracks at a different level (like the crossing track the
    /// portal exists for) are skipped via the pillar-height comparison.
    /// </summary>
    private static void supportSpannedTracks(TrainTracksPillarManager manager,
        ITrainTrackMayBeElevatedFriend clicked, EntitiesManager entitiesManager,
        TrainTrackPillarInfo leftInfo, TrainTrackPillarInfo rightInfo)
    {
        var terrain = (TerrainManager)s_terrainManagerField.GetValue(manager);
        var occupancy = (TerrainOccupancyManager)s_occupancyManagerField.GetValue(manager);
        Tile2f leftPos = leftInfo.Position2f;
        Tile2f rightPos = rightInfo.Position2f;
        RelTile2f span = leftPos - rightPos;
        ThicknessTilesI scanThickness = leftInfo.Height.CeiledThicknessTilesI + new ThicknessTilesI(3);

        // Collect other train tracks whose deck crosses the strip between the two columns.
        var seenIds = new HashSet<EntityId>();
        var spannedTracks = new List<TrainTrack>();
        var occupantIds = new Lyst<EntityId>();
        int sampleCount = span.Length.ToIntCeiled().Max(1);
        for (int i = 0; i <= sampleCount; i++)
        {
            Tile2f sample = rightPos + span * (Fix32.FromInt(i) / Fix32.FromInt(sampleCount));
            var tileIndex = terrain.GetTileIndex(sample.Tile2i);
            if (!terrain.IsValidIndex(tileIndex))
            {
                continue;
            }
            occupantIds.Clear();
            occupancy.GetAllOccupyingEntitiesInRange(tileIndex,
                terrain.GetHeight(sample).TilesHeightFloored, scanThickness, occupantIds);
            Lyst<EntityId>.Enumerator enumerator = occupantIds.GetEnumerator();
            while (enumerator.MoveNext())
            {
                EntityId occupantId = enumerator.Current;
                if (occupantId == clicked.Id || !seenIds.Add(occupantId))
                {
                    continue;
                }
                if (entitiesManager.TryGetEntity(occupantId, out TrainTrack otherTrack))
                {
                    spannedTracks.Add(otherTrack);
                }
            }
        }

        Tile2f portalCenter = rightPos + span / Fix32.FromInt(2);
        foreach (TrainTrack other in spannedTracks)
        {
            ITrainTrackMayBeElevatedFriend friend = other;
            ImmutableArray<TrainTrackPillarInfoRel> pillarsData
                = friend.GetTransformedPillarsData(friend.TrackTransform.Transform90RotFlip);
            if (pillarsData.IsEmpty)
            {
                continue;
            }
            // The other track's block nearest to the portal.
            int blockIdx = 0;
            Fix64 bestDistance = Fix64.MaxValue;
            Tile2f otherCorner = other.TrackCenterTile.Xy.CornerTile2f;
            for (int i = 0; i < pillarsData.Length; i++)
            {
                Fix64 distance = ((otherCorner + pillarsData[i].Position) - portalCenter).LengthSqr;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    blockIdx = i;
                }
            }
            // Validate the two co-located placements for this track before touching anything.
            var newInfos = new List<TrainTrackPillarInfo>(2);
            bool levelMismatch = false;
            foreach (TrainTrackPillarInfo placed in new[] { leftInfo, rightInfo })
            {
                TrainTrackPillarInfoRel rel;
                try
                {
                    rel = TrainTrackPillarInfoRel.FromPositionAndDirection(
                        placed.Position2f - otherCorner, pillarsData[blockIdx].RelHeight.Value,
                        pillarsData[blockIdx].Direction, blockIdx);
                }
                catch
                {
                    continue;
                }
                if (!manager.CanPlacePillar(other.TrackCenterTile, rel,
                    out ThicknessTilesF height, out HeightTilesF ground) || height.IsNotPositive)
                {
                    continue;
                }
                // A track at a different level (e.g. the crossing track underneath) would need a
                // very different pillar height — that is not a track this portal carries.
                if ((height.Value - placed.Height.Value).Abs() > Fix32.FromInt(3).HalfFast)
                {
                    levelMismatch = true;
                    break;
                }
                newInfos.Add(new TrainTrackPillarInfo(otherCorner.ExtendHeight(ground), height, rel));
            }
            if (levelMismatch || newInfos.Count == 0)
            {
                continue;
            }
            // The portal takes over the adopted block's support: its existing pillars (a vanilla
            // centre pillar standing inside the portal — possibly shifted along the track — or
            // an older pair) are removed and replaced by the co-located columns. Support returns
            // immediately at the same block; if placement fails the originals are restored.
            var backups = new List<TrainTrackPillarInfo>();
            if ((friend.PillarBlocksBitmap & (uint)(1 << blockIdx)) != 0)
            {
                var oldGroup = new List<TrainTrackPillar>();
                ReadOnlyArraySlice<TrainTrackPillar>.Enumerator pillarEnumerator = friend.Pillars.GetEnumerator();
                while (pillarEnumerator.MoveNext())
                {
                    if (pillarEnumerator.Current.BlockIndex == blockIdx)
                    {
                        oldGroup.Add(pillarEnumerator.Current);
                    }
                }
                foreach (TrainTrackPillar old in oldGroup)
                {
                    backups.Add(old.PillarInfo);
                }
                foreach (TrainTrackPillar old in oldGroup)
                {
                    tryRemoveLogged(manager, old);
                }
            }
            int addedCount = 0;
            foreach (TrainTrackPillarInfo info in newInfos)
            {
                if (manager.TryAddPillar(info, friend, friend.IsTrackEnabled, out _))
                {
                    addedCount++;
                }
            }
            if (addedCount == 0 && backups.Count > 0)
            {
                restorePillars(manager, friend, backups);
            }
        }
    }

    /// <summary>
    /// Removing any pillar of a portal removes the whole portal in one click: all pillars of the
    /// clicked pillar's (track, block) — the engine tracks pillars per block as a single bitmap
    /// bit, so leaving one of a pair behind would desync it — plus the co-located pillars other
    /// tracks received when the portal adopted them. Another track's share is only demolished
    /// when that track can afford to lose the support (the same CanRemoveSupportAtBlock check
    /// the vanilla tool applies to the clicked track); otherwise its pillars stay and keep the
    /// column standing. Blocks with a single plain pillar take the vanilla path.
    /// </summary>
    private static bool RemovePillarCmdPrefix(TrainTracksPillarManager __instance, RemoveTrainTrackPillarCmd cmd)
    {
        try
        {
            // Same suppression as the add prefix: replacement/demolition flows restore or
            // duplicate pre-validated pillars and must not trigger auto-portal substitution.
            AutoPortalPatch.SuppressSubstitution = true;
            PendingReplace replace = s_pendingReplace;
            if (replace != null && replace.PillarId == cmd.PillarId)
            {
                s_pendingReplace = null;
                return replacePillarWithPair(__instance, cmd, replace);
            }
            var entitiesManager = (EntitiesManager)s_entitiesManagerField.GetValue(__instance);
            if (!entitiesManager.TryGetEntity(cmd.PillarId, out TrainTrackPillar pillar))
            {
                return true;
            }
            List<TrainTrackPillar> clickedGroup = collectBlockGroup(pillar);
            List<List<TrainTrackPillar>> crossGroups
                = collectCoLocatedGroups(__instance, entitiesManager, clickedGroup);
            if (clickedGroup.Count == 1 && crossGroups.Count == 0)
            {
                return true;
            }
            EntityValidationResult result = __instance.TryRemovePillar(pillar);
            if (result.IsError)
            {
                cmd.SetResultError(result.ErrorMessage);
                return false;
            }
            foreach (TrainTrackPillar sibling in clickedGroup)
            {
                if (sibling != pillar)
                {
                    tryRemoveLogged(__instance, sibling);
                }
            }
            TrainTracksGraphManager graph = s_graphManager;
            foreach (List<TrainTrackPillar> group in crossGroups)
            {
                TrainTrackPillar sample = group[0];
                if (graph == null
                    || !graph.TrackEntities.TryGetValue(sample.TrainTrackEntityId, out TrainTrackId trackId)
                    || !graph.CanRemoveSupportAtBlock(sample.BlockIndex, trackId, isForElectrification: false))
                {
                    continue;
                }
                foreach (TrainTrackPillar member in group)
                {
                    tryRemoveLogged(__instance, member);
                }
            }
            cmd.SetResultSuccess();
            return false;
        }
        catch (Exception ex)
        {
            logOnce(ex);
            return true;
        }
        finally
        {
            AutoPortalPatch.SuppressSubstitution = false;
        }
    }

    /// <summary>All pillars of the given pillar's track that share its block index (including
    /// the pillar itself).</summary>
    private static List<TrainTrackPillar> collectBlockGroup(TrainTrackPillar pillar)
    {
        var group = new List<TrainTrackPillar>();
        ReadOnlyArraySlice<TrainTrackPillar>.Enumerator enumerator = pillar.TrainTrack.Pillars.GetEnumerator();
        while (enumerator.MoveNext())
        {
            if (enumerator.Current.BlockIndex == pillar.BlockIndex)
            {
                group.Add(enumerator.Current);
            }
        }
        return group;
    }

    /// <summary>
    /// Finds pillars of OTHER tracks standing at (practically) the same spots as the given
    /// group — the co-located duplicates a portal creates for the tracks it spans — and returns
    /// them grouped per (track, block).
    /// </summary>
    private static List<List<TrainTrackPillar>> collectCoLocatedGroups(TrainTracksPillarManager manager,
        EntitiesManager entitiesManager, List<TrainTrackPillar> clickedGroup)
    {
        var groups = new List<List<TrainTrackPillar>>();
        var terrain = (TerrainManager)s_terrainManagerField.GetValue(manager);
        var occupancy = (TerrainOccupancyManager)s_occupancyManagerField.GetValue(manager);
        var captured = new HashSet<TrainTrackPillar>(clickedGroup);
        var occupantIds = new Lyst<EntityId>();
        Fix32 tolerance = Fix32.One.HalfFast;
        foreach (TrainTrackPillar member in clickedGroup)
        {
            Tile2f position = member.PillarPosition.Xy;
            var tileIndex = terrain.GetTileIndex(position.Tile2i);
            if (!terrain.IsValidIndex(tileIndex))
            {
                continue;
            }
            occupantIds.Clear();
            occupancy.GetAllOccupyingEntitiesInRange(tileIndex,
                terrain.GetHeight(position).TilesHeightFloored,
                member.Height.CeiledThicknessTilesI, occupantIds);
            Lyst<EntityId>.Enumerator enumerator = occupantIds.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (!entitiesManager.TryGetEntity(enumerator.Current, out TrainTrackPillar other)
                    || captured.Contains(other))
                {
                    continue;
                }
                RelTile2f delta = other.PillarPosition.Xy - position;
                if (delta.X.Abs() > tolerance || delta.Y.Abs() > tolerance)
                {
                    continue;
                }
                List<TrainTrackPillar> group = collectBlockGroup(other);
                groups.Add(group);
                foreach (TrainTrackPillar groupMember in group)
                {
                    captured.Add(groupMember);
                }
            }
        }
        return groups;
    }

    private static void tryRemoveLogged(TrainTracksPillarManager manager, TrainTrackPillar pillar)
    {
        EntityValidationResult result = manager.TryRemovePillar(pillar);
        if (result.IsError)
        {
            Log.Warning("Elevation++: failed to remove portal pillar: " + result.ErrorMessage);
        }
    }

    /// <summary>
    /// Executes a modifier-click on an existing pillar: swaps the clicked pillar's block —
    /// centre pillar or an existing pair — for the newly proposed flanking pair. The new spots
    /// are validated BEFORE anything is removed, and if placement still fails afterwards the
    /// original pillars are restored from their infos, so the track never loses its support.
    /// </summary>
    private static bool replacePillarWithPair(TrainTracksPillarManager manager,
        RemoveTrainTrackPillarCmd cmd, PendingReplace replace)
    {
        try
        {
            var entitiesManager = (EntitiesManager)s_entitiesManagerField.GetValue(manager);
            if (!entitiesManager.TryGetEntity(cmd.PillarId, out TrainTrackPillar pillar))
            {
                cmd.SetResultError("Failed to find entity");
                return false;
            }
            ITrainTrackMayBeElevatedFriend entity = pillar.TrainTrack;
            if (!manager.CanPlacePillar(entity.TrackCenterTile, replace.RelLeft, out ThicknessTilesF h1, out _)
                || h1.IsNotPositive
                || !manager.CanPlacePillar(entity.TrackCenterTile, replace.RelRight, out ThicknessTilesF h2, out _)
                || h2.IsNotPositive)
            {
                cmd.SetResultError("Cannot place pillar here");
                return false;
            }

            List<TrainTrackPillar> oldGroup = collectBlockGroup(pillar);
            var backups = new List<TrainTrackPillarInfo>();
            foreach (TrainTrackPillar old in oldGroup)
            {
                backups.Add(old.PillarInfo);
            }
            foreach (TrainTrackPillar old in oldGroup)
            {
                tryRemoveLogged(manager, old);
            }

            if (!tryAddOne(manager, entity, replace.RelLeft, entity.IsTrackEnabled,
                out TrainTrackPillar first, out TrainTrackPillarInfo leftInfo, out string error))
            {
                restorePillars(manager, entity, backups);
                cmd.SetResultError(error);
                return false;
            }
            if (!tryAddOne(manager, entity, replace.RelRight, entity.IsTrackEnabled,
                out _, out TrainTrackPillarInfo rightInfo, out error))
            {
                if (first != null)
                {
                    manager.TryRemovePillar(first);
                }
                restorePillars(manager, entity, backups);
                cmd.SetResultError(error);
                return false;
            }
            try
            {
                supportSpannedTracks(manager, entity, entitiesManager, leftInfo, rightInfo);
            }
            catch (Exception ex)
            {
                logOnce(ex);
            }
            cmd.SetResultSuccess();
            return false;
        }
        catch (Exception ex)
        {
            logOnce(ex);
            cmd.SetResultError("Elevation++: pillar replacement failed.");
            return false;
        }
    }

    private static void restorePillars(TrainTracksPillarManager manager,
        ITrainTrackMayBeElevatedFriend entity, List<TrainTrackPillarInfo> backups)
    {
        foreach (TrainTrackPillarInfo info in backups)
        {
            if (!manager.TryAddPillar(info, entity, entity.IsTrackEnabled, out string error))
            {
                Log.Warning("Elevation++: failed to restore replaced pillar: " + error);
            }
        }
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"Elevation++: side track pillars patch failed (logged once): {ex}");
        }
    }
}
