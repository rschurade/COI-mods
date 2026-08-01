using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.Ports.Io;
using Mafi.Core.Terrain;
using Mafi.Unity.InputControl.Factory;

namespace ElevationPP;

/// <summary>
/// Shows the green "will connect" icon on a connector's port previews when the connector is being
/// placed onto an existing pipe (cut-in), instead of the red "blocked / cannot connect" icon.
///
/// The vanilla <see cref="PortPreview"/> status logic evaluates each port in isolation against the
/// CURRENT world: a port whose front tile is occupied by the run of the very pipe the connector is
/// about to be cut into (or by the already-connected joint of two pipes) reads as blocked or
/// not-connectable and renders red — even though after the cut those ports are exactly the ones
/// that reconnect the severed pipe. This postfix on <c>PortPreview.SimUpdate</c> flips a negative
/// status to positive when all of the following hold: the preview belongs to a mini-zipper proto,
/// the connector's own tile sits on a matching-shape transport that can actually be cut there
/// (<c>CanPlaceMiniZipperAt</c>, including this mod's elbow/riser extension), and the port's front
/// tile is occupied by a matching-shape transport. Ports facing empty air keep their neutral yellow
/// arrow, genuinely impossible placements keep their red.
/// </summary>
internal static class ConnectorPortPreviewPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.portpreview";

    /// <summary>
    /// Template-less port previews created by <see cref="VerticalPreviewPortsPatch"/> for the
    /// connector's vertical ports. They carry no entity proto, so they are recognized here by
    /// identity to receive the same "will connect" treatment. Registered/unregistered on the main
    /// thread, read on the sim thread — hence concurrent.
    /// </summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<PortPreview, bool>
        ExtraConnectorPreviews = new System.Collections.Concurrent.ConcurrentDictionary<PortPreview, bool>();

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static FieldInfo s_positionField;      // PortPreview.m_position
    private static FieldInfo s_directionField;     // PortPreview.m_direction
    private static FieldInfo s_entityProtoField;   // PortPreview.m_entityProto
    private static FieldInfo s_statusField;        // PortPreview.m_portConnStatusOnSim
    private static FieldInfo s_occupancyField;     // PortPreview.m_occupancyManager
    private static FieldInfo s_helperField;        // PortPreview.m_transportsConstructionHelper
    private static FieldInfo s_canConnectField;    // PortConnStatus.CanConnect
    private static ConstructorInfo s_statusCtor;   // PortConnStatus(Option<IoPort>, bool, bool)

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        MethodBase target = AccessTools.Method(typeof(PortPreview), "SimUpdate");
        s_positionField = AccessTools.Field(typeof(PortPreview), "m_position");
        s_directionField = AccessTools.Field(typeof(PortPreview), "m_direction");
        s_entityProtoField = AccessTools.Field(typeof(PortPreview), "m_entityProto");
        s_statusField = AccessTools.Field(typeof(PortPreview), "m_portConnStatusOnSim");
        s_occupancyField = AccessTools.Field(typeof(PortPreview), "m_occupancyManager");
        s_helperField = AccessTools.Field(typeof(PortPreview), "m_transportsConstructionHelper");
        Type statusType = AccessTools.Inner(typeof(PortPreview), "PortConnStatus");
        if (statusType != null)
        {
            s_canConnectField = AccessTools.Field(statusType, "CanConnect");
            s_statusCtor = AccessTools.Constructor(statusType,
                new[] { typeof(Option<IoPort>), typeof(bool), typeof(bool) });
        }
        if (target == null || s_positionField == null || s_directionField == null
            || s_entityProtoField == null || s_statusField == null || s_occupancyField == null
            || s_helperField == null || s_canConnectField == null || s_statusCtor == null)
        {
            Log.Error("Elevation++: PortPreview internals not resolved; "
                + "connector port preview patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(ConnectorPortPreviewPatch), nameof(SimUpdatePostfix)));
            Log.Info("Elevation++: connector port preview patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply connector port preview patch: {ex}");
        }
    }

    private static void SimUpdatePostfix(PortPreview __instance)
    {
        try
        {
            // Only touch a freshly computed, negative status.
            object status = s_statusField.GetValue(__instance);
            if (status == null || (bool)s_canConnectField.GetValue(status))
            {
                return;
            }
            var protoOption = (Option<ILayoutEntityProto>)s_entityProtoField.GetValue(__instance);
            if (!(protoOption.ValueOrNull is MiniZipperProto)
                && !ExtraConnectorPreviews.ContainsKey(__instance))
            {
                return;
            }
            Option<IoPortShapeProto> shapeOption = __instance.ShapeProto;
            if (shapeOption.IsNone)
            {
                return;
            }

            var position = (Tile3i)s_positionField.GetValue(__instance);
            var direction = (Direction903d)s_directionField.GetValue(__instance);
            var occupancy = (TerrainOccupancyManager)s_occupancyField.GetValue(__instance);
            var helper = (TransportsConstructionHelper)s_helperField.GetValue(__instance);

            // The connector must be hovering over a matching pipe that can actually be cut there...
            if (!occupancy.TryGetOccupyingEntityAt<Transport>(position, out Transport pipeAtPosition)
                || pipeAtPosition.Prototype.PortsShape != shapeOption.Value)
            {
                return;
            }
            // ...and this port must face a continuation of the pipe run (same entity or the
            // port-connected neighbor at a joint) — those are the connections the cut re-creates.
            Tile3i front = position + direction.ToTileDirection();
            if (!occupancy.TryGetOccupyingEntityAt<Transport>(front, out Transport pipeInFront)
                || pipeInFront.Prototype.PortsShape != shapeOption.Value)
            {
                return;
            }
            if (!helper.CanPlaceMiniZipperAt(pipeAtPosition, position, out var _, out var _))
            {
                return;
            }

            object positiveStatus = s_statusCtor.Invoke(
                new object[] { Option<IoPort>.None, true, false });
            s_statusField.SetValue(__instance, positiveStatus);
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Elevation++: connector port preview postfix failed (logged once): {ex}");
            }
        }
    }
}
