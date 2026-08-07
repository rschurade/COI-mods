using System;
using System.Reflection;
using Mafi;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo;
using Mafi.Core.Buildings.Cargo.Modules;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Buildings.Cargo.Ships.Modules;
using Mafi.Core.Buildings.Storages;
using Mafi.Core.Entities.Static;
using Mafi.Core.Products;
using Mafi.Core.Utils;

namespace ShippingPP.Terminals;

/// <summary>
/// Runs the crane/pump product exchange of a local terminal's module against ANY docked local
/// ship. Faithful reimplementation of <c>CargoDepotModule.UpdateProductExchange</c> (which is
/// hard-wired to the depot's OWN ship and couples depot slot i to ship slot i) with three
/// deliberate changes:
///  - the ship module is matched BY PRODUCT, not by slot index (visiting ships' layouts rarely
///    align), which also closes the vanilla import path's missing product-equality check;
///  - the transfer direction comes from the mod's per-module offer/request flag instead of
///    contracts (export = crane loads the ship from the buffer, import = crane unloads the ship);
///  - no dereference of the depot's own ship (a requesting terminal may not have one).
///
/// The module's own private crane/pipe timers, pending quantities and electricity consumer are
/// driven via reflection, so crane animations, power draw, transfer rates, the module state UI
/// and save/load all behave exactly like vanilla (the pending state is saved with the module).
/// </summary>
internal static class LocalCargoExchange
{
    private static bool s_initialized;
    private static bool s_initFailed;

    private static FieldInfo s_lacksPower;
    private static FieldInfo s_canWorkOnLowPower;
    private static FieldInfo s_electricityConsumer;
    private static FieldInfo s_pipeTimer;
    private static FieldInfo s_craneTimer;
    private static FieldInfo s_pendingToShip;
    private static FieldInfo s_pendingFromShip;
    private static FieldInfo s_pendingToStorage;
    private static FieldInfo s_isPipeDown;
    private static PropertyInfo s_isOperational;
    private static PropertyInfo s_buffer;
    private static MethodInfo s_tryConsume;
    private static Duration s_durationToLowerPipe;

