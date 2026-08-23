using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Unity.InputControl;
using Mafi.Unity.InputControl.Factory;
using Mafi.Unity.Ui.Controllers.LayoutEntityPlacing;

namespace ElevationPP.Platforms;

/// <summary>
/// Lets the building cursor land ON a concrete platform.
///
/// The generic building placer (<c>StaticEntityMassPlacer</c>) projects the mouse onto the
/// TERRAIN (a raycast against the terrain mesh, plus the manual height offset elevatable protos
/// allow) — an ordinary building's placement height range is 0..0, so it can only ever be
/// placed at ground level: pointing at a platform would put the building on the ground under it,
/// colliding with the platform. A postfix on the placer's <c>getCursorPosition</c> intersects the
/// camera ray with the horizontal plane of every platform deck height in the world (highest first,
/// i.e. nearest to the camera along a downward ray) and, when that intersection lands on a
/// platform deck, snaps the cursor to that point at deck-top height — the building then previews
/// and places on the platform, validated by <see cref="PlatformSupportPatch"/>. Elsewhere the cursor
/// stays on the terrain. Only ordinary buildings snap (<see cref="PlatformSupport.IsCandidateProto"/>);
/// elevatable protos keep their manual height control, and removal mode is untouched.
/// </summary>
internal static class PlatformSnapPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.platesnap";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static FieldInfo s_terrainCursorField;   // StaticEntityMassPlacer.m_terrainCursor
    private static FieldInfo s_previewsField;        // StaticEntityMassPlacer.m_entityPreviews
    private static FieldInfo s_removalField;         // StaticEntityMassPlacer.m_isInRemovalMode

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodBase getCursor = AccessTools.Method(typeof(StaticEntityMassPlacer), "getCursorPosition");
        s_terrainCursorField = AccessTools.Field(typeof(StaticEntityMassPlacer), "m_terrainCursor");
        s_previewsField = AccessTools.Field(typeof(StaticEntityMassPlacer), "m_entityPreviews");
        s_removalField = AccessTools.Field(typeof(StaticEntityMassPlacer), "m_isInRemovalMode");
        if (getCursor == null || s_terrainCursorField == null || s_previewsField == null
            || s_removalField == null)
        {
            Log.Error("Elevation++: building placer internals not resolved; the cursor will not "
                + "snap onto concrete platforms.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(getCursor, postfix: new HarmonyMethod(typeof(PlatformSnapPatch),
                nameof(GetCursorPositionPostfix)));
            Log.Info("Elevation++: platform cursor snapping patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply platform cursor snapping patch: {ex}");
        }
    }

    private static void GetCursorPositionPostfix(StaticEntityMassPlacer __instance,
        (RelTile2i size, Rotation90 rotation)? hints, ref Tile3i __result)
    {
        try
        {
            if (!PlatformSupport.IsActive)
            {
                return;
            }
            IReadOnlyList<int> deckHeights = PlatformSupport.DeckTopHeightsDescending;
            if (deckHeights.Count == 0 || (bool)s_removalField.GetValue(__instance))
            {
                return;
            }
            if (!(s_previewsField.GetValue(__instance)
                    is Lyst<KeyValuePair<IStaticEntityPreview, EntityConfigData>> previews)
                || previews.Count != 1
                || !PlatformSupport.IsCandidateProto(previews[0].Key.EntityProto))
            {
                return;
            }
            if (!(s_terrainCursorField.GetValue(__instance) is TerrainCursor cursor))
            {
                return;
            }

            for (int i = 0; i < deckHeights.Count; i++)
            {
                var deckTop = new HeightTilesI(deckHeights[i]);
                if (!cursor.TryComputePositionAtHeight(deckTop, out Tile3f hit))
                {
                    continue;
                }
                if (!PlatformSupport.TryGetPlatformUnder(hit.Xy.Tile2i, deckTop, out ConcretePlatform platform)
                    || platform.DeckTopHeight != deckTop)
                {
                    continue;
                }
                __result = roundLikeVanilla(hit, hints, deckTop);
                return;
            }
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    /// <summary>The placer's own rounding: even-sized footprints snap to tile corners, odd
    /// ones to tile centres.</summary>
    private static Tile3i roundLikeVanilla(Tile3f point, (RelTile2i size, Rotation90 rotation)? hints,
        HeightTilesI height)
    {
        if (hints.HasValue)
        {
            RelTile2i rotated = hints.Value.size.Rotate(hints.Value.rotation);
            return new Tile3i(
                rotated.X % 2 == 0 ? point.X.ToIntRounded() : point.X.ToIntFloored(),
                rotated.Y % 2 == 0 ? point.Y.ToIntRounded() : point.Y.ToIntFloored(),
                height.Value);
        }
        return new Tile3i(point.X.ToIntRounded(), point.Y.ToIntRounded(), height.Value);
    }

    private static void logOnce(Exception ex)
    {
        if (!s_runtimeErrorLogged)
        {
            s_runtimeErrorLogged = true;
            Log.Error($"Elevation++: platform cursor snapping failed (logged once): {ex}");
        }
    }
}
