using System;
using System.Reflection;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Buildings.Storages;
using Mafi.Core.Entities.Static;

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
    private static MethodInfo s_replaceFuelBufferIfNeeded;

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
        s_replaceFuelBufferIfNeeded = depot.GetMethod("replaceFuelBufferIfNeeded", ANY);

        s_initFailed = s_fuelBuffer == null || s_hasShip == null || s_hasShipLastStep == null
            || s_reservationManager == null || s_replaceFuelBufferIfNeeded == null
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
        s_replaceFuelBufferIfNeeded.Invoke(terminal, null);
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

        // Refuel the docked ship from the terminal's fuel buffer (any local ship, not just our
        // own — a visiting ship tops up too, same as a truck stop).
        var fuelBuffer = (LogisticsBuffer)s_fuelBuffer.GetValue(terminal);
        if (fuelBuffer.IsNotEmpty())
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
}