    public static bool TryInitialize()
    {
        if (s_initialized)
        {
            return !s_initFailed;
        }
        s_initialized = true;

        Type module = typeof(CargoDepotModule);
        s_lacksPower = findField(module, "m_lacksPower");
        s_canWorkOnLowPower = findField(module, "m_canWorkOnLowPower");
        s_electricityConsumer = findField(module, "m_electricityConsumer");
        s_pipeTimer = findField(module, "m_pipeMovementTimer");
        s_craneTimer = findField(module, "m_craneAnimationTimer");
        s_pendingToShip = findField(module, "m_pendingQuantityToShip");
        s_pendingFromShip = findField(module, "m_pendingQuantityFromShip");
        s_pendingToStorage = findField(module, "m_pendingQuantityToStorage");
        // IsPipeDown is a plain (public) field in the shipped assembly, not a property.
        s_isPipeDown = findField(module, "IsPipeDown");
        s_isOperational = findProperty(module, "IsOperational");
        s_buffer = findProperty(module, "Buffer");
        s_tryConsume = s_electricityConsumer?.FieldType.GetMethod("TryConsume",
            new[] { typeof(bool) });
        FieldInfo lowerPipe = module.GetField("DURATION_TO_LOWER_PIPE",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        s_durationToLowerPipe = lowerPipe != null
            ? (Duration)lowerPipe.GetValue(null)
            : Duration.FromSec(2);

        s_initFailed = s_lacksPower == null || s_canWorkOnLowPower == null
            || s_electricityConsumer == null || s_pipeTimer == null || s_craneTimer == null
            || s_pendingToShip == null || s_pendingFromShip == null || s_pendingToStorage == null
            || s_isPipeDown == null || s_isOperational == null || s_buffer == null
            || s_tryConsume == null;
        if (s_initFailed)
        {
            Log.Error("Shipping++: CargoDepotModule internals not resolved; "
                + "local cargo exchange disabled.");
        }
        return !s_initFailed;
    }

    /// <summary>
    /// One sim step of the module's exchange with the given docked ship (null when no ship is
    /// docked — pending transfers are then gracefully returned/committed). Mirrors the vanilla
    /// UpdateProductExchange flow step for step.
    /// </summary>
    public static void Update(CargoDepotModule module, CargoShipV2 dockedShip, bool isExport,
        IProductsManager productsManager)
    {
        if (s_initFailed || module.IsDestroyed)
        {
            return;
        }
        s_lacksPower.SetValue(module, false);
        if (module.IsNotEnabled || !(bool)s_isOperational.GetValue(module))
        {
            return;
        }

        var pipeTimer = (TickTimer)s_pipeTimer.GetValue(module);
        var craneTimer = (TickTimer)s_craneTimer.GetValue(module);
        bool canWorkOnLowPower = (bool)s_canWorkOnLowPower.GetValue(module);
        if (pipeTimer.IsNotFinished || craneTimer.IsNotFinished)
        {
            bool hasPower = (bool)s_tryConsume.Invoke(
                s_electricityConsumer.GetValue(module), new object[] { canWorkOnLowPower });
            if (!hasPower && !canWorkOnLowPower)
            {
                s_lacksPower.SetValue(module, true);
                return;
            }
        }

        // Pipe raising/lowering in progress.
        if (pipeTimer.IsNotFinished)
        {
            pipeTimer.DecrementOnly();
            if (!pipeTimer.IsFinished)
            {
                return;
            }
            s_isPipeDown.SetValue(module, !(bool)s_isPipeDown.GetValue(module));
        }

        var pendingToShip = (ProductQuantity)s_pendingToShip.GetValue(module);
        var pendingFromShip = (ProductQuantity)s_pendingFromShip.GetValue(module);

        // Crane animation in progress: hand the pending cargo over at the drop point.
        if (craneTimer.Decrement())
        {
            if (pendingToShip.IsNotEmpty
                && craneTimer.PercentFinished >= module.Prototype.PercentOfAnimationToDropCargoToShip)
            {
                CargoShipModule shipModule = findShipModuleFor(dockedShip, pendingToShip.Product,
                    needsCapacity: true);
                if (shipModule == null)
                {
                    // Ship gone mid-animation: put the cargo back into the module buffer.
                    returnToBuffer(module, pendingToShip, productsManager);
                    s_pendingToShip.SetValue(module, ProductQuantity.None);
                    return;
                }
                Quantity notStored = shipModule.StoreAsMuchAs(pendingToShip);
                Quantity stored = pendingToShip.Quantity - notStored;
                productsManager.ProductDestroyed(module.Prototype, pendingToShip.Product, stored,
                    DestroyReason.Export);
                if (notStored.IsPositive)
                {
                    returnToBuffer(module,
                        pendingToShip.Product.WithQuantity(notStored), productsManager);
                }
                s_pendingToShip.SetValue(module, ProductQuantity.None);
            }
            else if (pendingFromShip.IsNotEmpty && craneTimer.PercentFinished
                >= module.Prototype.PercentOfAnimationToDropCargoToShip.InverseTo100())
            {
                CargoShipModule shipModule = findShipModuleFor(dockedShip, pendingFromShip.Product,
                    needsCapacity: false);
                if (shipModule == null || shipModule.Quantity < pendingFromShip.Quantity)
                {
                    Log.Warning("Shipping++: ship module gone during unloading; cargo dropped.");
                    s_pendingFromShip.SetValue(module, ProductQuantity.None);
                }
                else
                {
                    ((ICargoShipModuleFriend)shipModule).RemoveExactly(pendingFromShip.Quantity);
                    s_pendingToStorage.SetValue(module, pendingFromShip);
                    s_pendingFromShip.SetValue(module, ProductQuantity.None);
                }
            }
            return;
        }

        // Commit the imported batch into the module buffer.
        var pendingToStorage = (ProductQuantity)s_pendingToStorage.GetValue(module);
        if (pendingToStorage.IsNotEmpty)
        {
            var buffer = getBuffer(module);
            Quantity leftover = buffer != null
                ? buffer.StoreAsMuchAs(pendingToStorage.Quantity)
                : pendingToStorage.Quantity;
            productsManager.ProductCreated(module.Prototype, pendingToStorage, CreateReason.Imported);
            if (leftover.IsPositive)
            {
                productsManager.ProductDestroyed(module.Prototype, pendingToStorage.Product,
                    leftover, DestroyReason.General);
            }
            s_pendingToStorage.SetValue(module, ProductQuantity.None);
        }

        if (dockedShip == null || dockedShip.DepartureRequestedByPlayer)
        {
            raisePipeIfDown(module, pipeTimer);
            return;
        }

        // Start a new exchange batch.
        if (!isExport)
        {
            // Import: ship module -> terminal buffer (product must match the module's product).
            ProductQuantity available = getAvailableForImport(module, dockedShip);
            if (available.IsEmpty)
            {
                raisePipeIfDown(module, pipeTimer);
                return;
            }
            available = available.WithNewQuantity(available.Quantity.Min(module.UsableCapacity));
            if (available.IsNotEmpty)
            {
                if (module.Prototype.HasPipeCraneAnimation
                    && !(bool)s_isPipeDown.GetValue(module))
                {
                    pipeTimer.Start(s_durationToLowerPipe);
                    return;
                }
                s_pendingFromShip.SetValue(module, available);
                craneTimer.Start(module.Prototype.DurationPerExchange);
            }
            return;
        }

        // Export: terminal buffer -> ship module.
        ProductQuantity availableExport = getAvailableForExport(module, dockedShip);
        if (availableExport.IsEmpty)
        {
            raisePipeIfDown(module, pipeTimer);
            return;
        }
        if (module.Prototype.HasPipeCraneAnimation && !(bool)s_isPipeDown.GetValue(module))
        {
            pipeTimer.Start(s_durationToLowerPipe);
            return;
        }
        s_pendingToShip.SetValue(module, availableExport);
        getBuffer(module).RemoveExactly(availableExport.Quantity);
        craneTimer.Start(module.Prototype.DurationPerExchange);
    }

    private static ProductQuantity getAvailableForImport(CargoDepotModule module,
        CargoShipV2 ship)
    {
        if (module.StoredProduct.IsNone)
        {
            return ProductQuantity.None;
        }
        CargoShipModule shipModule = findShipModuleFor(ship, module.StoredProduct.Value,
            needsCapacity: false);
        if (shipModule == null || !shipModule.CargoShip.IsDocked || shipModule.Quantity.IsNotPositive)
        {
            return ProductQuantity.None;
        }
        return module.StoredProduct.Value.WithQuantity(
            shipModule.Quantity.Min(module.Prototype.QuantityPerExchange));
    }

    private static ProductQuantity getAvailableForExport(CargoDepotModule module,
        CargoShipV2 ship)
    {
        ProductBuffer buffer = getBuffer(module);
        if (buffer == null || buffer.Quantity.IsNotPositive)
        {
            return ProductQuantity.None;
        }
        CargoShipModule shipModule = findShipModuleFor(ship, buffer.Product, needsCapacity: true);
        if (shipModule == null || !shipModule.CargoShip.IsDocked)
        {
            return ProductQuantity.None;
        }
        return buffer.Product.WithQuantity(shipModule.UsableCapacity.Min(buffer.Quantity)
            .Min(module.Prototype.QuantityPerExchange));
    }

    /// <summary>Ship module holding the given product (with free capacity when loading).</summary>
    private static CargoShipModule findShipModuleFor(CargoShipV2 ship, ProductProto product,
        bool needsCapacity)
    {
        if (ship == null)
        {
            return null;
        }
        for (int i = 0; i < ship.Modules.Count; i++)
        {
            CargoShipModule candidate = ship.Modules[i].ValueOrNull;
            if (candidate == null || candidate.StoredProduct.ValueOrNull != product)
            {
                continue;
            }
            if (needsCapacity ? candidate.UsableCapacity.IsPositive : candidate.Quantity.IsPositive)
            {
                return candidate;
            }
        }
        return null;
    }

    private static void raisePipeIfDown(CargoDepotModule module, TickTimer pipeTimer)
    {
        if (module.Prototype.HasPipeCraneAnimation && (bool)s_isPipeDown.GetValue(module))
        {
            pipeTimer.Start(s_durationToLowerPipe);
        }
    }

    /// <summary>
    /// Stores as much of <paramref name="pq"/> as fits into the terminal's modules that already
    /// hold that product, and returns what did not fit. Note that a module only accepts the one
    /// product the player assigned to it, so materials nothing is set to carry (typically the
    /// construction goods of a ship refund) come straight back as leftover.
    /// </summary>
    internal static Quantity StoreInModules(CargoDepot terminal, ProductQuantity pq)
    {
        if (!TryInitialize() || terminal == null || terminal.IsDestroyed)
        {
            return pq.Quantity;
        }
        Quantity remaining = pq.Quantity;
        for (int i = 0; i < terminal.Modules.Length && remaining.IsPositive; i++)
        {
            CargoDepotModule module = terminal.Modules[i].ValueOrNull;
            if (module == null || module.IsDestroyed
                || module.StoredProduct.ValueOrNull != pq.Product)
            {
                continue;
            }
            ProductBuffer buffer = getBuffer(module);
            if (buffer != null)
            {
                remaining = buffer.StoreAsMuchAs(remaining);
            }
        }
        return remaining;
    }

    private static ProductBuffer getBuffer(CargoDepotModule module)
    {
        // StorageBase.Buffer is Option<LogisticsBuffer> at runtime (LogisticsBuffer derives
        // from ProductBuffer).
        var option = (Option<LogisticsBuffer>)s_buffer.GetValue(module);
        return option.ValueOrNull;
    }

    private static void returnToBuffer(CargoDepotModule module, ProductQuantity pq,
        IProductsManager productsManager)
    {
        ProductBuffer buffer = getBuffer(module);
        Quantity leftover = buffer != null ? buffer.StoreAsMuchAs(pq.Quantity) : pq.Quantity;
        if (leftover.IsPositive)
        {
            productsManager.ProductDestroyed(module.Prototype, pq.Product, leftover,
                DestroyReason.General);
        }
    }

    private static FieldInfo findField(Type type, string name)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                return f;
            }
        }
        return null;
    }

    private static PropertyInfo findProperty(Type type, string name)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            PropertyInfo p = t.GetProperty(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null)
            {
                return p;
            }
        }
        return null;
    }
}
