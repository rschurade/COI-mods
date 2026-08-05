using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.Ports.Io;
using Mafi.Unity.InputControl.Factory;
using Mafi.Unity.Ports.Io;

namespace ElevationPP;

/// <summary>
/// Shows the connector's vertical (top/bottom) ports on the placement cursor.
///
/// Built connectors display their vertical ports because those exist as runtime
/// <see cref="IoPort"/>s (injected by <see cref="VerticalConnectorPortsPatch"/>), but the placement
/// preview builds its port arrows from the proto's <see cref="IoPortTemplate"/>s, which can only
/// express horizontal directions — so the cursor showed just the four side ports and gave no
/// feedback for a vertical connection underneath/above.
///
/// This adds two extra <see cref="PortPreview"/>s (up + down) to every mini-zipper placement
/// preview whose shape has vertical ports, using the template-less Initialize overload that accepts
/// a raw <see cref="Direction903d"/>. They are kept OUT of the preview's own port list (whose
/// transform propagation only supports template-based ports) and are instead created/moved/removed
/// by postfixes on the preview's Initialize/applyTransform/clear. Each is also registered with
/// <see cref="ConnectorPortPreviewPatch"/> so a vertical connection to an existing riser shows the
/// same green "will connect" icon as the horizontal ones.
/// </summary>
internal static class VerticalPreviewPortsPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.previewports";

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static FieldInfo s_managerField;            // LayoutEntityPreview.m_manager
    private static FieldInfo s_portPreviewManagerField; // LayoutEntityPreviewManager.PreviewManager (internal)
    private static FieldInfo s_disablePreviewsField;    // LayoutEntityPreview.m_disablePortPreviews
    private static FieldInfo s_disableZipperField;      // LayoutEntityPreview.m_disableMiniZipperPlacement

    // Vertical previews per active LayoutEntityPreview; mutated on the main thread only.
    private static readonly Dictionary<LayoutEntityPreview, PortPreview[]> s_extraPreviews
        = new Dictionary<LayoutEntityPreview, PortPreview[]>();

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        if (!VerticalConnectorPortsPatch.IsActive)
        {
            Log.Info("Elevation++: vertical connector ports are not active; "
                + "preview ports patch skipped.");
            return;
        }

        MethodBase initialize = AccessTools.Method(typeof(LayoutEntityPreview), "Initialize");
        MethodBase applyTransform = AccessTools.Method(typeof(LayoutEntityPreview), "applyTransform");
        MethodBase clear = AccessTools.Method(typeof(LayoutEntityPreview), "clear");
        s_managerField = AccessTools.Field(typeof(LayoutEntityPreview), "m_manager");
        s_portPreviewManagerField = AccessTools.Field(typeof(LayoutEntityPreviewManager), "PreviewManager");
        s_disablePreviewsField = AccessTools.Field(typeof(LayoutEntityPreview), "m_disablePortPreviews");
        s_disableZipperField = AccessTools.Field(typeof(LayoutEntityPreview), "m_disableMiniZipperPlacement");
        if (initialize == null || applyTransform == null || clear == null
            || s_managerField == null || s_portPreviewManagerField == null
            || s_disablePreviewsField == null || s_disableZipperField == null)
        {
            Log.Error("Elevation++: LayoutEntityPreview internals not resolved; "
                + "vertical preview ports patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(initialize,
                postfix: new HarmonyMethod(typeof(VerticalPreviewPortsPatch), nameof(InitializePostfix)));
            harmony.Patch(applyTransform,
                postfix: new HarmonyMethod(typeof(VerticalPreviewPortsPatch), nameof(ApplyTransformPostfix)));
            harmony.Patch(clear,
                postfix: new HarmonyMethod(typeof(VerticalPreviewPortsPatch), nameof(ClearPostfix)));
            Log.Info("Elevation++: vertical preview ports patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply vertical preview ports patch: {ex}");
        }

        // Vanilla places port status icons per direction; the down direction is authored 2 tiles
        // straight down (vanilla has no down-facing ports on buildings), which parks the icon at
        // the bottom of the riser the connector sits on. Pull it in to just below the connector.
        // The array is public static and shared, so a plain value tweak suffices (1 tile = 2 Unity
        // units; Unity Y is up).
        try
        {
            IoPortsRenderer.PORT_ICON_OFFSETS[Direction903d.MINUS_Z_INDEX]
                = new UnityEngine.Vector3(0f, -1f, 0f);
        }
        catch (Exception ex)
        {
            Log.Warning($"Elevation++: failed to adjust down-port icon offset: {ex.Message}");
        }
    }

    private static void InitializePostfix(LayoutEntityPreview __instance)
    {
        try
        {
            if (s_extraPreviews.ContainsKey(__instance))
            {
                return;
            }
            ILayoutEntityProto proto = __instance.LayoutEntityProto;
            if (!(proto is MiniZipperProto || proto is Connectors.BalancingConnectorProto)
                || proto.Ports.IsEmpty
                || (bool)s_disablePreviewsField.GetValue(__instance))
            {
                return;
            }
            IoPortTemplate template = proto.Ports.First;
            if (!VerticalConnectorPortsPatch.CoversShape(template.Shape))
            {
                return;
            }

            var manager = (LayoutEntityPreviewManager)s_managerField.GetValue(__instance);
            var portPreviewManager = (PortPreviewManager)s_portPreviewManagerField.GetValue(manager);
            bool zipperDisabled = (bool)s_disableZipperField.GetValue(__instance);
            Option<IoPortShapeProto> viaZipper = zipperDisabled
                ? Option<IoPortShapeProto>.None
                : (Option<IoPortShapeProto>)template.Shape;
            Tile3i position = proto.Layout.Transform(template.RelativePosition, __instance.Transform);

            var previews = new PortPreview[2];
            previews[0] = portPreviewManager.GetPortPreviewPooled().Initialize(
                position, Direction903d.PlusZ, template.Shape, IoPortType.Any, viaZipper,
                template.Spec.CanOnlyConnectToTransports, ownerIsTransport: false, isEndPort: false);
            previews[1] = portPreviewManager.GetPortPreviewPooled().Initialize(
                position, Direction903d.MinusZ, template.Shape, IoPortType.Any, viaZipper,
                template.Spec.CanOnlyConnectToTransports, ownerIsTransport: false, isEndPort: false);
            s_extraPreviews.Add(__instance, previews);
            ConnectorPortPreviewPatch.ExtraConnectorPreviews.TryAdd(previews[0], true);
            ConnectorPortPreviewPatch.ExtraConnectorPreviews.TryAdd(previews[1], true);
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static void ApplyTransformPostfix(LayoutEntityPreview __instance, TileTransform transform)
    {
        try
        {
            if (!s_extraPreviews.TryGetValue(__instance, out PortPreview[] previews))
            {
                return;
            }
            ILayoutEntityProto proto = __instance.LayoutEntityProto;
            if (proto == null || proto.Ports.IsEmpty)
            {
                return;
            }
            Tile3i position = proto.Layout.Transform(proto.Ports.First.RelativePosition, transform);
            previews[0].SetTransform(position, Direction903d.PlusZ);
            previews[1].SetTransform(position, Direction903d.MinusZ);
        }
        catch (Exception ex)
        {
            logOnce(ex);
        }
    }

    private static void ClearPostfix(LayoutEntityPreview __instance)
    {
        try
        {
            if (!s_extraPreviews.TryGetValue(__instance, out PortPreview[] previews))
            {
                return;
            }
            s_extraPreviews.Remove(__instance);
            foreach (PortPreview preview in previews)
            {
                ConnectorPortPreviewPatch.ExtraConnectorPreviews.TryRemove(preview, out _);
                preview.DestroyAndReturnToPool();
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
            Log.Error($"Elevation++: vertical preview ports patch failed (logged once): {ex}");
        }
    }
}
