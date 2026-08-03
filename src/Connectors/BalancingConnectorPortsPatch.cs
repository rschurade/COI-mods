using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.Ports;
using Mafi.Core.Ports.Io;

namespace ElevationPP.Connectors;

/// <summary>
/// Adds the top and bottom pipe ports to the balancing pipe connector, mirroring what
/// <see cref="VerticalConnectorPortsPatch"/> does for mini-zipper connectors.
///
/// The balancing connector's entity is the vanilla <c>Zipper</c>, whose ports are created by the
/// private <c>LayoutEntity.createPorts()</c> from the proto's 2D layout templates — which cannot
/// express vertical directions. This postfix appends the two runtime ports (up 'U', down 'W')
/// whenever the entity being built is a Zipper with a <see cref="BalancingConnectorProto"/>.
///
/// Timing: createPorts() runs in the LayoutEntity base constructor, BEFORE the Zipper constructor
/// sizes its per-port state (priority flags, input buffer) from <c>Ports.Length</c> — so all six
/// ports are covered automatically, and the balancer's port-generic logic (and its management
/// window) picks them up with zero further work. On save load ports are deserialized instead, so
/// entities keep their six ports across saves.
/// </summary>
internal static class BalancingConnectorPortsPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp.balancerports";

    /// <summary>Same names as the mini-zipper's injected vertical ports.</summary>
    private const char PORT_NAME_UP = 'U';
    private const char PORT_NAME_DOWN = 'W';

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;
    private static MethodInfo s_portsSetter; // LayoutEntity.Ports { protected set; }

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
                + "balancing connector ports patch skipped.");
            return;
        }

        MethodBase target = AccessTools.Method(typeof(LayoutEntity), "createPorts");
        s_portsSetter = AccessTools.PropertySetter(typeof(LayoutEntity), "Ports");
        if (target == null || s_portsSetter == null)
        {
            Log.Error("Elevation++: LayoutEntity.createPorts/Ports not resolved; "
                + "balancing connector ports patch skipped.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(target, postfix: new HarmonyMethod(
                typeof(BalancingConnectorPortsPatch), nameof(CreatePortsPostfix)));
            Log.Info("Elevation++: balancing connector ports patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: failed to apply balancing connector ports patch: {ex}");
        }
    }

    private static void CreatePortsPostfix(LayoutEntity __instance)
    {
        try
        {
            // createPorts runs for every layout entity; bail out fast for everything else.
            if (!(__instance is Zipper zipperEntity)
                || !(__instance.Prototype is BalancingConnectorProto))
            {
                return;
            }
            var withPorts = (IEntityWithPorts)zipperEntity;
            ImmutableArray<IoPort> ports = __instance.Ports;
            if (ports.IsEmpty || !VerticalConnectorPortsPatch.CoversShape(ports.First.ShapePrototype))
            {
                return;
            }
            // Defensive: never double-append.
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
            lyst.Add(new IoPort(__instance.Context.PortIdFactory.GetNextId(), withPorts,
                new PortSpec(PORT_NAME_UP, IoPortType.Any, first.ShapePrototype,
                    first.Spec.CanOnlyConnectToTransports),
                first.Position, Direction903d.PlusZ, lyst.Count));
            lyst.Add(new IoPort(__instance.Context.PortIdFactory.GetNextId(), withPorts,
                new PortSpec(PORT_NAME_DOWN, IoPortType.Any, first.ShapePrototype,
                    first.Spec.CanOnlyConnectToTransports),
                first.Position, Direction903d.MinusZ, lyst.Count));

            s_portsSetter.Invoke(__instance, new object[] { lyst.ToImmutableArray() });
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Elevation++: balancing connector ports postfix failed: {ex}");
            }
        }
    }
}
