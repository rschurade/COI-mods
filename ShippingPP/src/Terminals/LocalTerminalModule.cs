using System;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Buildings.Storages;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Maintenance;
using Mafi.Core.Notifications;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;
using Mafi.Core.Vehicles;
using Mafi.Core.World;
using Mafi.Core.World.Contracts;
using Mafi.Serialization;

namespace ShippingPP.Terminals;

/// <summary>
/// A cargo module attached to a local terminal: a vanilla <see cref="CargoDepotModule"/> that can
/// additionally take part in the game's directed truck routes (a storage's "assign export/import
/// building" picking), which vanilla modules cannot — the route API and the truck-job matching
/// are typed on <see cref="IEntityAssignedAsInput"/>/<see cref="IEntityAssignedAsOutput"/>, which
/// only storages and mine/forestry towers implement. Instantiated instead of the vanilla class
/// for modules placed on a <see cref="LocalTerminal"/> (see <c>ModuleFactoryPatch</c>); modules
/// from saves older than this feature stay vanilla until rebuilt.
///
/// Route semantics follow the module's shipping direction:
///   EXPORT module (offers to ships)   — valid RECEIVER of storage routes (storage → module,
///                                       trucks deliver what the ships will take away).
///   IMPORT module (requests from ships) — valid SOURCE of storage routes (module → storage,
///                                       trucks distribute what the ships bring in).
/// When the player flips the direction (or disables the module's truck logistics), existing
/// routes become invalid: they go dormant and the module raises the vanilla continuous
/// "Invalid import/export route" notification until the route is removed or the direction
/// restored — the same UX a storage shows for its own dead routes.
/// </summary>
public class LocalTerminalModule : CargoDepotModule, IEntityAssignedAsInput, IEntityAssignedAsOutput
{
    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((LocalTerminalModule)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((LocalTerminalModule)obj).DeserializeData(reader);

    private Set<IEntityAssignedAsInput> m_assignedInputEntities;
    private Set<IEntityAssignedAsOutput> m_assignedOutputEntities;
    private EntityNotificator m_invalidImportRouteNotif;
    private EntityNotificator m_invalidExportRouteNotif;

    public bool AllowNonAssignedOutput { get; private set; }

    public Mafi.Collections.IReadOnlySet<IEntityAssignedAsInput> AssignedInputs
        => m_assignedInputEntities;

    public Mafi.Collections.IReadOnlySet<IEntityAssignedAsOutput> AssignedOutputs
        => m_assignedOutputEntities;

    public LocalTerminalModule(EntityId id, CargoDepotModuleProto cargoDepotProto,
        TileTransform transform, EntityContext context, ISimLoopEvents simLoopEvents,
        ContractsManager contractsManager, CargoDepotManager binder,
        IVehicleBuffersRegistry vehicleBuffersRegistry,
        IEntityMaintenanceProvidersFactory maintenanceProvidersFactory,
        WorldMapManager worldMapManager)
        : base(id, cargoDepotProto, transform, context, simLoopEvents, contractsManager, binder,
            vehicleBuffersRegistry, maintenanceProvidersFactory, worldMapManager)
    {
        m_assignedInputEntities = new Set<IEntityAssignedAsInput>();
        m_assignedOutputEntities = new Set<IEntityAssignedAsOutput>();
        AllowNonAssignedOutput = true;
        m_invalidImportRouteNotif = context.NotificationsManager.CreateNotificatorFor(
            IdsCore.Notifications.InvalidImportRoute);
        m_invalidExportRouteNotif = context.NotificationsManager.CreateNotificatorFor(
            IdsCore.Notifications.InvalidExportRoute);
    }

    /// <summary>Whether this module currently offers its product to ships (export mode).</summary>
    private bool isExportMode()
    {
        ShippingManager manager = ShippingManager.Current;
        return manager != null && manager.IsExportModule(this);
    }

    /// <summary>
    /// Product compatibility with a storage: the module must have a product assigned (a route
    /// to a product-less module could never move anything), and the storage must either store
    /// that same product already or be able to.
    /// </summary>
    private bool checkProductCompatibility(Storage storage, out string reason)
    {
        ProductProto product = StoredProduct.ValueOrNull;
        if (product == null)
        {
            reason = "module has no product assigned";
            return false;
        }
        ProductProto storageProduct = storage.StoredProduct.ValueOrNull;
        if (storageProduct != null && storageProduct != product)
        {
            reason = $"product mismatch (module {product.Id}, storage {storageProduct.Id})";
            return false;
        }
        if (storageProduct == null && !storage.Prototype.StorableProducts.Contains(product))
        {
            reason = $"storage cannot store {product.Id}";
            return false;
        }
        reason = "ok";
        return true;
    }

    // Vanilla role naming (derived from the truck-job matching and the storage window wiring):
    // IEntityAssignedAsOutput is the SOURCE side of a route — its AssignedInputs are the
    // receivers it exports to; IEntityAssignedAsInput is the RECEIVER side — its
    // AssignedOutputs are the suppliers it imports from.

    /// <summary>Module as SOURCE: whether the given entity may be assigned to RECEIVE from this
    /// module (module → storage; requires import mode, where ships deliver and trucks
    /// distribute the cargo to storages).</summary>
    public bool CanBeAssignedWithInput(IEntityAssignedAsInput entity)
    {
        bool result = canBeAssignedWithInput(entity, out string reason);
        logConsent("module→storage", entity, result, reason);
        return result;
    }

    private bool canBeAssignedWithInput(IEntityAssignedAsInput entity, out string reason)
    {
        // "Already assigned" only when BOTH sides hold the route — a half-recorded route
        // (possible in saves made before the one-sided-assignment fix) stays assignable, so a
        // second assign click repairs it instead of being refused.
        if (ReferenceEquals(entity, this)
            || (m_assignedInputEntities.Contains(entity) && entity.AssignedOutputs.Contains(this)))
        {
            reason = "already assigned";
            return false;
        }
        return AcceptsReceiverRoute(entity, out reason);
    }

    /// <summary>
    /// STATELESS half of the module→storage consent (module as source; requires import mode):
    /// direction and product compatibility only, no already-assigned guard. The storage-side
    /// patch must use this instead of the full check: the assignment command adds the two route
    /// sides sequentially, so by the time the second side validates, the first side's set
    /// already contains the partner — a full check would refuse with "already assigned" and
    /// leave the route recorded on one side only.
    /// </summary>
    internal bool AcceptsReceiverRoute(IEntity partner, out string reason)
    {
        if (!(partner is Storage storage))
        {
            return failNotStorage(out reason);
        }
        if (isExportMode())
        {
            reason = "module is not in import mode";
            return false;
        }
        return checkProductCompatibility(storage, out reason);
    }

    /// <summary>Module as RECEIVER: whether the given entity may be assigned to FEED this
    /// module (storage → module; requires export mode, where trucks deliver and ships take
    /// the cargo away).</summary>
    public bool CanBeAssignedWithOutput(IEntityAssignedAsOutput entity)
    {
        bool result = canBeAssignedWithOutput(entity, out string reason);
        logConsent("storage→module", entity, result, reason);
        return result;
    }

    private bool canBeAssignedWithOutput(IEntityAssignedAsOutput entity, out string reason)
    {
        // Both-sides guard — see canBeAssignedWithInput.
        if (ReferenceEquals(entity, this)
            || (m_assignedOutputEntities.Contains(entity) && entity.AssignedInputs.Contains(this)))
        {
            reason = "already assigned";
            return false;
        }
        return AcceptsSupplierRoute(entity, out reason);
    }

    /// <summary>STATELESS half of the storage→module consent (module as receiver; requires
    /// export mode) — see <see cref="AcceptsReceiverRoute"/> for why the storage-side patch
    /// needs the stateless variant.</summary>
    internal bool AcceptsSupplierRoute(IEntity partner, out string reason)
    {
        if (!(partner is Storage storage))
        {
            return failNotStorage(out reason);
        }
        if (!isExportMode())
        {
            reason = "module is not in export mode";
            return false;
        }
        return checkProductCompatibility(storage, out reason);
    }

    private static bool failNotStorage(out string reason)
    {
        reason = "partner is not a storage";
        return false;
    }

    /// <summary>Support diagnostic: logs why a route partner was REFUSED (accepted routes are
    /// visible in-game). Consent runs every hover frame, so duplicates are suppressed.</summary>
    private static string s_lastConsentLog;

    private void logConsent(string direction, IEntity partner, bool result, string reason)
    {
        if (result)
        {
            return;
        }
        string msg = $"Shipping++[route]: {direction} refused for module {Id} / partner "
            + $"{partner?.Id.ToString() ?? "null"}: {reason}";
        if (msg != s_lastConsentLog)
        {
            s_lastConsentLog = msg;
            Log.Info(msg);
        }
    }

    void IEntityAssignedAsOutput.AssignStaticInputEntity(IEntityAssignedAsInput entity)
    {
        if (CanBeAssignedWithInput(entity))
        {
            m_assignedInputEntities.Add(entity);
            RefreshRouteValidity();
        }
    }

    void IEntityAssignedAsOutput.UnassignStaticInputEntity(IEntityAssignedAsInput entity)
    {
        m_assignedInputEntities.Remove(entity);
        RefreshRouteValidity();
    }

    void IEntityAssignedAsInput.AssignStaticOutputEntity(IEntityAssignedAsOutput entity)
    {
        if (CanBeAssignedWithOutput(entity))
        {
            m_assignedOutputEntities.Add(entity);
            RefreshRouteValidity();
        }
    }

    void IEntityAssignedAsInput.UnassignStaticOutputEntity(IEntityAssignedAsOutput entity)
    {
        m_assignedOutputEntities.Remove(entity);
        RefreshRouteValidity();
    }

    public void SetAllowNonAssignedOutput(bool value)
    {
        if (AllowNonAssignedOutput != value)
        {
            AllowNonAssignedOutput = value;
            if (!value)
            {
                Context.UnreachablesManager.TryClearUnreachableVehiclesFor(this);
            }
        }
    }

    /// <summary>
    /// Raises/clears the vanilla "invalid route" notifications: a route whose truck direction no
    /// longer matches the module's shipping direction (or whose truck logistics the player
    /// disabled) is dead until fixed. Called on assignment changes and by the shipping manager
    /// whenever the module's direction is (re)applied, so a direction flip in the terminal
    /// window flags the stale routes immediately.
    /// </summary>
    public void RefreshRouteValidity()
    {
        bool isExport = isExportMode();
        // m_assignedOutputEntities = suppliers (import routes INTO the module): need export
        // mode + truck deliveries. m_assignedInputEntities = receivers (export routes OUT of
        // the module): need import mode + truck pickups.
        m_invalidImportRouteNotif.NotifyIff(
            m_assignedOutputEntities.Count > 0 && (!isExport || IsLogisticsInputDisabled), this);
        m_invalidExportRouteNotif.NotifyIff(
            m_assignedInputEntities.Count > 0 && (isExport || IsLogisticsOutputDisabled), this);
    }

    protected override void OnDestroy()
    {
        m_assignedInputEntities.ForEachAndClear(
            (IEntityAssignedAsInput x) => x.UnassignStaticOutputEntity(this));
        m_assignedOutputEntities.ForEachAndClear(
            (IEntityAssignedAsOutput x) => x.UnassignStaticInputEntity(this));
        base.OnDestroy();
    }

    public static void Serialize(LocalTerminalModule value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    protected override void SerializeData(BlobWriter writer)
    {
        base.SerializeData(writer);
        writer.WriteBool(AllowNonAssignedOutput);
        Set<IEntityAssignedAsInput>.Serialize(m_assignedInputEntities, writer);
        Set<IEntityAssignedAsOutput>.Serialize(m_assignedOutputEntities, writer);
        EntityNotificator.Serialize(m_invalidImportRouteNotif, writer);
        EntityNotificator.Serialize(m_invalidExportRouteNotif, writer);
    }

    public new static LocalTerminalModule Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out LocalTerminalModule obj,
            (Func<BlobReader, Type, LocalTerminalModule>)null,
            (Func<BlobReader, string, LocalTerminalModule>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    protected override void DeserializeData(BlobReader reader)
    {
        base.DeserializeData(reader);
        AllowNonAssignedOutput = reader.ReadBool();
        m_assignedInputEntities = Set<IEntityAssignedAsInput>.Deserialize(reader);
        m_assignedOutputEntities = Set<IEntityAssignedAsOutput>.Deserialize(reader);
        m_invalidImportRouteNotif = EntityNotificator.Deserialize(reader);
        m_invalidExportRouteNotif = EntityNotificator.Deserialize(reader);
    }
}
