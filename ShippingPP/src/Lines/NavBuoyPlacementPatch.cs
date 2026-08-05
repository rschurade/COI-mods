using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Commands;

namespace ShippingPP.Lines;

/// <summary>
/// Keeps navigation buoys at the ocean surface instead of the ocean floor.
///
/// The build cursor projects onto the terrain under the water (the shared terrain cursor usually
/// has ocean intersection disabled), so an ocean-only entity gets a transform whose Z is the
/// ocean-floor height — in deep water the whole mast ends up submerged and invisible. Since the
/// buoy is a pure route marker with no simulation behavior, its Z can be normalized to sea level
/// with no side effects: a prefix on both static-entity build commands' <c>TryCreateEntity</c>
/// (single placement and batch/blueprint placement) rewrites the transform before the entity is
/// created, so occupancy, rendering and the save all agree. A third prefix on the placement
/// preview's <c>SetTransform</c> lifts the construction ghost the same way, otherwise the ghost
/// would still drown while dragging. Buoys already placed before this patch keep their old depth;
/// re-place them (they are free).
/// </summary>
internal static class NavBuoyPlacementPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.buoyplacement";

    /// <summary>
    /// Sim height of the ocean surface (<c>TerrainManager.GetHeightOrOceanSurface</c> returns 0
    /// for ocean tiles). The water plane renders slightly above it, so the mast base sits just
    /// below the waterline like a real buoy.
    /// </summary>
    private const int SEA_LEVEL_Z = 0;

    private static bool s_applied;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            var prefix = new HarmonyMethod(typeof(NavBuoyPlacementPatch), nameof(CreatePrefix));
            harmony.Patch(
                AccessTools.Method(typeof(CreateStaticEntityCmd),
                    nameof(CreateStaticEntityCmd.TryCreateEntity)),
                prefix: prefix);
            harmony.Patch(
                AccessTools.Method(typeof(BatchCreateStaticEntitiesCmd),
                    nameof(BatchCreateStaticEntitiesCmd.TryCreateEntity)),
                prefix: prefix);
            Log.Info("Shipping++: buoy placement patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply buoy placement patch: {ex}");
        }

        // The preview lives in Mafi.Unity; resolve defensively so a UI-less run just skips it.
        try
        {
            Type previewType = AccessTools.TypeByName(
                "Mafi.Unity.InputControl.Factory.LayoutEntityPreview");
            MethodInfo setTransform = previewType?.GetMethod("SetTransform",
                BindingFlags.Public | BindingFlags.Instance);
            if (setTransform == null)
            {
                Log.Info("Shipping++: LayoutEntityPreview.SetTransform not found (headless "
                    + "run?); buoy preview patch skipped.");
                return;
            }
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(setTransform, prefix: new HarmonyMethod(
                typeof(NavBuoyPlacementPatch), nameof(PreviewSetTransformPrefix)));
            Log.Info("Shipping++: buoy preview patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply buoy preview patch: {ex}");
        }
    }

    private static void CreatePrefix(IStaticEntityProto proto, ref TileTransform transform)
    {
        if (proto is NavBuoyProto)
        {
            transform = AtSeaLevel(transform);
        }
    }

    private static void PreviewSetTransformPrefix(object __instance, ref TileTransform transform)
    {
        if (__instance is Mafi.Unity.InputControl.Factory.LayoutEntityPreview preview
            && preview.EntityProto is NavBuoyProto)
        {
            transform = AtSeaLevel(transform);
        }
    }

    private static TileTransform AtSeaLevel(in TileTransform transform)
    {
        if (transform.Position.Z == SEA_LEVEL_Z)
        {
            return transform;
        }
        return new TileTransform(transform.Position.Xy.ExtendZ(SEA_LEVEL_Z),
            transform.Rotation, transform.IsReflected);
    }
}
