using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core.Factory.Transports;
using Mafi.Unity.InputControl;
using Mafi.Unity.Ui.Controllers.Trains;

namespace ElevationPP;

/// <summary>
/// Height-aware track snapping for the rail build tool.
///
/// Vanilla snaps the build cursor to the nearest end of any hovered/nearby track — position AND
/// height — and then overwrites the cursor's raised height from the snapped result. Laying an
/// elevated line over an existing network is maddening: raise the cursor to +4, brush the mouse
/// over any ground track, and the height resets to 0. (The vanilla "disable snapping" toggle
/// exists but kills connection snapping wholesale.)
///
/// This patch keeps snapping height-aware instead: while the cursor is manually raised
/// (RelativeHeight &gt; 0), a snap result whose endpoint lies at a clearly different height
/// (&gt; 1 tile) is discarded and replaced by the plain free-position pick, so the cursor — and
/// its height — stay where the player put them. Ground-level picking (RelativeHeight == 0) and
/// snapping to tracks at the matching height (extending an elevated line) behave exactly like
/// vanilla. Configurable via RailBuildHeightAwareSnapping (true by default; false = vanilla).
///
/// Implementation: postfix on TrainTrackBuildController.pickAtCursorPosition. Its PickResult is
/// a private nested struct, so the result is handled as a boxed object via cached reflection;
/// the replacement mirrors the method's own free-position fallback (lowest non-colliding height,
/// ceiled fractional Z) and also refreshes m_previousPickResult, which the click buffer reuses.
/// </summary>
internal static class TrackSnapHeightPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.tracksnapheight";

    /// <summary>Config switch (RailBuildHeightAwareSnapping); written by the settings UI
    /// handler, read every pick.</summary>
    internal static volatile bool Enabled = true;

    // Snapped endpoints within this height difference of the cursor plane still snap — covers
    // half-tile track node heights without letting a ground track capture a +2 cursor.
    private static readonly Fix32 SNAP_HEIGHT_TOLERANCE = Fix32.One;

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    private static FieldInfo s_terrainCursorField;
    private static FieldInfo s_previousPickResultField;
    private static FieldInfo s_positionField;
    private static FieldInfo s_pickedTrackField;
    private static PropertyInfo s_hasValueProp;
    private static ConstructorInfo s_pickResultCtor;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        Type controller = typeof(TrainTrackBuildController);
        MethodBase pick = AccessTools.Method(controller, "pickAtCursorPosition");
        s_terrainCursorField = AccessTools.Field(controller, "m_terrainCursor");
        s_previousPickResultField = AccessTools.Field(controller, "m_previousPickResult");
        Type pickResultType = s_previousPickResultField?.FieldType;
        if (pickResultType != null)
        {
            s_positionField = AccessTools.Field(pickResultType, "Position");
            s_pickedTrackField = AccessTools.Field(pickResultType, "PickedTrack");
            s_hasValueProp = s_pickedTrackField?.FieldType.GetProperty("HasValue");
            foreach (ConstructorInfo ctor in pickResultType.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (ctor.GetParameters().Length == 5)
                {
                    s_pickResultCtor = ctor;
                    break;
                }
            }
        }
        if (pick == null || s_terrainCursorField == null || s_positionField == null
            || s_pickedTrackField == null || s_hasValueProp == null || s_pickResultCtor == null)
        {
            Log.Error("Elevation++: track build tool internals not resolved; "
                + "height-aware snapping patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(pick,
                postfix: new HarmonyMethod(typeof(TrackSnapHeightPatch), nameof(PickPostfix)));
            Log.Info("Elevation++: height-aware track snapping patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply height-aware snapping patch: {ex}");
        }
    }

    private static void PickPostfix(TrainTrackBuildController __instance, ref object __result)
    {
        if (!Enabled || __result == null)
        {
            return;
        }
        try
        {
            object pickedTrack = s_pickedTrackField.GetValue(__result);
            if (!(bool)s_hasValueProp.GetValue(pickedTrack, null))
            {
                return;
            }
            var cursor = (TerrainCursor)s_terrainCursorField.GetValue(__instance);
            if (cursor == null || !cursor.HasValue || cursor.RelativeHeight.Value <= 0)
            {
                return;
            }
            var snapped = (Tile3f)s_positionField.GetValue(__result);
            Tile3f intended = cursor.Tile3f;
            if ((snapped.Z - intended.Z).Abs() <= SNAP_HEIGHT_TOLERANCE)
            {
                return;
            }

            // Same free-position fallback the method itself uses when nothing snaps.
            HeightTilesI lowest = TransportHelper.GetLowestNonCollidingHeight(cursor.Tile);
            Tile3f free = intended;
            if (free.Height < lowest)
            {
                free = free.SetZ(lowest.Value);
            }
            else if (free.Z.FractionalPart.IsNotZero)
            {
                free = free.SetZ(free.Z.ToIntCeiled());
            }
            object replacement
                = s_pickResultCtor.Invoke(new object[] { free, null, null, null, false });
            s_previousPickResultField.SetValue(__instance, replacement);
            __result = replacement;
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
            Log.Error($"Elevation++: height-aware snapping patch failed (logged once): {ex}");
        }
    }
}
