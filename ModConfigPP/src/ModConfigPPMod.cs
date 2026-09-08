using System;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Game;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;

namespace ModConfigPP;

/// <summary>
/// ModConfig++ — change any mod's settings from inside a running game.
///
/// The base game only lets you edit a mod's settings while the mod is not yet part of the
/// selected save (new game, or the moment a mod is added to a save); afterwards the settings
/// button is disabled and no UI anywhere can change the values. This mod adds a "Mod settings"
/// entry to the in-game escape menu that edits the LIVE configs of every installed mod — the
/// values the running game actually uses, stored in the save. See <see cref="InGameModSettings"/>.
/// </summary>
public sealed class ModConfigPPMod : IMod
{
    public ModManifest Manifest { get; }
    public bool IsUiOnly => false;

    [Obsolete("Use JsonConfig instead.")]
    public Option<IConfig> ModConfig { get; set; }
    public ModJsonConfig JsonConfig { get; }

    public ModConfigPPMod(ModManifest manifest)
    {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
    }

    public void RegisterPrototypes(ProtoRegistrator registrator) { }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb,
        bool gameWasLoaded) { }

    public void EarlyInit(DependencyResolver resolver) { }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded)
    {
        // No-op in headless runs where Mafi.Unity is not loaded.
        try
        {
            InGameModSettings.Initialize(resolver);
        }
        catch (Exception ex)
        {
            Log.Error($"ModConfig++: failed to init the in-game mod settings: {ex.Message}");
        }
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }

    public void Dispose()
    {
        InGameModSettings.Clear();
    }
}
