using System;
using System.Reflection;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Buildings.Storages;
using Mafi.Core.Economy;
using Mafi.Core.Entities.Static;
using Mafi.Core.Vehicles;

namespace ShippingPP.Terminals;

/// <summary>
/// The per-tick simulation of a local terminal, replacing the vanilla <c>CargoDepot.SimUpdate</c>
/// entirely (the vanilla one would pull ships from the shipwreck pool, only ever exchange cargo
/// with the depot's OWN ship — even when that ship is docked elsewhere — and raise a "depot has
/// no ship" notification for terminals that legitimately have none).
///
/// What runs instead: fuel-buffer upkeep, refueling of whichever LOCAL ship is physically docked
/// here, the dock blocked-area bookkeeping keyed to physical occupancy, and the mod's own
/// product exchange (<see cref="LocalCargoExchange"/>) between the docked ship and every module.
/// </summary>
internal static class LocalTerminalSim
{
    private static bool s_initialized;
    private static bool s_initFailed;
    private static bool s_runtimeErrorLogged;

    private static FieldInfo s_fuelBuffer;
    private static FieldInfo s_hasShip;
    private static FieldInfo s_hasShipLastStep;
    private static FieldInfo s_reservationManager;
    private static FieldInfo s_vehicleBuffersRegistry;
    private static FieldInfo s_upgradeInProgress;   // optional (hygiene only)

    public static bool TryInitialize()
    {
        if (s_initialized)
        {
            return !s_initFailed;
        }
        s_initialized = true;

        Type depot = typeof(CargoDepot);
        const BindingFlags ANY = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance;
        s_fuelBuffer = depot.GetField("m_fuelBuffer", ANY);
        s_hasShip = depot.GetField("m_hasShip", ANY);
        s_hasShipLastStep = depot.GetField("m_hasShipLastStep", ANY);
        s_reservationManager = depot.GetField("m_reservationManager", ANY);
        s_vehicleBuffersRegistry = depot.GetField("m_vehicleBuffersRegistry", ANY);
        // Not part of the init-failure check: purely cosmetic bookkeeping (see updateInternal).
        s_upgradeInProgress = depot.GetField("m_upgradeInProgress", ANY);

        s_initFailed = s_fuelBuffer == null || s_hasShip == null || s_hasShipLastStep == null
            || s_reservationManager == null || s_vehicleBuffersRegistry == null
            || !LocalCargoExchange.TryInitialize();
        if (s_initFailed)
        {
            Log.Error("Shipping++: CargoDepot internals not resolved; "
                + "local terminal simulation disabled (falling back to vanilla).");
        }
        return !s_initFailed;
    }

