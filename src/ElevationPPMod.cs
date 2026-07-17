using System;
using System.Reflection;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Game;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Core.Trains;

namespace ElevationPP;

/// <summary>
/// Raises the limits of elevated structures (train tracks, pipes and belts) by patching the
/// height and pillar-spacing knobs the game reads at build time. Four values, all configurable
/// via config.json and editable live in the in-game mod settings:
///
///   Rails:
///     - <see cref="TrainTrackPillarProto.MAX_PILLAR_HEIGHT"/> (static, vanilla 6) — pillar
///       height cap; also the rail build-cursor up-arrow clamp.
///     - <see cref="TrainTrackConstants.PILLAR_SUPPORT_DISTANCE"/> (static, vanilla 7) — max
///       distance any elevated track block may sit from a supporting pillar.
///
///   Pipes / belts (transports):
///     - <see cref="TransportPillarProto.MAX_PILLAR_HEIGHT"/> (static, vanilla 6) — pillar
///       height cap; also the transport build-cursor up-arrow clamp (which lands at
///       MAX_PILLAR_HEIGHT - 1).
///     - <see cref="TransportProto.MaxPillarSupportRadius"/> (per-proto, vanilla 4) — max
///       distance any transport tile may sit from a pivot/pillar. Auto-built pillars end up
///       roughly 2x this value apart. Patched on every transport proto that uses pillars
///       (NeedsPillars); protos that need no pillars are left untouched.
///
/// All four are read at call time by the build controllers, validators and path-finders, so
/// patching from RegisterDependencies (after config.json is attached, before any game state is
/// created or loaded) takes effect for new and loaded games alike. Rail and transport pillars
/// have separate static MAX_PILLAR_HEIGHT fields, so the two height knobs are independent.
///
/// Transport pillars are rendered tile-by-tile (one layer per height unit), so they extend
/// cleanly to any height with no rendering work needed. Rail pillars draw a single fixed-length
/// tower mesh and need the separate stacking fix in <see cref="RailPillarRenderPatch"/>.
/// </summary>
public sealed class ElevationPPMod : IMod
{
    public ModManifest Manifest { get; }
    public bool IsUiOnly => false;

    [Obsolete("Use JsonConfig instead.")]
    public Option<IConfig> ModConfig { get; set; }
    public ModJsonConfig JsonConfig { get; }

    // Vanilla values in 0.8.5: rail height 6 / rail support 7; transport height 6 / transport
    // support radius 4 (pillars 2x that = 8 tiles apart).
    private const string CFG_RAIL_HEIGHT = "RailPillarMaxHeight";
    private const string CFG_RAIL_SUPPORT = "RailPillarSupportDistance";
    private const string CFG_TRANSPORT_HEIGHT = "TransportPillarMaxHeight";
    private const string CFG_TRANSPORT_SUPPORT = "TransportPillarSupportDistance";
    private const string CFG_TRANSPORT_SEGMENT = "TransportPlacementMaxLength";
    private const int DEFAULT_RAIL_HEIGHT = 16;
    private const int DEFAULT_RAIL_SUPPORT = 14;
    private const int DEFAULT_TRANSPORT_HEIGHT = 16;
    private const int DEFAULT_TRANSPORT_SUPPORT = 8;
    private const int DEFAULT_TRANSPORT_SEGMENT = 128;

    // Cached so the patches can re-run when the player edits values in the settings UI.
    private ProtosDb m_protosDb;

    public ElevationPPMod(ModManifest manifest)
    {
        Manifest = manifest;
        JsonConfig = new ModJsonConfig(this);
        // ModJsonConfig.ReplaceWith (used when config.json is attached) preserves subscribers,
        // so subscribing in the constructor is safe.
        JsonConfig.OnValueChanged += onConfigValueChanged;
    }

    public void RegisterPrototypes(ProtoRegistrator registrator)
    {
        // Adds the "Elevated Unit Station" — a cargo station buildable on elevated track.
        registrator.RegisterData<Stations.ElevatedStationData>();
    }

    public void RegisterDependencies(DependencyResolverBuilder depBuilder, ProtosDb protosDb, bool gameWasLoaded)
    {
        m_protosDb = protosDb;

        // Replace the transport (belt/pipe) path-finder with a multi-leg router so one placement
        // drag can reach beyond the vanilla ~31-tile window (TransportPlacementMaxLength).
        try
        {
            depBuilder.RegisterDependency<LongTransportPathFinder>().AsAllInterfaces();
            depBuilder.SetPreferredImplementationFor(typeof(LongTransportPathFinder),
                typeof(Mafi.Core.Factory.Transports.ITransportPathFinder));
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: Failed to register long transport path-finder: {ex.Message}");
        }

        applyPatches();
    }

    public void EarlyInit(DependencyResolver resolver) { }

    public void Initialize(DependencyResolver resolver, bool gameWasLoaded)
    {
        // Rendering-side fix for rail pillars taller than the vanilla tower mesh; no-op in
        // headless runs where Mafi.Unity is not loaded. Transport pillars need no such fix.
        try
        {
            RailPillarRenderPatch.TryApply();
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: Failed to apply pillar render patch: {ex.Message}");
        }

        // Lets a placed elevated station be electrified in-place (the vanilla "Electrify track"
        // tool otherwise reports "Collision with pillar" against the station's own support pillars).
        try
        {
            Stations.ElevatedStationCollisionPatch.TryApply();
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: Failed to apply elevated station electrification patch: {ex.Message}");
        }

        // Lets an elevated belt/pipe be held by the supported connectors at its ends, so the pillar
        // in a short segment between two connectors is no longer mandatory and can be removed.
        try
        {
            ConnectorPillarSupportPatch.TryApply();
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: Failed to apply connector-supported transport patch: {ex.Message}");
        }
    }

