using System;
using Mafi;
using Mafi.Collections;
using Mafi.Core.Game;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;

namespace ShippingPP;

/// <summary>
/// Shipping++ — local cargo shipping between terminals on the player's island.
///
/// Adds local cargo terminals (reusing the vanilla cargo depot building and modules) whose ships
/// sail between terminals on the same map instead of leaving for the world map: terminals with
/// export modules offer products, terminals with import modules request them, and a dispatcher
/// automatically routes the ships — the same offer/request model the vanilla train network uses.
/// Ships are built at the terminal from delivered construction materials and consume fuel per
/// journey like vanilla cargo ships.
/// </summary>
public sealed class ShippingPPMod : IMod
{
    public ModManifest Manifest { get; }
    public bool IsUiOnly => false;

    [Obsolete("Use JsonConfig instead.")]
    public Option<IConfig> ModConfig { get; set; }
    public ModJsonConfig JsonConfig { get; }

    public ShippingPPMod(ModManifest manifest)
    {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
    }

    public void RegisterPrototypes(ProtoRegistrator registrator)
    {
        // Adds the "Local cargo terminal" — the smallest vanilla cargo depot re-purposed for
        // local shipping.
        registrator.RegisterData<Terminals.LocalTerminalData>();

        // Adds the "Navigation buoy" — a route marker stop for shipping lines.
        registrator.RegisterData<Lines.NavBuoyData>();
    }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb,
        bool gameWasLoaded)
    {
        // The mod's save-persisted core: ship construction at local terminals (and later the
        // shipping dispatcher). Instantiated with the game, serialized with the save.
        try
        {
            depBuilder.RegisterDependency<ShippingManager>().AsSelf();
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to register shipping manager: {ex.Message}");
        }

        // Processes the mod's input commands (build-ship button etc.).
        try
        {
            depBuilder.RegisterDependency<Terminals.ShippingCommandsProcessor>().AsAllInterfaces();
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to register commands processor: {ex.Message}");
        }
    }

    public void EarlyInit(DependencyResolver resolver) { }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded)
    {
        // Registers the procedural buoy model before any entity renders.
        try
        {
            Lines.NavBuoyModel.TryInject(resolver);
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to inject buoy model: {ex.Message}");
        }

        // Keeps local terminals from taking a ship out of the vanilla shipwreck pool (their
        // ships are built on site instead).
        try
        {
            Terminals.TerminalPoolPatch.TryApply();
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply terminal pool patch: {ex.Message}");
        }

        // Gives locally-built ships the mod's dock-to-dock job provider instead of the vanilla
        // world-trade providers.
        try
        {
            Ships.LocalShipProviderPatch.TryApply();
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply local ship provider patch: {ex.Message}");
        }

        // Per-module offer/request direction and free product choice on terminal modules.
        try
        {
            Terminals.ModuleDirectionPatch.TryApply();
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply module direction patch: {ex.Message}");
        }

        // The module window's product dropdown lists all products (not just world-minable) for
        // modules attached to a local terminal; no-op in headless runs.
        try
        {
            Terminals.ModulePickerPatch.TryApply();
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply module picker patch: {ex.Message}");
        }

        // Navigation buoys are placed at sea level instead of sinking to the ocean floor.
        try
        {
            Lines.NavBuoyPlacementPatch.TryApply();
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply buoy placement patch: {ex.Message}");
        }
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }

    public void Dispose() { }
}