    /// <summary>Returns false if the vanilla SimUpdate should run instead (init failure).</summary>
    public static bool Update(LocalTerminal terminal)
    {
        if (!TryInitialize())
        {
            return false;
        }
        try
        {
            updateInternal(terminal);
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Shipping++: local terminal sim failed (logged once): {ex}");
            }
        }
        return true;
    }

    private static void updateInternal(LocalTerminal terminal)
    {
        // Vanilla clears this flag on the first constructed tick after an in-place tier upgrade
        // (and uses it to upgrade its slot ship — a slot local terminals keep empty, so only the
        // flag reset is mirrored here; the terminal's own fleet keeps its existing ships, new
        // ships are simply built at the upgraded size).
        if (s_upgradeInProgress != null && terminal.IsConstructed
            && (bool)s_upgradeInProgress.GetValue(terminal))
        {
            s_upgradeInProgress.SetValue(terminal, false);
        }

        if (terminal.IsNotEnabled)
        {
            return;
        }

        ShippingManager manager = ShippingManager.Current;
        CargoShipV2 docked = manager?.DockedLocalShipAt(terminal);

        // Blocked-area bookkeeping, keyed to PHYSICAL occupancy (vanilla keys it to its own
        // ship's docked-anywhere state, which is wrong for ships that visit other docks).
        bool hasShip = docked != null;
        bool hadShip = (bool)s_hasShip.GetValue(terminal);
        s_hasShipLastStep.SetValue(terminal, hadShip);
        s_hasShip.SetValue(terminal, hasShip);
        if (hasShip != hadShip)
        {
            var reservationManager =
                (StaticEntityOceanReservationManagerV2)s_reservationManager.GetValue(terminal);
            reservationManager.NotifyBlockedAreaChanged(terminal.OceanAreaBlocked, hasShip);
        }

        if (docked == null)
        {
            foreach (Option<CargoDepotModule> slot in terminal.Modules)
            {
                slot.ValueOrNull?.UpdateUndockedPipe();
            }
            return;
        }

        // The terminal's fuel buffer follows its own fleet's fuel type. Vanilla keys the buffer
        // product to the depot's slot ship (replaceFuelBufferIfNeeded) — the slot the mod keeps
        // empty — so the equivalent is done here: when a ship HOMED at this terminal docks
        // bearing a different fuel (the result of a fuel refit in the ship window), the buffer
        // is recreated for that fuel. Old-fuel stock returns to the asset pool, the truck
        // import slider setting carries over.
        var fuelBuffer = (LogisticsBuffer)s_fuelBuffer.GetValue(terminal);
        if (docked.AssignedDepot.ValueOrNull == terminal
            && docked.FuelProto != fuelBuffer.Product)
        {
            fuelBuffer = swapFuelBuffer(terminal, fuelBuffer, docked);
        }

        // Refuel the docked ship from the terminal's fuel buffer (any local ship, not just our
        // own — a visiting ship tops up too, same as a truck stop), but only with MATCHING
        // fuel: a visiting ship refitted to another fuel type must not get this terminal's
        // product pumped into its tank (ProductBuffer.StoreAsMuchAs does not check products).
        if (fuelBuffer.IsNotEmpty() && fuelBuffer.Product == docked.FuelProto)
        {
            Quantity taken = fuelBuffer.Quantity - docked.StoreFuelAsMuchAs(fuelBuffer.Quantity);
            fuelBuffer.RemoveExactly(taken);
        }

        foreach (Option<CargoDepotModule> slot in terminal.Modules)
        {
            CargoDepotModule module = slot.ValueOrNull;
            if (module != null)
            {
                LocalCargoExchange.Update(module, docked,
                    manager.IsExportModule(module), manager.ProductsManager);
            }
        }
    }

    /// <summary>
    /// Recreates the terminal's ship-fuel buffer for the given ship's fuel type — the reflection
    /// reimplementation of the vanilla <c>CargoDepot.replaceFuelBufferIfNeeded</c> (which is
    /// hard-wired to the depot ship slot the mod keeps empty). Stored fuel of the old type is
    /// returned to the asset pool and the truck import slider setting is preserved.
    /// </summary>
    private static LogisticsBuffer swapFuelBuffer(LocalTerminal terminal, LogisticsBuffer old,
        CargoShipV2 ship)
    {
        var registry = (IVehicleBuffersRegistry)s_vehicleBuffersRegistry.GetValue(terminal);
        Percent importStep = old.ImportUntilPercent;
        registry.TryUnregisterInputBuffer(old);
        terminal.Context.AssetTransactionManager.ClearBuffer(old);
        old.Destroy();
        var replacement = new LogisticsBuffer(ship.GetFuelReserveNeeded(), ship.FuelProto,
            usePartialTrucksForHighPriorities: true);
        if (!terminal.IsLogisticsInputDisabled)
        {
            registry.RegisterInputBufferAndAssert(terminal, replacement, terminal);
        }
        replacement.SetImportStep(importStep);
        s_fuelBuffer.SetValue(terminal, replacement);
        Log.Info($"Shipping++: terminal {terminal.Id} fuel buffer switched to "
            + $"'{ship.FuelProto.Id}'.");
        return replacement;
    }
}
