using System;
using System.Collections.Generic;
using Mafi;
using Mafi.Collections;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Buildings.Cargo.Ships.Modules;
using Mafi.Core.Economy;
using Mafi.Core.Entities;
using Mafi.Core.Entities.Static;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Core.Simulation;
using Mafi.Core.Utils;
using Mafi.Core.Vehicles;
using Mafi.Serialization;
using ShippingPP.Terminals;

namespace ShippingPP;

/// <summary>
/// The mod's central, save-persisted manager. Currently owns ship provisioning: every constructed
/// local terminal without a ship gets a ship construction site — the ship's construction products
/// are requested via truck logistics (temporary <see cref="ProductBuffer"/>s registered as input
/// buffers, the same mechanism the vanilla shipyard uses for battleship repairs), and when all
/// materials are delivered and the build time has elapsed, the ship is created already docked at
/// the terminal. Ships created here are tracked so they never enter (or corrupt the accounting of)
/// the vanilla shipwreck pool — see <see cref="TerminalPoolPatch"/>.
///
/// Serialization follows the vanilla manager pattern exactly (public static Serialize/Deserialize
/// pair + delayed data serialization); the whole state — including entity references and the
/// registered buffers — is restored from the save's object graph. The constructor only runs for
/// new games or when the mod is first added to a save; after loading, state is re-established in
/// DeserializeData (the saveable event subscriptions are restored by the events themselves).
/// </summary>
public class ShippingManager
{
    /// <summary>Version stamp of this manager's own save data (bump when the format changes).</summary>
    private const int SAVE_VERSION = 4;

    /// <summary>A trip must move at least this share of the ship's cargo capacity to be worth
    /// dispatching (the train network's "wait for a worthwhile load" rule). A ship whose whole
    /// current cargo can be dumped at the target is always allowed to sail.</summary>
    private const int MIN_LOAD_PERCENT = 20;

    /// <summary>Extra build time on top of material delivery.</summary>
    private static readonly Duration BUILD_EXTRA_DURATION = Duration.FromSec(60);
    private const int SCAN_PERIOD_TICKS = 30;
    private const int BUFFER_IMPORT_PRIORITY = 9;

    private static ShippingManager s_current;

    /// <summary>
    /// Materials to build one local cargo ship. Set at proto-registration time (vanilla's price
    /// of a cargo ship: the ships settlement sells one for 600 Construction parts III). The ship
    /// proto's own cost is empty — vanilla ships are salvaged wrecks, never built.
    /// </summary>
    public static AssetValue ShipBuildCost;

    private readonly Dict<CargoDepot, ShipBuildState> m_builds;
    private readonly Set<CargoShipV2> m_localShips;
    /// <summary>Which ship is currently heading for which terminal's dock (one inbound per dock).</summary>
    private readonly Dict<CargoDepot, CargoShipV2> m_inboundShips;
    /// <summary>Terminal modules the player switched to export ("offer") mode; absent = import.</summary>
    private readonly Set<CargoDepotModule> m_exportModules;
    /// <summary>Per-module network threshold in percent (absent = 100 = always active). Vanilla
    /// train semantics: an import module requests while filled below the threshold, an export
    /// module offers while filled above (100 - threshold).</summary>
    private readonly Dict<CargoDepotModule, int> m_moduleThresholds;
    /// <summary>Tick each terminal was last chosen as a dispatch target (round-robin fairness).</summary>
    private readonly Dict<CargoDepot, int> m_lastServed;
    private readonly Lyst<CargoDepot> m_toRemoveTmp;
    private readonly EntitiesManager m_entitiesManager;
    private readonly ISimLoopEvents m_simLoopEvents;
    private readonly IVehicleBuffersRegistry m_buffersRegistry;
    private readonly IProductsManager m_productsManager;
    private readonly ICargoShipFactory m_cargoShipFactory;
    private readonly IInstaBuildManager m_instaBuildManager;
    private readonly BuildBufferPriorityProvider m_priorityProvider;

    private int m_tickCounter;