    public void MigrateJsonConfig(VersionSlim savedVersion, Dict<string, object> savedValues) { }

    private void onConfigValueChanged(string paramName)
    {
        applyPatches();
    }

    private void applyPatches()
    {
        patchStaticPillarHeight(typeof(TrainTrackPillarProto),
            JsonConfig.GetInt(CFG_RAIL_HEIGHT, DEFAULT_RAIL_HEIGHT), "rail");
        patchRailSupportDistance(JsonConfig.GetInt(CFG_RAIL_SUPPORT, DEFAULT_RAIL_SUPPORT));
        patchStaticPillarHeight(typeof(TransportPillarProto),
            JsonConfig.GetInt(CFG_TRANSPORT_HEIGHT, DEFAULT_TRANSPORT_HEIGHT), "transport");
        patchTransportSupportRadius(JsonConfig.GetInt(CFG_TRANSPORT_SUPPORT, DEFAULT_TRANSPORT_SUPPORT));
        LongTransportPathFinder.MaxSegmentLength =
            JsonConfig.GetInt(CFG_TRANSPORT_SEGMENT, DEFAULT_TRANSPORT_SEGMENT);
        Log.Info($"Elevation++: transport placement max length set to {LongTransportPathFinder.MaxSegmentLength}.");
    }

    /// <summary>
    /// Sets the public static <c>MAX_PILLAR_HEIGHT</c> (a <see cref="ThicknessTilesI"/>) on the
    /// given pillar-proto type. Used for both TrainTrackPillarProto and TransportPillarProto.
    /// </summary>
    private static void patchStaticPillarHeight(Type protoType, int newValue, string label)
    {
        FieldInfo field = protoType.GetField("MAX_PILLAR_HEIGHT",
            BindingFlags.Public | BindingFlags.Static);

        if (field == null)
        {
            Log.Error($"Elevation++: {protoType.Name}.MAX_PILLAR_HEIGHT field not found!");
            return;
        }

        try
        {
            var oldValue = (ThicknessTilesI)field.GetValue(null);
            field.SetValue(null, new ThicknessTilesI(newValue));
            Log.Info($"Elevation++: {label} MAX_PILLAR_HEIGHT {oldValue.Value} -> {newValue}.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: Failed to set {protoType.Name}.MAX_PILLAR_HEIGHT: {ex.Message}");
        }
    }

    private static void patchRailSupportDistance(int newValue)
    {
        FieldInfo field = typeof(TrainTrackConstants).GetField("PILLAR_SUPPORT_DISTANCE",
            BindingFlags.Public | BindingFlags.Static);

        if (field == null)
        {
            Log.Error("Elevation++: TrainTrackConstants.PILLAR_SUPPORT_DISTANCE field not found!");
            return;
        }

        try
        {
            var oldValue = (RelTile1f)field.GetValue(null);
            field.SetValue(null, newValue.Tiles().RelTile1f);
            Log.Info($"Elevation++: rail PILLAR_SUPPORT_DISTANCE {oldValue.Value} -> {newValue}.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: Failed to set PILLAR_SUPPORT_DISTANCE: {ex.Message}");
        }
    }

    /// <summary>
    /// Sets <see cref="TransportProto.MaxPillarSupportRadius"/> (a per-proto readonly
    /// <see cref="RelTile1i"/>) on every transport that uses pillars. The path-finder casts this
    /// radius to a byte and enforces a max unsupported span of 2x the radius, so the value must
    /// stay well under 128; config.json caps it at 32.
    /// </summary>
    private void patchTransportSupportRadius(int radius)
    {
        if (m_protosDb == null)
        {
            return;
        }

        FieldInfo field = typeof(TransportProto).GetField("MaxPillarSupportRadius",
            BindingFlags.Public | BindingFlags.Instance);

        if (field == null)
        {
            Log.Error("Elevation++: TransportProto.MaxPillarSupportRadius field not found!");
            return;
        }

        var newRadius = new RelTile1i(radius);
        int count = 0;
        foreach (TransportProto proto in m_protosDb.All<TransportProto>())
        {
            // Leave transports that don't use pillars alone — NeedsPillars is
            // MaxPillarSupportRadius != MaxValue, so writing a finite value here would
            // suddenly force pillars onto a transport that was designed without them.
            if (!proto.NeedsPillars)
            {
                continue;
            }

            try
            {
                field.SetValue(proto, newRadius);
                count++;
            }
            catch (Exception ex)
            {
                Log.Warning($"Elevation++: Failed to set MaxPillarSupportRadius on '{proto}': {ex.Message}");
            }
        }

        Log.Info($"Elevation++: set transport MaxPillarSupportRadius to {radius} on {count} proto(s).");
    }

    public void Dispose()
    {
        JsonConfig.OnValueChanged -= onConfigValueChanged;
    }
}
