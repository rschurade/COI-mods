using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.Ports.Io;
using Mafi.Core.Prototypes;

namespace ElevationPP;

/// <summary>
/// Adds a top and a bottom port to pipe connectors (mini-zippers), so pipes can connect to them
/// vertically, not just from the four horizontal sides.
///
/// The game's runtime port system is fully 3D: <see cref="IoPort"/> directions are
/// <see cref="Direction903d"/> (six directions incl. up/down), the ports manager matches ports by a
/// (tile, direction) lookup, pipes already form vertical end-to-end connections with each other, and
/// the drag tool's port snapping probes all six directions around a drag endpoint. The only reason
/// connectors have no vertical ports is the proto layer: building ports are declared in 2D ASCII
/// layouts whose <c>IoPortTemplate.RelativeDirection</c> is a <see cref="Direction90"/> — vertical is
/// unrepresentable there. The connector's product transfer is direction-agnostic (per-port buffers,
/// round-robin over connected ports), so fluid behaves identically through a vertical port.
///
/// This postfix on <c>MiniZipper.createPorts()</c> therefore appends two runtime ports (up 'U',
/// down 'W') at the connector's tile. It only targets connectors whose port shape belongs to a
/// transport that can run vertically (<c>TransportProto.ZStepLength == 0</c>, i.e. pipes); belt and
/// molten connectors are left untouched. Everything downstream — snapping, auto-connect, fluid flow —
/// rides on vanilla machinery.
///
/// Timing details:
/// - New connectors: <c>createPorts()</c> runs in the constructor before the per-port input buffer is
///   sized from <c>Ports.Length</c>, so the buffer automatically covers the extra ports.
/// - Tier upgrades: <c>OnUpgradeDone</c> re-runs <c>createPorts()</c> on the live entity; the input
///   buffer is resized here if it predates the vertical ports (connector from an older save).
/// - Loaded saves: ports are deserialized, <c>createPorts()</c> does not run, so connectors built
///   before this feature keep their four ports until rebuilt or upgraded.
/// </summary>
internal static class VerticalConnectorPortsPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.verticalports";

    /// <summary>Port names for the injected vertical ports (vanilla connector uses A-D).</summary>
    private const char PORT_NAME_UP = 'U';
    private const char PORT_NAME_DOWN = 'W';

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static HashSet<IoPortShapeProto> s_verticalShapes;

    /// <summary>Whether the vertical ports are actually being injected (patch applied OK).</summary>
    public static bool IsActive { get; private set; }

    /// <summary>Whether connectors of the given port shape receive vertical ports.</summary>
    internal static bool CoversShape(IoPortShapeProto shape)
    {
        return IsActive && s_verticalShapes != null && shape != null && s_verticalShapes.Contains(shape);
    }
    private static MethodInfo s_portsSetter;        // MiniZipper.Ports { private set; }
    private static FieldInfo s_inputBufferField;    // MiniZipper.m_inputBuffer

    public static void TryApply(ProtosDb protosDb)
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        // Vertical ports only make sense for shapes whose transports can run vertically.
        // ZStepLength semantics: 0 = vertical risers (pipes), N = ramps (belts),
        // RelTile1i.MaxValue = cannot change height (molten channels).
        s_verticalShapes = new HashSet<IoPortShapeProto>();
        foreach (TransportProto proto in protosDb.All<TransportProto>())
        {
            if (proto.ZStepLength.Value == 0)
            {
                s_verticalShapes.Add(proto.PortsShape);
            }
        }
        if (s_verticalShapes.Count == 0)
        {
            Log.Warning("Elevation++: no vertically-capable transport found; "
                + "vertical connector ports patch skipped.");
            return;
        }

        MethodBase target = AccessTools.Method(typeof(MiniZipper), "createPorts");
        s_portsSetter = AccessTools.PropertySetter(typeof(MiniZipper), "Ports");
        s_inputBufferField = AccessTools.Field(typeof(MiniZipper), "m_inputBuffer");
        if (target == null || s_portsSetter == null || s_inputBufferField == null)
        {
            Log.Error("Elevation++: MiniZipper.createPorts/Ports/m_inputBuffer not resolved; "
                + "vertical connector ports patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(target,
                postfix: new HarmonyMethod(typeof(VerticalConnectorPortsPatch), nameof(CreatePortsPostfix)));
            IsActive = true;
            Log.Info($"Elevation++: vertical connector ports patch applied "
                + $"({s_verticalShapes.Count} port shape(s)).");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply vertical connector ports patch: {ex}");
        }
    }

    private static void CreatePortsPostfix(MiniZipper __instance)
    {
        try
        {
            ImmutableArray<IoPort> ports = __instance.Ports;
            if (ports.IsEmpty || !s_verticalShapes.Contains(ports.First.ShapePrototype))
            {
                return;
            }
            // Defensive: never double-append should createPorts ever run on already-extended ports.
            ImmutableArray<IoPort>.Enumerator enumerator = ports.GetEnumerator();
            while (enumerator.MoveNext())
            {
                if (enumerator.Current.Direction.DirectionVector.Z != 0)
                {
                    return;
                }
            }

            IoPort first = ports.First;
            var lyst = new Lyst<IoPort>();
            ImmutableArray<IoPort>.Enumerator enumerator2 = ports.GetEnumerator();
            while (enumerator2.MoveNext())
            {
                lyst.Add(enumerator2.Current);
            }
            lyst.Add(new IoPort(__instance.Context.PortIdFactory.GetNextId(), __instance,
                new PortSpec(PORT_NAME_UP, IoPortType.Any, first.ShapePrototype,
                    first.Spec.CanOnlyConnectToTransports),
                first.Position, Direction903d.PlusZ, lyst.Count));
            lyst.Add(new IoPort(__instance.Context.PortIdFactory.GetNextId(), __instance,
                new PortSpec(PORT_NAME_DOWN, IoPortType.Any, first.ShapePrototype,
                    first.Spec.CanOnlyConnectToTransports),
                first.Position, Direction903d.MinusZ, lyst.Count));

            s_portsSetter.Invoke(__instance, new object[] { lyst.ToImmutableArray() });

            // On tier upgrade of a connector saved before this feature the input buffer still has
            // the old length; grow it so the new port indices are valid. In the constructor path the
            // buffer is null here and gets sized from Ports.Length right after this method returns.
            var buffer = (ProductQuantity[])s_inputBufferField.GetValue(__instance);
            if (buffer != null && buffer.Length < lyst.Count)
            {
                var newBuffer = new ProductQuantity[lyst.Count];
                for (int i = 0; i < newBuffer.Length; i++)
                {
                    newBuffer[i] = i < buffer.Length ? buffer[i] : ProductQuantity.None;
                }
                s_inputBufferField.SetValue(__instance, newBuffer);
            }
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Elevation++: vertical connector ports postfix failed: {ex}");
            }
        }
    }
}