    private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
        (obj, writer) => ((ShippingManager)obj).SerializeData(writer);
    private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
        (obj, reader) => ((ShippingManager)obj).DeserializeData(reader);

    public ShippingManager(EntitiesManager entitiesManager, ISimLoopEvents simLoopEvents,
        IVehicleBuffersRegistry buffersRegistry, IProductsManager productsManager,
        ICargoShipFactory cargoShipFactory, IInstaBuildManager instaBuildManager)
    {
        m_builds = new Dict<CargoDepot, ShipBuildState>();
        m_localShips = new Set<CargoShipV2>();
        m_inboundShips = new Dict<CargoDepot, CargoShipV2>();
        m_exportModules = new Set<CargoDepotModule>();
        m_moduleThresholds = new Dict<CargoDepotModule, int>();
        m_lastServed = new Dict<CargoDepot, int>();
        m_toRemoveTmp = new Lyst<CargoDepot>();
        m_entitiesManager = entitiesManager;
        m_simLoopEvents = simLoopEvents;
        m_buffersRegistry = buffersRegistry;
        m_productsManager = productsManager;
        m_cargoShipFactory = cargoShipFactory;
        m_instaBuildManager = instaBuildManager;
        m_priorityProvider = new BuildBufferPriorityProvider();
        simLoopEvents.Update.Add(this, update);
        m_entitiesManager.StaticEntityRemoved.Add(this, entityRemoved);
        s_current = this;
        Log.Info("Shipping++: shipping manager created.");
    }

    /// <summary>The manager of the current game session (set on creation and on load).</summary>
    public static ShippingManager Current => s_current;

    internal IProductsManager ProductsManager => m_productsManager;

    /// <summary>The local ship physically docked at the given terminal, or null.</summary>
    public CargoShipV2 DockedLocalShipAt(CargoDepot terminal)
    {
        foreach (CargoShipV2 ship in m_localShips)
        {
            if (!ship.IsDestroyed && ship.DockedAt.ValueOrNull == terminal)
            {
                return ship;
            }
        }
        return null;
    }

    /// <summary>Whether the module is in export ("offer") mode; default is import ("request").</summary>
    public bool IsExportModule(CargoDepotModule module)
    {
        return m_exportModules.Contains(module);
    }

    public void SetModuleExport(CargoDepotModule module, bool isExport)
    {
        bool changed = isExport ? m_exportModules.Add(module) : m_exportModules.Remove(module);
        if (changed)
        {
            applyModuleDirection(module);
        }
    }

    /// <summary>Network threshold in percent (100 = always active, the default).</summary>
    public int GetModuleThreshold(CargoDepotModule module)
    {
        return m_moduleThresholds.TryGetValue(module, out int value) ? value : 100;
    }

    public void SetModuleThreshold(CargoDepotModule module, int percent)
    {
        percent = percent.Clamp(10, 100);
        if (percent == 100)
        {
            m_moduleThresholds.Remove(module);
        }
        else
        {
            m_moduleThresholds[module] = percent;
        }
    }

