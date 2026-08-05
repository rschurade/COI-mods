using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Terrain;
using Mafi.Localization;
using Mafi.Unity.Camera;
using Mafi.Unity.InputControl.Factory;
using Mafi.Unity.Ui.Controllers.LayoutEntityPlacing;
using Mafi.Unity.Ui.Library;
using Mafi.Unity.UiToolkit;
using Mafi.Unity.UiToolkit.Component;

namespace ElevationPP;

/// <summary>
/// Shows the pipe-tool's height tooltip (the small cursor-following "platform height" popup) when
/// placing height-adjustable buildings: pipe/belt connectors, balancers, sorters, lifts and the
/// mod's elevated stations.
///
/// The vanilla transport drag tool displays a <see cref="HeightPopup"/> with the current elevation,
/// but the generic building placer (<c>StaticEntityMassPlacer</c>) has no such display — when
/// placing a connector on an elevated pipe or raising a station there is no feedback about the
/// current height. This creates its own <see cref="HeightPopup"/> and drives it from a postfix on
/// the placer's per-frame <c>InputUpdate</c>: whenever the active preview's proto has a positive
/// placement height range (i.e. the building can be elevated at all), the popup shows the preview's
/// height above terrain, exactly like the pipe tool computes it. It hides on removal mode, on
/// non-elevatable protos, and when the tool deactivates.
/// </summary>
internal static class PlacementHeightPopupPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.heightpopup";

    private static bool s_patched;
    private static bool s_runtimeErrorLogged;
    private static FieldInfo s_previewsField;   // StaticEntityMassPlacer.m_entityPreviews
    private static FieldInfo s_removalField;    // StaticEntityMassPlacer.m_isInRemovalMode

    // Per game session (the UI dies with the session; re-resolved in TryInitialize).
    private static CameraController s_camera;
    private static UiRoot s_uiRoot;
    private static TerrainManager s_terrain;
    private static HeightPopup s_popup;

    public static void TryInitialize(DependencyResolver resolver)
    {
        s_popup = null;
        s_camera = null;
        s_uiRoot = null;
        s_terrain = null;

        try
        {
            s_camera = resolver.Resolve<CameraController>();
            s_uiRoot = resolver.Resolve<UiRoot>();
            s_terrain = resolver.Resolve<TerrainManager>();
        }
        catch (Exception)
        {
            Log.Info("Elevation++: UI not available (headless run?), placement height popup skipped.");
            return;
        }

        if (s_patched)
        {
            return;
        }
        s_patched = true;

        MethodBase inputUpdate = AccessTools.Method(typeof(StaticEntityMassPlacer), "InputUpdate");
        MethodBase deactivate = AccessTools.Method(typeof(StaticEntityMassPlacer), "Deactivate");
        s_previewsField = AccessTools.Field(typeof(StaticEntityMassPlacer), "m_entityPreviews");
        s_removalField = AccessTools.Field(typeof(StaticEntityMassPlacer), "m_isInRemovalMode");
        if (inputUpdate == null || deactivate == null || s_previewsField == null || s_removalField == null)
        {
            Log.Error("Elevation++: StaticEntityMassPlacer internals not resolved; "
                + "placement height popup skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(inputUpdate,
                postfix: new HarmonyMethod(typeof(PlacementHeightPopupPatch), nameof(InputUpdatePostfix)));
            harmony.Patch(deactivate,
                postfix: new HarmonyMethod(typeof(PlacementHeightPopupPatch), nameof(DeactivatePostfix)));
            Log.Info("Elevation++: placement height popup patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply placement height popup patch: {ex}");
        }
    }

    private static void InputUpdatePostfix(StaticEntityMassPlacer __instance)
    {
        try
        {
            if (s_uiRoot == null)
            {
                return;
            }
            if (!__instance.IsActive || (bool)s_removalField.GetValue(__instance))
            {
                hidePopup();
                return;
            }
            var previews = (Lyst<KeyValuePair<IStaticEntityPreview, EntityConfigData>>)
                s_previewsField.GetValue(__instance);
            if (previews == null || previews.Count == 0)
            {
                hidePopup();
                return;
            }
            IStaticEntityPreview preview = previews[0].Key;
            if (!(preview is IStaticEntityPreviewDirect direct)
                || !(preview.EntityProto is ILayoutEntityProto layoutProto)
                || layoutProto.Layout.PlacementHeightRange.Height.Value <= 0)
            {
                hidePopup();
                return;
            }

            Tile3i position = direct.Transform.Position;
            int relativeHeight = position.Z - s_terrain.GetHeight(position.Xy).Value.ToIntFloored();

            if (s_popup == null)
            {
                s_popup = new HeightPopup(s_camera, s_uiRoot);
            }
            s_popup.Label.Value(relativeHeight);
            s_popup.Show();
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Elevation++: placement height popup failed (logged once): {ex}");
            }
        }
    }

    private static void DeactivatePostfix()
    {
        try
        {
            hidePopup();
        }
        catch (Exception)
        {
        }
    }

    private static void hidePopup()
    {
        s_popup?.Hide();
    }
}
