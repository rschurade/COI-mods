using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.Maintenance;
using Mafi.Core.Mods;
using Mafi.Core.Ports.Io;
using Mafi.Core.Prototypes;
using Mafi.Core.Research;
using Mafi.Core.UnlockingTree;
using ElevationPP.Stations;

namespace ElevationPP.Connectors;

/// <summary>
/// Registers the "Balancing pipe connector": the pipe connector's 1x1 model and layout married to
/// the vanilla pipe balancer's entity class (see <see cref="BalancingConnectorProto"/>). It costs
/// and consumes half of the vanilla pipe balancer, sits right next to it in the toolbar, and
/// unlocks with the same research.
/// </summary>
internal class BalancingConnectorData : IModData
{
    public void RegisterData(ProtoRegistrator registrator)
    {
        ProtosDb db = registrator.PrototypesDb;

        // The pipe port shape: the shape of any transport that can run vertically
        // (ZStepLength == 0 — pipes; belts ramp, molten channels cannot change height).
        IoPortShapeProto pipeShape = null;
        foreach (TransportProto transport in db.All<TransportProto>())
        {
            if (transport.ZStepLength.Value == 0)
            {
                pipeShape = transport.PortsShape;
                break;
            }
        }
        if (pipeShape == null)
        {
            Log.Warning("Elevation++: no pipe transport found; balancing connector not registered.");
            return;
        }

        // Donors: the pipe connector (model + 1x1 layout) and the pipe balancer (costs, power,
        // toolbar spot, research).
        MiniZipperProto connector = db.Get<MiniZipperProto>(
            IdsCore.Transports.GetMiniZipperIdFor(pipeShape.Id)).ValueOrNull;
        ZipperProto balancer = null;
        foreach (ZipperProto zipper in db.All<ZipperProto>())
        {
            if (!(zipper is BalancingConnectorProto) && zipper.PortsShape == pipeShape)
            {
                balancer = zipper;
                break;
            }
        }
        if (connector == null || balancer == null)
        {
            Log.Warning("Elevation++: pipe connector or pipe balancer proto not found; "
                + "balancing connector not registered.");
            return;
        }

        var id = new StaticEntityProto.ID("ElevationPP_BalancingPipeConnector");
        Proto.Str strings = Proto.CreateStr(id, "Balancing pipe connector",
            "A pipe connector with the full prioritization controls of a pipe balancer: per-port "
            + "priorities and strictly even input/output ratios, on all six connections including "
            + "the vertical ones. Can be placed directly onto an existing pipe, cutting itself in.",
            null);

        // Toolbar: same category as the pipe balancer, ordered right after it.
        ImmutableArray<ToolbarEntryData> categories;
        if (balancer.Graphics.Categories.IsNotEmpty)
        {
            ToolbarEntryData entry = balancer.Graphics.Categories[0];
            categories = ImmutableArray.Create(
                new ToolbarEntryData(entry.CategoryProto, false, (entry.Order ?? 100) + 1));
        }
        else
        {
            categories = connector.Graphics.Categories;
        }
        var gfx = (LayoutEntityProto.Gfx)ElevatedStationData.cloneGfxWithCategory(
            connector.Graphics, ElevatedStationData.vanillaIconPath(connector), categories);

        var proto = new BalancingConnectorProto(id, strings, connector.Layout,
            halfCosts(balancer.Costs), balancer.ElectricityConsumed / 2, gfx);
        ElevatedStationData.addGated(db, proto, findUnlockingNode(db, balancer));
        Log.Info($"Elevation++: registered '{id}' (half of '{balancer.Id}' costs, "
            + $"{proto.ElectricityConsumed} power).");
    }

    /// <summary>Half of the given costs: construction products, workers and monthly maintenance
    /// are halved (rounded up so nothing drops to zero); the default priority is kept.</summary>
    private static EntityCosts halfCosts(EntityCosts costs)
    {
        MaintenanceCosts maintenance = costs.Maintenance;
        MaintenanceCosts halvedMaintenance = maintenance.Product == null
            ? maintenance
            : new MaintenanceCosts(maintenance.Product, maintenance.MaxMaintenancePerMonth / 2,
                maintenance.ExtraBufferDuration, maintenance.InitialMaintenanceBoost);
        return new EntityCosts(costs.BaseConstructionCost.CeilDiv(2), costs.DefaultPriority,
            (costs.Workers + 1) / 2, halvedMaintenance);
    }

    private static ResearchNodeProto findUnlockingNode(ProtosDb db, IProto target)
    {
        foreach (ResearchNodeProto node in db.All<ResearchNodeProto>())
        {
            foreach (IProto unlocked in ProtoUnlock.GetUnlockedProtos(node.Units.AsEnumerable()))
            {
                if (unlocked == target)
                {
                    return node;
                }
            }
        }
        return null;
    }
}