    /// <summary>
    /// Brings a module's truck-logistics registration in line with its direction: an export
    /// module consumes factory products (global input, truck deliveries enabled), an import
    /// module offers them (global output, truck pickups enabled). Idempotent; re-run
    /// periodically because product (re)assignment recreates the buffer with vanilla defaults.
    /// </summary>
    private void applyModuleDirection(CargoDepotModule module)
    {
        bool isExport = IsExportModule(module);
        object buffer = ProtoUtils.GetField(typeof(CargoDepotModule), module,
            "<Buffer>k__BackingField");
        if (buffer is Option<Mafi.Core.Buildings.Storages.LogisticsBuffer> option
            && option.HasValue)
        {
            System.Reflection.MethodInfo setIsInput = option.Value.GetType()
                .GetMethod("SetIsInput", System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
            setIsInput?.Invoke(option.Value, new object[] { isExport });
        }
        if (module.IsLogisticsInputDisabled == isExport)
        {
            module.SetLogisticsInputDisabled(!isExport);
        }
        if (module.IsLogisticsOutputDisabled != isExport)
        {
            module.SetLogisticsOutputDisabled(isExport);
        }
    }

    /// <summary>Periodic idempotent re-apply for all terminal modules (and pruning).</summary>
    private void syncModuleDirections()
    {
        m_exportModules.RemoveWhere(m => m.IsDestroyed);
        pruneDestroyedKeys(m_moduleThresholds);
        pruneDestroyedKeys(m_lastServed);
        foreach (LocalTerminal terminal in m_entitiesManager.GetAllEntitiesOfType<LocalTerminal>())
        {
            foreach (Option<CargoDepotModule> slot in terminal.Modules)
            {
                CargoDepotModule module = slot.ValueOrNull;
                if (module != null && module.StoredProduct.HasValue)
                {
                    applyModuleDirection(module);
                }
            }
        }
    }

    /// <summary>Whether the given ship was built at a local terminal (vs. the vanilla pool).</summary>
    public static bool IsLocalShip(CargoShipV2 ship)
    {
        return s_current != null && s_current.m_localShips.Contains(ship);
    }

    /// <summary>
    /// The dispatcher: picks the most valuable terminal for the given ship to visit, or null.
    /// Value of a visit = products the ship could deliver there (ship cargo × the terminal's
    /// import modules' free capacity) plus products it could fetch (the terminal's export
    /// modules' stock × the ship's free module capacity for that product). Only terminals with
    /// a free, unreserved, accessible dock qualify; the chosen dock is reserved for the ship.
    /// </summary>
    public CargoDepot FindTradeTargetFor(CargoShipV2 ship)
    {
        // Release this ship's previous reservation and prune stale ones.
        m_toRemoveTmp.Clear();
        foreach (KeyValuePair<CargoDepot, CargoShipV2> pair in m_inboundShips)
        {
            if (pair.Value == ship || pair.Value.IsDestroyed || pair.Key.IsDestroyed
                || pair.Value.DockedAt.HasValue)
            {
                m_toRemoveTmp.Add(pair.Key);
            }
        }
        foreach (CargoDepot stale in m_toRemoveTmp)
        {
            m_inboundShips.Remove(stale);
        }

        // Min-load gate: the trip must move at least MIN_LOAD_PERCENT of the ship's capacity —
        // unless the target can absorb the ship's whole current cargo (never strand goods).
        Quantity shipCapacityTotal = Quantity.Zero;
        Quantity shipCargoTotal = Quantity.Zero;
        for (int i = 0; i < ship.Modules.Count; i++)
        {
            CargoShipModule module = ship.Modules[i].ValueOrNull;
            if (module != null)
            {
                shipCapacityTotal += module.Capacity;
                shipCargoTotal += module.Quantity;
            }
        }
        int minTripValue = shipCapacityTotal.Value * MIN_LOAD_PERCENT / 100;

        var candidates = new Lyst<KeyValuePair<CargoDepot, int>>();
        int bestValue = 0;
        foreach (LocalTerminal terminal in m_entitiesManager.GetAllEntitiesOfType<LocalTerminal>())
        {
            // Occupied = some ship is physically docked AT this terminal (the terminal's own
            // ship may be docked elsewhere, which must not block its home dock).
            bool dockOccupied = false;
            foreach (CargoShipV2 localShip in m_localShips)
            {
                if (localShip != ship && localShip.DockedAt.ValueOrNull == terminal)
                {
                    dockOccupied = true;
                    break;
                }
            }
            if (terminal.IsDestroyed || !terminal.IsConstructed || terminal.IsAccessBlocked
                || ship.DockedAt.ValueOrNull == terminal || dockOccupied
                || m_inboundShips.ContainsKey(terminal))
            {
                continue;
            }

            int value = 0;
            Quantity deliverable = Quantity.Zero;
            foreach (Option<CargoDepotModule> slot in terminal.Modules)
            {
                CargoDepotModule module = slot.ValueOrNull;
                if (module == null || module.StoredProduct.IsNone || !module.IsEnabled)
                {
                    continue;
                }
                ProductProto product = module.StoredProduct.Value;
                int fillPercent = module.Capacity.IsPositive
                    ? module.CurrentQuantity.Value * 100 / module.Capacity.Value
                    : 0;
                int threshold = GetModuleThreshold(module);
                if (IsExportModule(module))
                {
                    // The terminal offers: worth fetching, while filled above (100 - threshold).
                    if (fillPercent > 100 - threshold)
                    {
                        value += shipFreeCapacityFor(ship, product).Min(moduleStock(module)).Value;
                    }
                }
                else
                {
                    // The terminal requests: worth delivering, while filled below the threshold.
                    if (fillPercent < threshold)
                    {
                        Quantity d = shipQuantityOf(ship, product).Min(module.UsableCapacity);
                        value += d.Value;
                        deliverable += d;
                    }
                }
            }
            if (value <= 0)
            {
                continue;
            }
            bool fullDump = shipCargoTotal.IsPositive && deliverable >= shipCargoTotal;
            if (value < minTripValue && !fullDump)
            {
                continue;
            }
            candidates.Add(new KeyValuePair<CargoDepot, int>(terminal, value));
            bestValue = bestValue.Max(value);
        }

        // Among candidates close to the best value (within 20%), prefer the least recently
        // served terminal so equal requesters take turns instead of starving.
        CargoDepot best = null;
        int bestLastServed = int.MaxValue;
        foreach (KeyValuePair<CargoDepot, int> candidate in candidates)
        {
            if (candidate.Value * 5 < bestValue * 4)
            {
                continue;
            }
            int lastServed = m_lastServed.TryGetValue(candidate.Key, out int tick)
                ? tick : int.MinValue;
            if (best == null || lastServed < bestLastServed)
            {
                best = candidate.Key;
                bestLastServed = lastServed;
            }
        }
        if (best != null)
        {
            m_lastServed[best] = m_tickCounter;
            m_inboundShips[best] = ship;
        }
        return best;
    }

    private static Quantity moduleStock(CargoDepotModule module)
    {
        return module.CurrentQuantity;
    }

    private static Quantity shipQuantityOf(CargoShipV2 ship, ProductProto product)
    {
        Quantity total = Quantity.Zero;
        for (int i = 0; i < ship.Modules.Count; i++)
        {
            CargoShipModule module = ship.Modules[i].ValueOrNull;
            if (module != null && module.StoredProduct.ValueOrNull == product)
            {
                total += module.Quantity;
            }
        }
        return total;
    }

    private static Quantity shipFreeCapacityFor(CargoShipV2 ship, ProductProto product)
    {
        Quantity total = Quantity.Zero;
        for (int i = 0; i < ship.Modules.Count; i++)
        {
            CargoShipModule module = ship.Modules[i].ValueOrNull;
            if (module != null && module.StoredProduct.ValueOrNull == product)
            {
                total += module.UsableCapacity;
            }
        }
        return total;
    }

    private void update()
    {
        stepBuilds();
        if (++m_tickCounter % SCAN_PERIOD_TICKS == 0)
        {
            pruneDestroyedShips();
            syncModuleDirections();
        }
    }

    /// <summary>Whether the terminal has a ship construction in progress.</summary>
    public bool IsBuildingShip(CargoDepot terminal)
    {
        return m_builds.ContainsKey(terminal);
    }

    public Option<ConstructionProgress> TryGetShipBuildProgress(CargoDepot terminal)
    {
        return m_builds.TryGetValue(terminal, out ShipBuildState state)
            ? state.Progress.SomeOption()
            : Option<ConstructionProgress>.None;
    }

    /// <summary>
    /// Opens a ship construction site on the terminal (player-triggered). Returns an error string,
    /// or null on success. The ship's construction materials are then requested via truck
    /// logistics and the ship is created docked when everything is delivered.
    /// </summary>
    public string StartShipConstruction(CargoDepot terminal)
    {
        if (!(terminal.Prototype is LocalTerminalProto))
        {
            return "Not a local cargo terminal.";
        }
        if (terminal.IsDestroyed || !terminal.IsConstructed)
        {
            return "The terminal is not fully constructed.";
        }
        if (terminal.CargoShip.HasValue)
        {
            return "The terminal already has a ship.";
        }
        if (m_builds.ContainsKey(terminal))
        {
            return "A ship is already under construction.";
        }

        // Sandbox / insta-build: the ship is created immediately and for free, matching how
        // vanilla construction behaves with insta-build enabled.
        if (m_instaBuildManager.IsInstaBuildEnabled)
        {
            createShipDockedAt(terminal);
            return null;
        }

        AssetValue cost = ShipBuildCost;
        if (cost.IsEmpty)
        {
            Log.Warning("Shipping++: no ship build cost configured; building the ship for free.");
            createShipDockedAt(terminal);
            return null;
        }
        var buffers = new Lyst<ProductBuffer>();
        foreach (ProductQuantity item in cost.Products)
        {
            var buffer = new ProductBuffer(item.Quantity, item.Product);
            buffers.Add(buffer);
            m_buffersRegistry.RegisterInputBufferAndAssert(terminal, buffer, m_priorityProvider);
        }
        var progress = new ConstructionProgress(terminal, buffers.ToImmutableArray(), cost,
            Duration.OneTick, BUILD_EXTRA_DURATION);
        m_builds.Add(terminal, new ShipBuildState(terminal, buffers, progress));
        Log.Info($"Shipping++: started ship construction at terminal {terminal.Id} ({cost}).");
        return null;
    }

    /// <summary>Cancels the terminal's ship construction; delivered materials are lost.</summary>
    public bool CancelShipConstruction(CargoDepot terminal)
    {
        if (!m_builds.TryGetValue(terminal, out ShipBuildState state))
        {
            return false;
        }
        cancelBuild(state, reportDestroyed: true);
        m_builds.Remove(terminal);
        Log.Info($"Shipping++: cancelled ship construction at terminal {terminal.Id}.");
        return true;
    }

    private void stepBuilds()
    {
        if (m_builds.Count == 0)
        {
            return;
        }
        m_toRemoveTmp.Clear();
        foreach (KeyValuePair<CargoDepot, ShipBuildState> pair in m_builds)
        {
            ShipBuildState state = pair.Value;
            if (pair.Key.IsDestroyed)
            {
                cancelBuild(state, reportDestroyed: true);
                m_toRemoveTmp.Add(pair.Key);
                continue;
            }
            state.Progress.TryMakeStep();
            if (state.Progress.IsDone || m_instaBuildManager.IsInstaBuildEnabled)
            {
                finishBuild(state);
                m_toRemoveTmp.Add(pair.Key);
            }
        }
        foreach (CargoDepot done in m_toRemoveTmp)
        {
            m_builds.Remove(done);
        }
    }

    private void finishBuild(ShipBuildState state)
    {
        foreach (ProductBuffer buffer in state.Buffers)
        {
            if (buffer.Quantity.IsPositive)
            {
                m_productsManager.ProductDestroyed(((Proto)state.Terminal.Prototype).SomeOption(),
                    buffer.Product, buffer.Quantity, DestroyReason.Construction);
            }
            m_buffersRegistry.UnregisterInputBufferAndAssert(buffer);
        }
        createShipDockedAt(state.Terminal);
    }

    /// <summary>Creates the terminal's ship, already docked, and tracks it as a local ship.</summary>
    private void createShipDockedAt(CargoDepot terminal)
    {
        CargoShipProto shipProto = ((CargoDepotProto)terminal.Prototype).CargoShipProto;
        Option<ProductProto> fuel = shipProto.AvailableFuels.First.FuelProto.SomeOption();
        CargoShipV2 ship = m_cargoShipFactory.AddCargoShip(terminal, shipProto, fuel,
            skipSpawn: true);
        ship.SpawnAtDock(terminal);
        terminal.ReplaceShipAndDestroyCurrent(ship);
        m_localShips.Add(ship);
        Log.Info($"Shipping++: ship {ship.Id} built and docked at terminal {terminal.Id}.");
    }

    /// <summary>Aborts a build; any already-delivered materials are lost (reported destroyed).</summary>
    private void cancelBuild(ShipBuildState state, bool reportDestroyed)
    {
        foreach (ProductBuffer buffer in state.Buffers)
        {
            if (reportDestroyed && buffer.Quantity.IsPositive)
            {
                m_productsManager.ProductDestroyed(((Proto)state.Terminal.Prototype).SomeOption(),
                    buffer.Product, buffer.Quantity, DestroyReason.General);
            }
            m_buffersRegistry.TryUnregisterInputBuffer(buffer);
        }
    }

    private void pruneDestroyedShips()
    {
        m_localShips.RemoveWhere(ship => ship.IsDestroyed);
    }

    private static void pruneDestroyedKeys<TKey>(Dict<TKey, int> dict)
        where TKey : Mafi.Core.Entities.IEntity
    {
        Lyst<TKey> toRemove = null;
        foreach (KeyValuePair<TKey, int> pair in dict)
        {
            if (pair.Key.IsDestroyed)
            {
                (toRemove = toRemove ?? new Lyst<TKey>()).Add(pair.Key);
            }
        }
        if (toRemove != null)
        {
            foreach (TKey key in toRemove)
            {
                dict.Remove(key);
            }
        }
    }

    private void entityRemoved(IStaticEntity entity)
    {
        if (entity is CargoDepot depot && m_builds.TryGetValue(depot, out ShipBuildState state))
        {
            cancelBuild(state, reportDestroyed: true);
            m_builds.Remove(depot);
        }
    }

    public static void Serialize(ShippingManager value, BlobWriter writer)
    {
        if (writer.TryStartClassSerialization(value))
        {
            writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
        }
    }

    private void SerializeData(BlobWriter writer)
    {
        writer.WriteInt(SAVE_VERSION);
        Dict<CargoDepot, ShipBuildState>.Serialize(m_builds, writer);
        Set<CargoShipV2>.Serialize(m_localShips, writer);
        EntitiesManager.Serialize(m_entitiesManager, writer);
        writer.WriteGeneric(m_simLoopEvents);
        writer.WriteGeneric(m_buffersRegistry);
        writer.WriteGeneric(m_productsManager);
        writer.WriteGeneric(m_cargoShipFactory);
        writer.WriteGeneric(m_instaBuildManager);
        BuildBufferPriorityProvider.Serialize(m_priorityProvider, writer);
        Dict<CargoDepot, CargoShipV2>.Serialize(m_inboundShips, writer);
        Set<CargoDepotModule>.Serialize(m_exportModules, writer);
        Dict<CargoDepotModule, int>.Serialize(m_moduleThresholds, writer);
        Dict<CargoDepot, int>.Serialize(m_lastServed, writer);
        writer.WriteInt(m_tickCounter);
    }

    public static ShippingManager Deserialize(BlobReader reader)
    {
        if (reader.TryStartClassDeserialization(out ShippingManager obj,
            (Func<BlobReader, Type, ShippingManager>)null,
            (Func<BlobReader, string, ShippingManager>)null, nullObjIsOk: false))
        {
            reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
        }
        return obj;
    }

    private void DeserializeData(BlobReader reader)
    {
        int version = reader.ReadInt();
        reader.SetField(this, "m_builds", Dict<CargoDepot, ShipBuildState>.Deserialize(reader));
        reader.SetField(this, "m_localShips", Set<CargoShipV2>.Deserialize(reader));
        reader.SetField(this, "m_toRemoveTmp", new Lyst<CargoDepot>());
        reader.SetField(this, "m_entitiesManager", EntitiesManager.Deserialize(reader));
        reader.SetField(this, "m_simLoopEvents", reader.ReadGenericAs<ISimLoopEvents>());
        reader.SetField(this, "m_buffersRegistry", reader.ReadGenericAs<IVehicleBuffersRegistry>());
        reader.SetField(this, "m_productsManager", reader.ReadGenericAs<IProductsManager>());
        reader.SetField(this, "m_cargoShipFactory", reader.ReadGenericAs<ICargoShipFactory>());
        reader.SetField(this, "m_instaBuildManager", reader.ReadGenericAs<IInstaBuildManager>());
        reader.SetField(this, "m_priorityProvider", BuildBufferPriorityProvider.Deserialize(reader));
        reader.SetField(this, "m_inboundShips", (version >= 2)
            ? Dict<CargoDepot, CargoShipV2>.Deserialize(reader)
            : new Dict<CargoDepot, CargoShipV2>());
        reader.SetField(this, "m_exportModules", (version >= 3)
            ? Set<CargoDepotModule>.Deserialize(reader)
            : new Set<CargoDepotModule>());
        reader.SetField(this, "m_moduleThresholds", (version >= 4)
            ? Dict<CargoDepotModule, int>.Deserialize(reader)
            : new Dict<CargoDepotModule, int>());
        reader.SetField(this, "m_lastServed", (version >= 4)
            ? Dict<CargoDepot, int>.Deserialize(reader)
            : new Dict<CargoDepot, int>());
        if (version >= 4)
        {
            m_tickCounter = reader.ReadInt();
        }
        s_current = this;
    }

    /// <summary>State of one terminal's ship construction site.</summary>
    internal sealed class ShipBuildState
    {
        public CargoDepot Terminal { get; private set; }
        public Lyst<ProductBuffer> Buffers { get; private set; }
        public ConstructionProgress Progress { get; private set; }

        private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
            (obj, writer) => ((ShipBuildState)obj).SerializeData(writer);
        private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
            (obj, reader) => ((ShipBuildState)obj).DeserializeData(reader);

        public ShipBuildState(CargoDepot terminal, Lyst<ProductBuffer> buffers,
            ConstructionProgress progress)
        {
            Terminal = terminal;
            Buffers = buffers;
            Progress = progress;
        }

        public static void Serialize(ShipBuildState value, BlobWriter writer)
        {
            if (writer.TryStartClassSerialization(value))
            {
                writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
            }
        }

        private void SerializeData(BlobWriter writer)
        {
            CargoDepot.Serialize(Terminal, writer);
            Lyst<ProductBuffer>.Serialize(Buffers, writer);
            ConstructionProgress.Serialize(Progress, writer);
        }

        public static ShipBuildState Deserialize(BlobReader reader)
        {
            if (reader.TryStartClassDeserialization(out ShipBuildState obj,
                (Func<BlobReader, Type, ShipBuildState>)null,
                (Func<BlobReader, string, ShipBuildState>)null, nullObjIsOk: false))
            {
                reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
            }
            return obj;
        }

        private void DeserializeData(BlobReader reader)
        {
            Terminal = CargoDepot.Deserialize(reader);
            Buffers = Lyst<ProductBuffer>.Deserialize(reader);
            Progress = ConstructionProgress.Deserialize(reader);
        }
    }

    /// <summary>
    /// Truck-logistics priority for ship construction materials: fixed priority, optimal quantity
    /// = whatever is still missing (same shape as the vanilla shipyard's repair provider).
    /// </summary>
    internal sealed class BuildBufferPriorityProvider : IInputBufferPriorityProvider
    {
        private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
            (obj, writer) => { };
        private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
            (obj, reader) => { };

        public BufferStrategy GetInputPriority(IProductBuffer buffer, Quantity pendingQuantity)
        {
            return new BufferStrategy(BUFFER_IMPORT_PRIORITY,
                buffer.UsableCapacity - pendingQuantity);
        }

        public static void Serialize(BuildBufferPriorityProvider value, BlobWriter writer)
        {
            if (writer.TryStartClassSerialization(value))
            {
                writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
            }
        }

        public static BuildBufferPriorityProvider Deserialize(BlobReader reader)
        {
            if (reader.TryStartClassDeserialization(out BuildBufferPriorityProvider obj,
                (Func<BlobReader, Type, BuildBufferPriorityProvider>)null,
                (Func<BlobReader, string, BuildBufferPriorityProvider>)null, nullObjIsOk: false))
            {
                reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
            }
            return obj;
        }
    }
}
