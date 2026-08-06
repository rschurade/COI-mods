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
using Mafi.Core.Notifications;
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
    private const int SAVE_VERSION = 8;

    /// <summary>The "ship has no home port" warning; registered in
    /// <see cref="Terminals.LocalTerminalData"/> at proto-registration time.</summary>
    public static readonly EntityNotificationProto.ID ShipHasNoHomeNotifId =
        new EntityNotificationProto.ID("ShippingPP_ShipHasNoHomePort");

    /// <summary>How many ships may wait (hold at anchor) for one dock, on top of the one being
    /// served. Docks with a full queue are not offered to further ships.</summary>
    private const int MAX_WAITING_PER_DOCK = 2;

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
    /// Materials to build one MODULE of a local cargo ship; a terminal's ship costs this times
    /// its ship's module count (2/4/6/8 per terminal tier). Set at proto-registration time.
    /// The ship proto's own cost is empty — vanilla ships are salvaged wrecks, never built.
    /// </summary>
    public static AssetValue ShipBuildCostPerModule;

    /// <summary>Materials to build the terminal's cargo ship: the per-module base scaled by the
    /// module count of the ship tier this terminal builds.</summary>
    public static AssetValue GetShipBuildCost(CargoDepot terminal)
    {
        AssetValue perModule = ShipBuildCostPerModule;
        CargoShipProto shipProto = terminal.Prototype.CargoShipProto;
        if (perModule.IsEmpty || shipProto == null)
        {
            return perModule;
        }
        var products = new Lyst<ProductQuantity>();
        foreach (ProductQuantity pq in perModule.Products)
        {
            products.Add(pq.Product.WithQuantity(
                (pq.Quantity.Value * shipProto.MaximumModulesCount).Quantity()));
        }
        return new AssetValue(products.ToImmutableArray());
    }

    private readonly Dict<CargoDepot, ShipBuildState> m_builds;
    private readonly Set<CargoShipV2> m_localShips;
    /// <summary>Per-dock arrival queue: the head ship may dock (once the dock is physically
    /// free), the rest hold at anchor outside the harbor. Entries are removed on arrival,
    /// on retarget (<see cref="ReleaseDockClaim"/>) and when ships/terminals die.</summary>
    private readonly Dict<CargoDepot, Lyst<CargoShipV2>> m_dockQueues;
    /// <summary>What each dispatched ship intends to fetch/deliver at its target — subtracted
    /// from other ships' dispatch evaluations so two ships never sail for the same cargo.</summary>
    private readonly Dict<CargoShipV2, CargoPlan> m_cargoPlans;
    /// <summary>The one ship per dock that currently holds the berth promise (granted but not
    /// yet docked). While a grant is active every other ship is denied, no matter how the
    /// queue shifts — without this, a grantee whose docking takes a few ticks (undocking
    /// predecessor, navigation cooldown) loses the berth to the next ship in line and the
    /// whole queue storms the dock at once.</summary>
    private readonly Dict<CargoDepot, CargoShipV2> m_berthGrants;
    /// <summary>Terminal modules the player switched to export ("offer") mode; absent = import.</summary>
    private readonly Set<CargoDepotModule> m_exportModules;
    /// <summary>Per-module network threshold in percent (absent = 100 = always active). Vanilla
    /// train semantics: an import module requests while filled below the threshold, an export
    /// module offers while filled above (100 - threshold).</summary>
    private readonly Dict<CargoDepotModule, int> m_moduleThresholds;
    /// <summary>Tick each terminal was last chosen as a dispatch target (round-robin fairness).</summary>
    private readonly Dict<CargoDepot, int> m_lastServed;
    /// <summary>Player-defined shipping lines (ordered cyclic stop lists).</summary>
    private readonly Lyst<Lines.ShippingLine> m_lines;
    /// <summary>Which line each assigned ship follows (absent = automatic network dispatch).</summary>
    private readonly Dict<CargoShipV2, int> m_shipLines;
    /// <summary>Active "no home port" warnings, one per orphaned ship (entries exist only for
    /// ships that are or recently were orphaned; healthy ships never get one).</summary>
    private readonly Dict<CargoShipV2, EntityNotificator> m_orphanNotifs;
    private int m_nextLineId;
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
        m_dockQueues = new Dict<CargoDepot, Lyst<CargoShipV2>>();
        m_cargoPlans = new Dict<CargoShipV2, CargoPlan>();
        m_berthGrants = new Dict<CargoDepot, CargoShipV2>();
        m_exportModules = new Set<CargoDepotModule>();
        m_moduleThresholds = new Dict<CargoDepotModule, int>();
        m_lastServed = new Dict<CargoDepot, int>();
        m_lines = new Lyst<Lines.ShippingLine>();
        m_shipLines = new Dict<CargoShipV2, int>();
        m_orphanNotifs = new Dict<CargoShipV2, EntityNotificator>();
        m_nextLineId = 1;
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
        foreach (Lines.ShippingLine line in m_lines)
        {
            line.PruneDestroyedStops();
        }
        var deadAssignments = new Lyst<CargoShipV2>();
        foreach (KeyValuePair<CargoShipV2, int> pair in m_shipLines)
        {
            if (pair.Key.IsDestroyed || TryGetLine(pair.Value) == null)
            {
                deadAssignments.Add(pair.Key);
            }
        }
        foreach (CargoShipV2 dead in deadAssignments)
        {
            m_shipLines.Remove(dead);
        }
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

    /// <summary>Number of live local ships homed at (built by) the given terminal.</summary>
    public int CountShipsHomedAt(CargoDepot terminal)
    {
        int count = 0;
        foreach (CargoShipV2 ship in m_localShips)
        {
            if (!ship.IsDestroyed && ship.AssignedDepot.ValueOrNull == terminal)
            {
                count++;
            }
        }
        return count;
    }

    private static System.Reflection.MethodInfo s_cargoShipSlotSetter;
    private static bool s_slotSetterFailed;

    /// <summary>
    /// Keeps the vanilla depot ship slot of every local terminal empty. The mod tracks its
    /// fleets itself (<see cref="m_localShips"/> + <see cref="CargoShipV2.AssignedDepot"/>);
    /// the slot's remaining vanilla effects are only harmful — most notably the depot destroys
    /// its slot ship when the depot is demolished, which would make one ship of the fleet die
    /// with the terminal while its siblings survive as re-homable orphans. Idempotent sweep:
    /// also migrates saves from when the first-built ship still took the slot.
    /// </summary>
    private void vacateVanillaShipSlots()
    {
        if (s_slotSetterFailed)
        {
            return;
        }
        foreach (LocalTerminal terminal in m_entitiesManager.GetAllEntitiesOfType<LocalTerminal>())
        {
            if (terminal.CargoShip.IsNone)
            {
                continue;
            }
            if (s_cargoShipSlotSetter == null)
            {
                s_cargoShipSlotSetter = typeof(CargoDepot).GetProperty("CargoShip")
                    ?.GetSetMethod(nonPublic: true);
                if (s_cargoShipSlotSetter == null)
                {
                    s_slotSetterFailed = true;
                    Log.Error("Shipping++: CargoDepot.CargoShip setter not found; vanilla "
                        + "ship slots stay occupied (slot ships die with their terminal).");
                    return;
                }
            }
            Log.Info($"Shipping++: vacating vanilla ship slot of terminal {terminal.Id}.");
            s_cargoShipSlotSetter.Invoke(terminal,
                new object[] { Option<CargoShipV2>.None });
        }
    }

    private static System.Reflection.MethodInfo s_setNewFuelMethod;
    private static System.Reflection.FieldInfo s_pendingFuelCostField;
    private static bool s_fuelSwitchReflectFailed;

    /// <summary>
    /// Completes pending fuel-type switches (the vanilla "replace fuel" button in the ship
    /// window, fully reused — command, cost and cancel included) for idle local ships. Vanilla
    /// finishes a switch only while the ship idles AT WORLD between trade journeys — a state
    /// local ships never reach — so the same completion (private <c>setNewFuel</c> + clearing
    /// the pending fields) is mirrored here whenever the ship has no active job. The switch
    /// returns the old tank content to the asset pool; the now-dry ship makes its emergency
    /// run home, where the terminal's fuel buffer follows the new fuel type (see
    /// <see cref="Terminals.LocalTerminalSim"/>).
    /// </summary>
    private void completePendingFuelSwitches()
    {
        if (s_fuelSwitchReflectFailed)
        {
            return;
        }
        foreach (CargoShipV2 ship in m_localShips)
        {
            if (ship.IsDestroyed || ship.PendingFuelToChangeTo.IsNone || ship.HasJobs)
            {
                continue;
            }
            if (s_setNewFuelMethod == null)
            {
                const System.Reflection.BindingFlags ANY = System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance;
                s_setNewFuelMethod = typeof(CargoShipV2).GetMethod("setNewFuel", ANY);
                s_pendingFuelCostField = typeof(CargoShipV2)
                    .GetField("m_pendingFuelChangeCost", ANY);
                if (s_setNewFuelMethod == null || s_pendingFuelCostField == null)
                {
                    s_fuelSwitchReflectFailed = true;
                    Log.Error("Shipping++: CargoShipV2 fuel-switch internals not found; fuel "
                        + "refits of local ships would stay pending forever.");
                    return;
                }
            }
            ProductProto newFuel = ship.PendingFuelToChangeTo.Value;
            s_setNewFuelMethod.Invoke(ship, new object[] { newFuel });
            ship.PendingFuelToChangeTo = Option<ProductProto>.None;
            s_pendingFuelCostField.SetValue(ship, AssetValue.Empty);
            Log.Info($"Shipping++: ship {ship.Id} refitted to fuel '{newFuel.Id}'.");
        }
    }

    /// <summary>A local ship whose home terminal is gone. Orphaned ships cannot take network
    /// jobs (all trades route through home) and cannot make emergency refuel runs — the player
    /// must give them a new home port (button in the ship window).</summary>
    public static bool IsShipOrphaned(CargoShipV2 ship)
    {
        CargoDepot home = ship.AssignedDepot.ValueOrNull;
        return home == null || home.IsDestroyed;
    }

    /// <summary>Raises the per-ship "no home port" warning for orphaned ships and clears it
    /// once the ship is re-homed or gone. Notificators are created lazily on first orphaning
    /// and dropped when their ship dies.</summary>
    private void updateOrphanNotifications()
    {
        var dead = new Lyst<CargoShipV2>();
        foreach (KeyValuePair<CargoShipV2, EntityNotificator> pair in m_orphanNotifs)
        {
            if (pair.Key.IsDestroyed || !m_localShips.Contains(pair.Key))
            {
                EntityNotificator notif = pair.Value;
                notif.Deactivate(pair.Key.Context.NotificationsManager);
                dead.Add(pair.Key);
            }
        }
        foreach (CargoShipV2 ship in dead)
        {
            m_orphanNotifs.Remove(ship);
        }
        foreach (CargoShipV2 ship in m_localShips)
        {
            if (ship.IsDestroyed)
            {
                continue;
            }
            bool orphaned = IsShipOrphaned(ship);
            if (!m_orphanNotifs.TryGetValue(ship, out EntityNotificator notif))
            {
                if (!orphaned)
                {
                    continue;
                }
                notif = ship.Context.NotificationsManager
                    .CreateNotificatorFor(ShipHasNoHomeNotifId);
            }
            notif.NotifyIff(orphaned, ship);
            m_orphanNotifs[ship] = notif; // EntityNotificator is a struct — store the mutation.
        }
    }

    /// <summary>
    /// Re-homes a local ship to another terminal (player action from the ship window). Returns
    /// an error string, or null on success. The ship's cargo modules re-mirror the new home's
    /// module layout; like reconfiguring modules on a live home terminal, vanilla destroys the
    /// cargo of ship modules whose type changes in the process.
    /// </summary>
    public string SetShipHome(CargoShipV2 ship, LocalTerminal terminal)
    {
        if (ship.IsDestroyed || !m_localShips.Contains(ship))
        {
            return "Not a local ship.";
        }
        if (terminal.IsDestroyed || !terminal.IsConstructed)
        {
            return "The terminal is not operational.";
        }
        CargoDepot oldHome = ship.AssignedDepot.ValueOrNull;
        if (oldHome == terminal)
        {
            return null;
        }
        ship.AssignCargoDepot(terminal);
        // The new home's ship-fuel buffer must hold this ship's reserve (the job the vanilla
        // depot ship slot used to do — local ships don't use it).
        object buffer = ProtoUtils.GetField(typeof(CargoDepot), terminal, "m_fuelBuffer");
        (buffer as Mafi.Core.Buildings.Storages.LogisticsBuffer)
            ?.IncreaseCapacityTo(ship.GetFuelReserveNeeded());
        // Any queue position or berth grant at the old home is meaningless now.
        ReleaseDockClaim(ship);
        Log.Info($"Shipping++: ship {ship.Id} re-homed to terminal {terminal.Id}.");
        return null;
    }

    // ------------------------------------------------------------------ lines

    internal Lyst<Lines.ShippingLine> AllLines => m_lines;

    public Lines.ShippingLine TryGetLine(int id)
    {
        foreach (Lines.ShippingLine line in m_lines)
        {
            if (line.Id == id)
            {
                return line;
            }
        }
        return null;
    }

    public Lines.ShippingLine CreateLine(CargoDepot firstStop)
    {
        var line = new Lines.ShippingLine(m_nextLineId++);
        line.AddStop(firstStop);
        m_lines.Add(line);
        return line;
    }

    public void DeleteLine(int id)
    {
        for (int i = 0; i < m_lines.Count; i++)
        {
            if (m_lines[i].Id == id)
            {
                m_lines.RemoveAt(i);
                break;
            }
        }
        // Unassign ships that followed it (they fall back to network dispatch).
        var toUnassign = new Lyst<CargoShipV2>();
        foreach (KeyValuePair<CargoShipV2, int> pair in m_shipLines)
        {
            if (pair.Value == id)
            {
                toUnassign.Add(pair.Key);
            }
        }
        foreach (CargoShipV2 ship in toUnassign)
        {
            m_shipLines.Remove(ship);
        }
    }

    /// <summary>The line id the ship is assigned to, or null (= automatic network dispatch).</summary>
    public int? GetLineIdFor(CargoShipV2 ship)
    {
        return m_shipLines.TryGetValue(ship, out int id) ? id : (int?)null;
    }

    public void SetShipLine(CargoShipV2 ship, int? lineId)
    {
        if (lineId.HasValue && TryGetLine(lineId.Value) != null)
        {
            m_shipLines[ship] = lineId.Value;
            Log.Info($"Shipping++: ship {ship.Id} assigned to line {lineId.Value}.");
        }
        else
        {
            if (lineId.HasValue)
            {
                Log.Warning($"Shipping++: ship {ship.Id} assignment to unknown line "
                    + $"{lineId.Value} ignored; ship unassigned.");
            }
            m_shipLines.Remove(ship);
        }
    }

    /// <summary>
    /// Claims a place in the terminal's dock queue for the ship (joining it if there is room)
    /// and returns true only when the ship is at the head of the queue AND the dock is
    /// physically free — i.e. it may sail in and dock right now. Ships that get false but are
    /// queued should hold at anchor (<see cref="GetQueueIndex"/> gives their position). Shared
    /// by line ships and the network dispatcher.
    /// </summary>
    public bool TryReserveDock(CargoDepot terminal, CargoShipV2 ship)
    {
        pruneQueues();
        if (terminal.IsDestroyed || !terminal.IsConstructed || terminal.IsAccessBlocked)
        {
            return false;
        }
        // An active berth promise beats everything: the grantee keeps its claim (regardless of
        // queue churn) until it has actually docked, and every other ship is denied for as
        // long as the promise stands (stale promises are cleaned up by pruneQueues).
        if (m_berthGrants.TryGetValue(terminal, out CargoShipV2 grantee))
        {
            return grantee == ship && !isDockOccupiedByOther(terminal, ship);
        }
        Lyst<CargoShipV2> queue = queueFor(terminal, create: false);
        int index = indexIn(queue, ship);
        if (index < 0)
        {
            if (queue != null && queue.Count > MAX_WAITING_PER_DOCK)
            {
                return false; // Queue full — cannot even wait here.
            }
            queue = queueFor(terminal, create: true);
            queue.Add(ship);
            index = queue.Count - 1;
        }
        bool granted = index == 0 && !isDockOccupiedByOther(terminal, ship);
        if (granted)
        {
            m_berthGrants[terminal] = ship;
        }
        return granted;
    }

    /// <summary>The ship's position in the terminal's dock queue (0 = next to dock), or -1.</summary>
    public int GetQueueIndex(CargoDepot terminal, CargoShipV2 ship)
    {
        return indexIn(queueFor(terminal, create: false), ship);
    }

    /// <summary>Whether any OTHER ship is queued for this dock (used by docked ships to decide
    /// to yield the berth instead of idling on it).</summary>
    public bool DockHasWaiters(CargoDepot terminal, CargoShipV2 except = null)
    {
        pruneQueues();
        Lyst<CargoShipV2> queue = queueFor(terminal, create: false);
        if (queue == null)
        {
            return false;
        }
        foreach (CargoShipV2 waiter in queue)
        {
            if (waiter != except)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Removes the ship from every dock queue and drops its cargo plan (call when it
    /// retargets or gives up on a leg).</summary>
    public void ReleaseDockClaim(CargoShipV2 ship)
    {
        foreach (KeyValuePair<CargoDepot, Lyst<CargoShipV2>> pair in m_dockQueues)
        {
            pair.Value.Remove(ship);
        }
        m_cargoPlans.Remove(ship);
        m_toRemoveTmp.Clear();
        foreach (KeyValuePair<CargoDepot, CargoShipV2> pair in m_berthGrants)
        {
            if (pair.Value == ship)
            {
                m_toRemoveTmp.Add(pair.Key);
            }
        }
        foreach (CargoDepot released in m_toRemoveTmp)
        {
            m_berthGrants.Remove(released);
        }
    }

    private Lyst<CargoShipV2> queueFor(CargoDepot terminal, bool create)
    {
        if (!m_dockQueues.TryGetValue(terminal, out Lyst<CargoShipV2> queue) && create)
        {
            queue = new Lyst<CargoShipV2>();
            m_dockQueues.Add(terminal, queue);
        }
        return queue;
    }

    private static int indexIn(Lyst<CargoShipV2> queue, CargoShipV2 ship)
    {
        if (queue == null)
        {
            return -1;
        }
        for (int i = 0; i < queue.Count; i++)
        {
            if (queue[i] == ship)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>Drops queue entries that arrived (ship docked at that terminal — also releasing
    /// their cargo plan there), died, or stopped being local; drops queues of dead terminals.</summary>
    private void pruneQueues()
    {
        m_toRemoveTmp.Clear();
        foreach (KeyValuePair<CargoDepot, Lyst<CargoShipV2>> pair in m_dockQueues)
        {
            Lyst<CargoShipV2> queue = pair.Value;
            for (int i = queue.Count - 1; i >= 0; i--)
            {
                CargoShipV2 ship = queue[i];
                bool arrived = !ship.IsDestroyed && ship.DockedAt.ValueOrNull == pair.Key;
                if (arrived && m_cargoPlans.TryGetValue(ship, out CargoPlan plan)
                    && plan.Terminal == pair.Key)
                {
                    m_cargoPlans.Remove(ship);
                }
                if (arrived || ship.IsDestroyed || !m_localShips.Contains(ship))
                {
                    queue.RemoveAt(i);
                }
            }
            if (queue.Count == 0 || pair.Key.IsDestroyed)
            {
                m_toRemoveTmp.Add(pair.Key);
            }
        }
        foreach (CargoDepot stale in m_toRemoveTmp)
        {
            m_dockQueues.Remove(stale);
        }
        // Berth promises: fulfilled (grantee docked there), or void (grantee dead, foreign, or
        // docked somewhere else after giving up).
        m_toRemoveTmp.Clear();
        foreach (KeyValuePair<CargoDepot, CargoShipV2> pair in m_berthGrants)
        {
            CargoShipV2 grantee = pair.Value;
            if (grantee.IsDestroyed || pair.Key.IsDestroyed || !m_localShips.Contains(grantee)
                || grantee.DockedAt.HasValue)
            {
                m_toRemoveTmp.Add(pair.Key);
            }
        }
        foreach (CargoDepot done in m_toRemoveTmp)
        {
            m_berthGrants.Remove(done);
        }
        // Plans of dead ships (retargets go through ReleaseDockClaim).
        Lyst<CargoShipV2> deadPlans = null;
        foreach (KeyValuePair<CargoShipV2, CargoPlan> pair in m_cargoPlans)
        {
            if (pair.Key.IsDestroyed || pair.Value.Terminal.IsDestroyed)
            {
                (deadPlans = deadPlans ?? new Lyst<CargoShipV2>()).Add(pair.Key);
            }
        }
        if (deadPlans != null)
        {
            foreach (CargoShipV2 dead in deadPlans)
            {
                m_cargoPlans.Remove(dead);
            }
        }
    }

    private bool isDockOccupiedByOther(CargoDepot terminal, CargoShipV2 ship)
    {
        foreach (CargoShipV2 localShip in m_localShips)
        {
            if (localShip != ship && localShip.DockedAt.ValueOrNull == terminal)
            {
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------ network dispatch

    /// <summary>
    /// The dispatcher: picks the most valuable terminal for the given ship to visit, or null.
    /// Value of a visit = products the ship could deliver there (ship cargo × the terminal's
    /// import modules' free capacity) plus products it could fetch (the terminal's export
    /// modules' stock × the ship's free module capacity for that product) — both reduced by
    /// what OTHER dispatched ships already plan to fetch/deliver there, so cargo is never
    /// promised twice. Terminals qualify while their dock queue has room; the ship joins the
    /// chosen dock's queue and its planned quantities are recorded as its cargo plan.
    /// </summary>
    public CargoDepot FindTradeTargetFor(CargoShipV2 ship)
    {
        pruneQueues();

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
        var candidatePlans = new Dict<CargoDepot, CargoPlan>();
        int bestValue = 0;
        foreach (LocalTerminal terminal in m_entitiesManager.GetAllEntitiesOfType<LocalTerminal>())
        {
            if (terminal.IsDestroyed || !terminal.IsConstructed || terminal.IsAccessBlocked
                || ship.DockedAt.ValueOrNull == terminal)
            {
                continue;
            }
            Lyst<CargoShipV2> queue = queueFor(terminal, create: false);
            if (queue != null && indexIn(queue, ship) < 0 && queue.Count > MAX_WAITING_PER_DOCK)
            {
                continue; // Dock queue full.
            }

            int value = 0;
            Quantity deliverable = Quantity.Zero;
            var plan = new CargoPlan(terminal);
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
                        Quantity stock = moduleStock(module)
                            - plannedByOthers(ship, terminal, product, fetch: true);
                        Quantity q = shipFreeCapacityFor(ship, product).Min(stock);
                        if (q.IsPositive)
                        {
                            value += q.Value;
                            plan.AddFetch(product, q);
                        }
                    }
                }
                else
                {
                    // The terminal requests: worth delivering, while filled below the threshold.
                    if (fillPercent < threshold)
                    {
                        Quantity room = module.UsableCapacity
                            - plannedByOthers(ship, terminal, product, fetch: false);
                        Quantity d = shipQuantityOf(ship, product).Min(room);
                        if (d.IsPositive)
                        {
                            value += d.Value;
                            deliverable += d;
                            plan.AddDeliver(product, d);
                        }
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
            candidatePlans[terminal] = plan;
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
            // The caller joins the dock queue via TryReserveDock; the dispatcher itself never
            // touches queues (releasing/rejoining here would churn the queue order).
            m_cargoPlans[ship] = candidatePlans[best];
        }
        else
        {
            m_cargoPlans.Remove(ship);
        }
        return best;
    }

    /// <summary>Sum other dispatched ships already plan to fetch from (or deliver to) the
    /// terminal for this product.</summary>
    private Quantity plannedByOthers(CargoShipV2 ship, CargoDepot terminal, ProductProto product,
        bool fetch)
    {
        Quantity total = Quantity.Zero;
        foreach (KeyValuePair<CargoShipV2, CargoPlan> pair in m_cargoPlans)
        {
            if (pair.Key != ship && pair.Value.Terminal == terminal)
            {
                total += fetch ? pair.Value.FetchOf(product) : pair.Value.DeliverOf(product);
            }
        }
        return total;
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
            checkSimReplacementRuns();
            vacateVanillaShipSlots();
            completePendingFuelSwitches();
            updateOrphanNotifications(); // Before pruning: dead ships' warnings need removal.
            pruneDestroyedShips();
            syncModuleDirections();
        }
    }

    private bool m_simCheckDone;

    /// <summary>
    /// Support diagnostic (once per session): local terminals exist, their SimUpdate ticks —
    /// but the mod's sim replacement never ran. That combination means another mod's Harmony
    /// prefix on <c>CargoDepot.SimUpdate</c> skips ours, leaving the terminal behaving like a
    /// vanilla depot; the log line names every patch owner so the conflict is identifiable
    /// from a user's log alone.
    /// </summary>
    private void checkSimReplacementRuns()
    {
        if (m_simCheckDone || Terminals.TerminalPoolPatch.SimHasRun)
        {
            m_simCheckDone = true;
            return;
        }
        // The prefix marks SimHasRun even for paused terminals, so any constructed live
        // terminal that has not marked it is proof the prefix is being skipped.
        foreach (LocalTerminal terminal in m_entitiesManager.GetAllEntitiesOfType<LocalTerminal>())
        {
            if (!terminal.IsDestroyed && terminal.IsConstructed)
            {
                m_simCheckDone = true;
                Terminals.TerminalPoolPatch.LogSimUpdatePatchOwners();
                return;
            }
        }
    }

    /// <summary>Whether the terminal has a ship construction in progress.</summary>
    public bool IsBuildingShip(CargoDepot terminal)
    {
        return m_builds.ContainsKey(terminal);
    }

    /// <summary>How many module slots of the terminal have a module built on them.</summary>
    public static int CountBuiltModules(CargoDepot terminal)
    {
        int count = 0;
        for (int i = 0; i < terminal.Modules.Length; i++)
        {
            if (terminal.Modules[i].HasValue)
            {
                count++;
            }
        }
        return count;
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
        if (m_builds.ContainsKey(terminal))
        {
            return "A ship is already under construction.";
        }
        // A ship's cargo modules mirror the home terminal's modules — with none built, the
        // ship could not carry anything. Partial fits are fine: the ship gains the missing
        // modules automatically when more are built on the terminal later.
        if (CountBuiltModules(terminal) == 0)
        {
            return "Build at least one terminal module first.";
        }

        // Sandbox / insta-build: the ship is created immediately and for free, matching how
        // vanilla construction behaves with insta-build enabled.
        if (m_instaBuildManager.IsInstaBuildEnabled)
        {
            createShipDockedAt(terminal);
            return null;
        }

        AssetValue cost = GetShipBuildCost(terminal);
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

    /// <summary>
    /// Creates a ship homed at the terminal and tracks it as a local ship. The first ship
    /// spawns docked; further ships spawn on the water at the dock approach — their job
    /// provider then queues them for a berth like any other arrival. Local ships never occupy
    /// the vanilla depot's ship slot (see <see cref="vacateVanillaShipSlots"/>); the slot's
    /// one useful job — sizing the terminal's ship-fuel buffer — is done here directly.
    /// </summary>
    private void createShipDockedAt(CargoDepot terminal)
    {
        CargoShipProto shipProto = ((CargoDepotProto)terminal.Prototype).CargoShipProto;
        object buffer = ProtoUtils.GetField(typeof(CargoDepot), terminal, "m_fuelBuffer");
        var fuelBuffer = buffer as Mafi.Core.Buildings.Storages.LogisticsBuffer;
        // New ships take the fuel the terminal's buffer already handles — after a fleet fuel
        // refit the buffer carries the new fuel type (see LocalTerminalSim) and a fresh ship
        // should match its siblings. First ships default to the proto's first fuel (diesel).
        ProductProto fuelProto = shipProto.AvailableFuels.First.FuelProto;
        if (fuelBuffer != null)
        {
            foreach (CargoShipProto.FuelData fuelData in shipProto.AvailableFuels)
            {
                if (fuelData.FuelProto == fuelBuffer.Product)
                {
                    fuelProto = fuelData.FuelProto;
                    break;
                }
            }
        }
        bool dockFree = DockedLocalShipAt(terminal) == null;
        CargoShipV2 ship = m_cargoShipFactory.AddCargoShip(terminal, shipProto,
            fuelProto.SomeOption(), skipSpawn: dockFree);
        if (dockFree)
        {
            ship.SpawnAtDock(terminal);
        }
        if (fuelBuffer != null)
        {
            fuelBuffer.IncreaseCapacityTo(ship.GetFuelReserveNeeded());
            // Commissioning fuel from the terminal's own fuel buffer, as far as it stretches —
            // a ship that spawns away from the dock cannot refuel until it docks, but needs
            // leg fuel to take its first job.
            if (fuelBuffer.Quantity.IsPositive && fuelBuffer.Product == ship.FuelProto)
            {
                Quantity taken = fuelBuffer.Quantity
                    - ship.StoreFuelAsMuchAs(fuelBuffer.Quantity);
                fuelBuffer.RemoveExactly(taken);
            }
        }
        m_localShips.Add(ship);
        Log.Info($"Shipping++: ship {ship.Id} built at terminal {terminal.Id} "
            + $"({(dockFree ? "docked" : "waiting off the dock")}).");
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
        writer.WriteInt(m_dockQueues.Count);
        foreach (KeyValuePair<CargoDepot, Lyst<CargoShipV2>> pair in m_dockQueues)
        {
            CargoDepot.Serialize(pair.Key, writer);
            Lyst<CargoShipV2>.Serialize(pair.Value, writer);
        }
        Set<CargoDepotModule>.Serialize(m_exportModules, writer);
        Dict<CargoDepotModule, int>.Serialize(m_moduleThresholds, writer);
        Dict<CargoDepot, int>.Serialize(m_lastServed, writer);
        writer.WriteInt(m_tickCounter);
        Lyst<Lines.ShippingLine>.Serialize(m_lines, writer);
        Dict<CargoShipV2, int>.Serialize(m_shipLines, writer);
        writer.WriteInt(m_nextLineId);
        writer.WriteInt(m_cargoPlans.Count);
        foreach (KeyValuePair<CargoShipV2, CargoPlan> pair in m_cargoPlans)
        {
            CargoShipV2.Serialize(pair.Key, writer);
            CargoPlan.Serialize(pair.Value, writer);
        }
        writer.WriteInt(m_berthGrants.Count);
        foreach (KeyValuePair<CargoDepot, CargoShipV2> pair in m_berthGrants)
        {
            CargoDepot.Serialize(pair.Key, writer);
            CargoShipV2.Serialize(pair.Value, writer);
        }
        writer.WriteInt(m_orphanNotifs.Count);
        foreach (KeyValuePair<CargoShipV2, EntityNotificator> pair in m_orphanNotifs)
        {
            CargoShipV2.Serialize(pair.Key, writer);
            EntityNotificator.Serialize(pair.Value, writer);
        }
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
        // The manually-written entity-keyed dicts below must NOT be filled here: the entity
        // instances exist but their data (including the id) is only deserialized later, so
        // inserting now hashes every key under id 0 — colliding keys, overwritten entries and
        // an unusable hash table once the real ids arrive ("two different instances with the
        // same ID: 0" in the log). The pairs are parked and inserted in initDictsAfterLoad,
        // which the reader runs after all data is in — the same trick the vanilla Dict uses.
        reader.SetField(this, "m_dockQueues", new Dict<CargoDepot, Lyst<CargoShipV2>>());
        m_loadedDockQueues = new Lyst<KeyValuePair<CargoDepot, Lyst<CargoShipV2>>>();
        if (version >= 6)
        {
            int queueCount = reader.ReadInt();
            for (int i = 0; i < queueCount; i++)
            {
                CargoDepot terminal = CargoDepot.Deserialize(reader);
                m_loadedDockQueues.Add(new KeyValuePair<CargoDepot, Lyst<CargoShipV2>>(
                    terminal, Lyst<CargoShipV2>.Deserialize(reader)));
            }
        }
        else if (version >= 2)
        {
            // v2..5 stored a single inbound ship per dock; it becomes a one-entry queue. The
            // dict is empty until its own delayed load ran, so it too is read in the init step.
            m_loadedLegacyInbound = Dict<CargoDepot, CargoShipV2>.Deserialize(reader);
        }
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
        reader.SetField(this, "m_lines", (version >= 5)
            ? Lyst<Lines.ShippingLine>.Deserialize(reader)
            : new Lyst<Lines.ShippingLine>());
        reader.SetField(this, "m_shipLines", (version >= 5)
            ? Dict<CargoShipV2, int>.Deserialize(reader)
            : new Dict<CargoShipV2, int>());
        m_nextLineId = (version >= 5) ? reader.ReadInt() : 1;
        reader.SetField(this, "m_cargoPlans", new Dict<CargoShipV2, CargoPlan>());
        m_loadedCargoPlans = new Lyst<KeyValuePair<CargoShipV2, CargoPlan>>();
        if (version >= 6)
        {
            int planCount = reader.ReadInt();
            for (int i = 0; i < planCount; i++)
            {
                CargoShipV2 ship = CargoShipV2.Deserialize(reader);
                m_loadedCargoPlans.Add(new KeyValuePair<CargoShipV2, CargoPlan>(
                    ship, CargoPlan.Deserialize(reader)));
            }
        }
        reader.SetField(this, "m_berthGrants", new Dict<CargoDepot, CargoShipV2>());
        m_loadedBerthGrants = new Lyst<KeyValuePair<CargoDepot, CargoShipV2>>();
        if (version >= 7)
        {
            int grantCount = reader.ReadInt();
            for (int i = 0; i < grantCount; i++)
            {
                CargoDepot terminal = CargoDepot.Deserialize(reader);
                m_loadedBerthGrants.Add(new KeyValuePair<CargoDepot, CargoShipV2>(
                    terminal, CargoShipV2.Deserialize(reader)));
            }
        }
        reader.SetField(this, "m_orphanNotifs", new Dict<CargoShipV2, EntityNotificator>());
        m_loadedOrphanNotifs = new Lyst<KeyValuePair<CargoShipV2, EntityNotificator>>();
        if (version >= 8)
        {
            int notifCount = reader.ReadInt();
            for (int i = 0; i < notifCount; i++)
            {
                CargoShipV2 ship = CargoShipV2.Deserialize(reader);
                m_loadedOrphanNotifs.Add(new KeyValuePair<CargoShipV2, EntityNotificator>(
                    ship, EntityNotificator.Deserialize(reader)));
            }
        }
        reader.RegisterInitAfterLoad(this, nameof(initDictsAfterLoad), InitPriority.Normal);
        s_current = this;
    }

    /// <summary>Parked entity-keyed pairs from <see cref="DeserializeData"/>, inserted into the
    /// real dicts by <see cref="initDictsAfterLoad"/> once entity ids are loaded.</summary>
    private Lyst<KeyValuePair<CargoDepot, Lyst<CargoShipV2>>> m_loadedDockQueues;
    private Dict<CargoDepot, CargoShipV2> m_loadedLegacyInbound;
    private Lyst<KeyValuePair<CargoShipV2, CargoPlan>> m_loadedCargoPlans;
    private Lyst<KeyValuePair<CargoDepot, CargoShipV2>> m_loadedBerthGrants;
    private Lyst<KeyValuePair<CargoShipV2, EntityNotificator>> m_loadedOrphanNotifs;

    private void initDictsAfterLoad()
    {
        foreach (KeyValuePair<CargoDepot, Lyst<CargoShipV2>> pair in m_loadedDockQueues)
        {
            m_dockQueues[pair.Key] = pair.Value;
        }
        if (m_loadedLegacyInbound != null)
        {
            foreach (KeyValuePair<CargoDepot, CargoShipV2> pair in m_loadedLegacyInbound)
            {
                m_dockQueues[pair.Key] = new Lyst<CargoShipV2> { pair.Value };
            }
        }
        foreach (KeyValuePair<CargoShipV2, CargoPlan> pair in m_loadedCargoPlans)
        {
            m_cargoPlans[pair.Key] = pair.Value;
        }
        foreach (KeyValuePair<CargoDepot, CargoShipV2> pair in m_loadedBerthGrants)
        {
            m_berthGrants[pair.Key] = pair.Value;
        }
        foreach (KeyValuePair<CargoShipV2, EntityNotificator> pair in m_loadedOrphanNotifs)
        {
            m_orphanNotifs[pair.Key] = pair.Value;
        }
        m_loadedDockQueues = null;
        m_loadedLegacyInbound = null;
        m_loadedCargoPlans = null;
        m_loadedBerthGrants = null;
        m_loadedOrphanNotifs = null;
    }

    /// <summary>A dispatched ship's intended exchange at its target terminal.</summary>
    internal sealed class CargoPlan
    {
        public CargoDepot Terminal { get; private set; }
        private Lyst<KeyValuePair<ProductProto, Quantity>> m_fetch;
        private Lyst<KeyValuePair<ProductProto, Quantity>> m_deliver;

        private static readonly Action<object, BlobWriter> s_serializeDataDelayedAction =
            (obj, writer) => ((CargoPlan)obj).SerializeData(writer);
        private static readonly Action<object, BlobReader> s_deserializeDataDelayedAction =
            (obj, reader) => ((CargoPlan)obj).DeserializeData(reader);

        public CargoPlan(CargoDepot terminal)
        {
            Terminal = terminal;
            m_fetch = new Lyst<KeyValuePair<ProductProto, Quantity>>();
            m_deliver = new Lyst<KeyValuePair<ProductProto, Quantity>>();
        }

        public void AddFetch(ProductProto product, Quantity quantity)
        {
            m_fetch.Add(new KeyValuePair<ProductProto, Quantity>(product, quantity));
        }

        public void AddDeliver(ProductProto product, Quantity quantity)
        {
            m_deliver.Add(new KeyValuePair<ProductProto, Quantity>(product, quantity));
        }

        public Quantity FetchOf(ProductProto product)
        {
            return sumOf(m_fetch, product);
        }

        public Quantity DeliverOf(ProductProto product)
        {
            return sumOf(m_deliver, product);
        }

        private static Quantity sumOf(Lyst<KeyValuePair<ProductProto, Quantity>> list,
            ProductProto product)
        {
            Quantity total = Quantity.Zero;
            foreach (KeyValuePair<ProductProto, Quantity> pair in list)
            {
                if (pair.Key == product)
                {
                    total += pair.Value;
                }
            }
            return total;
        }

        public static void Serialize(CargoPlan value, BlobWriter writer)
        {
            if (writer.TryStartClassSerialization(value))
            {
                writer.EnqueueDataSerialization(value, s_serializeDataDelayedAction);
            }
        }

        private void SerializeData(BlobWriter writer)
        {
            CargoDepot.Serialize(Terminal, writer);
            writeList(writer, m_fetch);
            writeList(writer, m_deliver);
        }

        private static void writeList(BlobWriter writer,
            Lyst<KeyValuePair<ProductProto, Quantity>> list)
        {
            writer.WriteInt(list.Count);
            foreach (KeyValuePair<ProductProto, Quantity> pair in list)
            {
                writer.WriteGeneric(pair.Key);
                writer.WriteInt(pair.Value.Value);
            }
        }

        public static CargoPlan Deserialize(BlobReader reader)
        {
            if (reader.TryStartClassDeserialization(out CargoPlan obj,
                (Func<BlobReader, Type, CargoPlan>)null,
                (Func<BlobReader, string, CargoPlan>)null, nullObjIsOk: false))
            {
                reader.EnqueueDataDeserialization(obj, s_deserializeDataDelayedAction);
            }
            return obj;
        }

        private void DeserializeData(BlobReader reader)
        {
            Terminal = CargoDepot.Deserialize(reader);
            m_fetch = readList(reader);
            m_deliver = readList(reader);
        }

        private static Lyst<KeyValuePair<ProductProto, Quantity>> readList(BlobReader reader)
        {
            var list = new Lyst<KeyValuePair<ProductProto, Quantity>>();
            int count = reader.ReadInt();
            for (int i = 0; i < count; i++)
            {
                var product = reader.ReadGenericAs<ProductProto>();
                var quantity = new Quantity(reader.ReadInt());
                list.Add(new KeyValuePair<ProductProto, Quantity>(product, quantity));
            }
            return list;
        }
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
