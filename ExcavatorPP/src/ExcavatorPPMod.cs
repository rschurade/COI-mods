using System;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Game;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;

namespace ExcavatorPP;

/// <summary>
/// Excavator++ — excavators keep mining with a partially filled bucket.
///
/// Vanilla forces an excavator to empty its bucket completely before it may dig again. When a
/// scoop only partially fits into the waiting truck, the leftover stays in the bucket and the
/// excavator idles until another truck takes those few units — which leaves that truck partially
/// filled, so the cycle repeats forever. This mod lets the excavator keep digging until the
/// bucket is full and only then unload: every dump is a full bucket, every truck leaves full,
/// and the out-of-sync cycle heals itself. See <see cref="ContinuousMiningPatch"/>.
/// </summary>
public sealed class ExcavatorPPMod : IMod
{
    public ModManifest Manifest { get; }
    public bool IsUiOnly => false;

    [Obsolete("Use JsonConfig instead.")]
    public Option<IConfig> ModConfig { get; set; }
    public ModJsonConfig JsonConfig { get; }

    public ExcavatorPPMod(ModManifest manifest)
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
        // All-or-nothing: on any failure the patch unpatches itself and the game keeps the
        // vanilla empty-bucket-before-mining behavior.
        try
        {
            ContinuousMiningPatch.TryApply();
        }
        catch (Exception ex)
        {
            Log.Error($"Excavator++: failed to apply continuous mining patch: {ex.Message}");
        }
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }

    public void Dispose() { }
}
