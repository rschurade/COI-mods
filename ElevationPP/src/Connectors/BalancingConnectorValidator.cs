using System;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Entities.Validators;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.Prototypes;

namespace ElevationPP.Connectors;

/// <summary>
/// Placement support for the balancing pipe connector: lets it be placed directly onto an existing
/// pipe, cutting itself in — the same quality-of-life the mini-zipper connector has.
///
/// This is a faithful sibling of the vanilla <c>MiniZipperValidator</c>, registered in the DI
/// container the same way (as all interfaces):
/// - As the request FACTORY for <see cref="BalancingConnectorProto"/> it beats the default layout
///   factory (factory resolution walks the proto type hierarchy) and creates the add request with
///   the ignore-transports-and-pillars collision predicate, so hovering a pipe doesn't instantly
///   fail with a collision before the cut is even considered.
/// - As an addition VALIDATOR it checks the hovered tile via the vanilla
///   <c>CanBuildMiniZipperAt</c>: a free tile validates as a normal placement, a tile occupied by a
///   matching pipe validates as a cut (including this mod's riser-elbow and mid-riser extensions,
///   which live inside <c>CanPlaceMiniZipperAt</c>), and anything else errors. The vanilla check
///   wants a mini-zipper proto to compare against the pipe's shape, so the pipe connector proto of
///   the same port shape is passed as a stand-in; only the cut geometry is taken from the result.
/// - As a PRE-ADD validator it performs the pending cut right before the entity is added; the
///   severed pipe ends then auto-connect to the new entity's ports like they do for a connector.
/// </summary>
public class BalancingConnectorValidator : IEntityAdditionValidator<LayoutEntityAddRequest>,
    IEntityPreAddValidator,
    IFactory<BalancingConnectorProto, EntityAddRequestFactoryData, LayoutEntityAddRequest>
{
    private readonly TransportsManager m_transportsManager;
    private readonly ProtosDb m_protosDb;

    private CanPlaceMiniZipperAtResult? m_lastCanAddResult;

    public EntityValidatorPriority Priority => EntityValidatorPriority.Default;

    public BalancingConnectorValidator(TransportsManager transportsManager, ProtosDb protosDb)
    {
        m_transportsManager = transportsManager;
        m_protosDb = protosDb;
    }

    public EntityValidationResult CanAdd(LayoutEntityAddRequest addRequest)
    {
        m_lastCanAddResult = null;
        if (!(addRequest.Proto is BalancingConnectorProto proto))
        {
            return EntityValidationResult.Success;
        }
        Option<MiniZipperProto> standIn = m_protosDb.Get<MiniZipperProto>(
            IdsCore.Transports.GetMiniZipperIdFor(proto.PortsShape.Id));
        if (standIn.IsNone)
        {
            // No connector exists for this shape; plain placement validation only.
            return EntityValidationResult.Success;
        }
        if (!m_transportsManager.CanBuildMiniZipperAt(standIn.Value, addRequest.Transform.Position,
            out CanPlaceMiniZipperAtResult? result, out var error))
        {
            return EntityValidationResult.CreateError(error);
        }
        // result is null for a free tile (normal placement) and carries the cut for a pipe tile.
        m_lastCanAddResult = result;
        return EntityValidationResult.Success;
    }

    public void PrepareForAdd()
    {
        if (m_lastCanAddResult.HasValue)
        {
            m_transportsManager.CutOutTransportForMiniZipper(m_lastCanAddResult.Value);
            m_lastCanAddResult = null;
        }
    }

    public LayoutEntityAddRequest Create(BalancingConnectorProto proto,
        EntityAddRequestFactoryData factoryData)
    {
        EntityAddRequestData data = factoryData.Data;
        Predicate<EntityId> ignoreForCollisions = data.IgnoreForCollisions.HasValue
            ? (EntityId x) => data.IgnoreForCollisions.ValueOrNull(x)
                || m_transportsManager.IgnoreTransportsAndPillars(x)
            : (Predicate<EntityId>)((EntityId x) => m_transportsManager.IgnoreTransportsAndPillars(x));
        return LayoutEntityAddRequest.GetPooledInstanceToCreateEntity(proto,
            new EntityAddRequestData(data.Transform, enableMiniZipperPlacement: false,
                ignoreForCollisions, data.RecordTileErrors),
            factoryData.ReasonToAdd, data.AllowValidationSuppression);
    }
}
