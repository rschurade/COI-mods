using Mafi;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.Prototypes;

namespace ElevationPP.Connectors;

/// <summary>
/// A 1x1 pipe connector with the full balancing feature set of the vanilla pipe balancer.
///
/// This is a plain <see cref="ZipperProto"/> (the balancer proto), so the entity it creates IS the
/// vanilla <c>Zipper</c>: per-port priorities, the "enforce strictly even inputs/outputs" toggles,
/// the prioritization management window (<c>ZipperInspector</c> sizes its port buttons from the
/// entity's live ports and positions them by projecting each port's connection tile, so the six
/// ports — including the vertical pair — get their buttons automatically), the input/output
/// buffering and the priority commands all come from vanilla code untouched.
///
/// What makes it a connector rather than a 2x2 balancer:
/// - it reuses the pipe connector's model and 1x1 layout (4 horizontal pipe ports),
/// - <see cref="BalancingConnectorPortsPatch"/> injects the same top/bottom runtime ports the mod
///   gives mini-zipper connectors, so it balances vertical connections too,
/// - <see cref="BalancingConnectorValidator"/> lets it be placed directly onto an existing pipe,
///   cutting itself in exactly like a connector (including on riser elbows and mid-riser).
///
/// The proto class exists (rather than using ZipperProto directly) so the mod's patches can
/// recognize the building by type. Elevation is always on — like the vanilla balancer it can stand
/// on transport pillars (canBeElevated), and pillars can pass through it (inherited).
/// </summary>
public class BalancingConnectorProto : ZipperProto
{
    public BalancingConnectorProto(ID id, Proto.Str strings, EntityLayout layout, EntityCosts costs,
        Electricity electricityConsumed, Gfx graphics)
        : base(id, strings, layout, costs, electricityConsumed, canBeElevated: true, graphics)
    {
    }
}
