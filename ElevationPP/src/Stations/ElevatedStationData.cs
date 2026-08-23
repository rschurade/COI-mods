using System;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Core;
using Mafi.Base;
using Mafi.Collections.ImmutableCollections;
using Mafi.Curves;
using Mafi.Core.Entities.Animations;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Mods;
using Mafi.Core.Products;
using Mafi.Core.Prototypes;
using Mafi.Core.Research;
using Mafi.Core.Trains;
using Mafi.Core.UnlockingTree;
using Mafi.Localization;
using Mafi.Base.Prototypes.Trains;

namespace ElevationPP.Stations;

/// <summary>
/// Registers elevated variants of the train stations. Rather than hard-coding each type, it
/// iterates every existing station proto at runtime and clones it — so electrified, molten and any
/// DLC variants are picked up automatically. Each clone gets an elevated track trajectory, a
/// UsingPillar footprint, a widened placement-height range, the source proto's own icon, and is
/// filed under a new "Elevated stations" sub-tab next to Stations. Normal and electrified variants
/// of the same type are combined under one toolbar slot (with the variants in its popup), just like
/// the vanilla Stations tab.
/// </summary>
internal class ElevatedStationData : IModData
{
    internal static readonly Proto.ID ELEVATED_CATEGORY_ID = new Proto.ID("ElevationPP_ElevatedStations");
    private static readonly LocStr1 ELEVATED_PREFIX =
        Loc.Str1("ElevationPP_ElevatedPrefix", "Elevated {0}", "Name prefix for elevated train station variants.");
    // Highest station deck with a pillar under it: pillar height cap minus the deck tile itself
    // (the pillar's top tile is the station's base tile). Follows TransportPillarMaxHeight.
    private static int PlacementHeightMax => TransportPillarProto.MAX_PILLAR_HEIGHT.Value - 1;

    public void RegisterData(ProtoRegistrator registrator)
    {
        ProtosDb db = registrator.PrototypesDb;

        // 1. New "Elevated stations" sub-tab under the Trains category, next to Stations.
        if (db.TryGetProto<ToolbarCategoryProto>(Ids.ToolbarCategories.Trains, out var trainsCat))
        {
            db.Add(new ToolbarCategoryProto(ELEVATED_CATEGORY_ID,
                Proto.CreateStr(ELEVATED_CATEGORY_ID, "Elevated stations", "Elevated train station modules.", null),
                1.5f, "Assets/Base/Icons/Toolbar/TrainStations.svg", false, "", null, null, trainsCat));
        }
        else
        {
            Log.Error("Elevation++: Trains toolbar category not found; elevated stations have no tab.");
        }

        // Map each vanilla station proto to the research node that unlocks it, so each elevated
        // clone can be gated behind the same research instead of being available from game start.
        var protoToNode = new Dictionary<IProto, ResearchNodeProto>();
        foreach (ResearchNodeProto node in db.All<ResearchNodeProto>())
        {
            foreach (IProto unlocked in ProtoUnlock.GetUnlockedProtos(node.Units.AsEnumerable()))
            {
                protoToNode[unlocked] = node;
            }
        }

        // 2. Clone every cargo module and root. Collect by group so normal/electrified variants of a
        //    type can be combined into one toolbar slot afterwards.
        var moduleGroups = new Dictionary<ProductType, List<Proto>>();
        var rootGroup = new List<Proto>();
        var fuelGroups = new Dictionary<ProductProto, List<Proto>>();
        var isElectrified = new Dictionary<Proto, bool>();

        foreach (TrainStationModuleProto v in new List<TrainStationModuleProto>(db.All<TrainStationModuleProto>()))
        {
            if (v is ElevatedStationModuleProto || v.IsObsolete)
            {
                continue;
            }
            protoToNode.TryGetValue(v, out var node);
            Proto p = registerElevatedModule(registrator, db, v, node);
            if (p == null)
            {
                continue;
            }
            if (!moduleGroups.TryGetValue(v.ProductType, out var list))
            {
                moduleGroups[v.ProductType] = list = new List<Proto>();
            }
            list.Add(p);
            isElectrified[p] = v.ElectrificationType != ElectrificationType.None;
        }

        foreach (TrainStationRootProto v in new List<TrainStationRootProto>(db.All<TrainStationRootProto>()))
        {
            if (v is ElevatedStationRootProto || v.IsObsolete)
            {
                continue;
            }
            protoToNode.TryGetValue(v, out var node);
            Proto p = registerElevatedRoot(registrator, db, v, node);
            if (p == null)
            {
                continue;
            }
            rootGroup.Add(p);
            isElectrified[p] = v.ElectrificationType != ElectrificationType.None;
        }

        foreach (TrainStationFuelProto v in new List<TrainStationFuelProto>(db.All<TrainStationFuelProto>()))
        {
            if (v is ElevatedStationFuelProto || v.IsObsolete)
            {
                continue;
            }
            protoToNode.TryGetValue(v, out var node);
            Proto p = registerElevatedFuel(registrator, db, v, node);
            if (p == null)
            {
                continue;
            }
            ProductProto fuelKey = firstFuelProduct(v);
            if (fuelKey != null)
            {
                if (!fuelGroups.TryGetValue(fuelKey, out var list))
                {
                    fuelGroups[fuelKey] = list = new List<Proto>();
                }
                list.Add(p);
            }
            isElectrified[p] = v.ElectrificationType != ElectrificationType.None;
        }

        // 3. Combine normal + electrified of each type under a single toolbar slot (popup variants).
        foreach (var group in moduleGroups.Values)
        {
            combineUnderOneSlot(group, isElectrified);
        }
        foreach (var group in fuelGroups.Values)
        {
            combineUnderOneSlot(group, isElectrified);
        }
        combineUnderOneSlot(rootGroup, isElectrified);

        Log.Info($"Elevation++: registered elevated stations — {isElectrified.Count} proto(s) in " +
                 $"{moduleGroups.Count + fuelGroups.Count + 1} toolbar group(s).");
    }

    private static void combineUnderOneSlot(List<Proto> group, Dictionary<Proto, bool> isElectrified)
    {
        if (group.Count < 2)
        {
            return;
        }
        // Link the variants into a tier chain so the toolbar combines them under one slot with the
        // others in the popup (same as the vanilla Stations tab). The chain head (the proto that
        // calls SetNextTier) shows as tier I, so put the non-electrified variant first there and the
        // electrified one after — matching the normal menu (I = non-electrified, II = electrified).
        group.Sort((a, b) => isElectrified[b].CompareTo(isElectrified[a]));
        for (int i = 1; i < group.Count; i++)
        {
            ((IProtoWithUpgrade)group[i]).SetNextTier((IProtoWithUpgrade)group[i - 1]);
        }
    }

    private Proto registerElevatedModule(ProtoRegistrator registrator, ProtosDb db, TrainStationModuleProto v,
        ResearchNodeProto unlockedBy)
    {
        var id = new StaticEntityProto.ID("ElevationPP_Elev_" + v.Id);
        Proto.Str strings = Proto.CreateStrFromLocalized(id, ELEVATED_PREFIX.Format(v.Strings.Name), v.Strings.DescShort);

        // Reuse the module's OWN footprint and width, not a fixed 5-wide guess — otherwise wider
        // modules (e.g. the 10-wide molten station) get under-covered, clip neighbours, and report
        // the wrong width so they don't join a station group. The elevated track spans that width.
        int w = v.Layout.LayoutSize.X;
        TrainTrackTrajectoryData trajectory = makeElevatedTrajectory(registrator,
            new Vector2f(0, 0), new Vector2f(w / 3, 0), new Vector2f(2 * w / 3, 0), new Vector2f(w, 0),
            trackOffsetFor(v), v.ElectrificationType != ElectrificationType.None);
        if (trajectory == null)
        {
            return null;
        }
        EntityLayout layout = parseHollowFootprint(registrator, footprintRows(v.Layout, () => moduleFootprint(v)));
        // Mirror the module's order from the normal Stations tab; keep molten last as requested
        // (its DLC toolbar order otherwise lands it near the front).
        int order = v.Id.ToString().Contains("Molten") ? 200 : vanillaOrder(v.Graphics.Categories, 115);
        var categoryArray = registrator.GetCategoryToArray(ELEVATED_CATEGORY_ID, false, order);
        var gfx = (TrainStationModuleProto.Gfx)cloneGfxWithCategory(v.Graphics, vanillaIconPath(v), categoryArray);
        recordAnimationPath(id, v);
        bool electrified = v.ElectrificationType != ElectrificationType.None;

        var proto = new ElevatedStationModuleProto(
            id, strings, layout, v.Costs, trajectory, v.ProductType, v.Capacity, v.TransferPeriod,
            v.TransferQuantity, v.ConnectionCompletionPerStepWhenLoading, v.PowerConsumption,
            ((IProtoWithAnimation)v).AnimationParams, gfx, null, null, null, cannotBeReflected: false, null, electrified);
        return addGated(db, proto, unlockedBy);
    }

    private Proto registerElevatedRoot(ProtoRegistrator registrator, ProtosDb db, TrainStationRootProto v,
        ResearchNodeProto unlockedBy)
    {
        var id = new StaticEntityProto.ID("ElevationPP_Elev_" + v.Id);
        Proto.Str strings = Proto.CreateStrFromLocalized(id, ELEVATED_PREFIX.Format(v.Strings.Name), v.Strings.DescShort);
        bool electrified = v.ElectrificationType != ElectrificationType.None;
        // Build per-variant so the electrified root gets a catenary-carrying (poled) trajectory.
        TrainTrackTrajectoryData trajectory = makeElevatedTrajectory(registrator,
            new Vector2f(0, 0), new Vector2f(1, 0), new Vector2f(1, 0), new Vector2f(2, 0),
            new RelTile2f(0, -2), electrified);
        if (trajectory == null)
        {
            return null;
        }
        EntityLayout layout = parseHollowFootprint(registrator,
            "[5][5]", "[5][5]", "[5][5]", "[5][5]", "[5][5]", "[5][5]", "[5][5]");
        // Root is the base building; keep it leftmost (lower order than any module), like the
        // normal Stations tab where "Train station" comes first.
        var categoryArray = registrator.GetCategoryToArray(ELEVATED_CATEGORY_ID, false, 100);
        var gfx = (EntityWithTrainTrackBaseProto.Gfx)cloneGfxWithCategory(v.Graphics, vanillaIconPath(v), categoryArray);
        recordAnimationPath(id, v);

        var proto = new ElevatedStationRootProto(
            id, strings, layout, v.Costs, trajectory, v.PowerConsumption, gfx,
            null, null, null, cannotBeReflected: false, electrified);
        return addGated(db, proto, unlockedBy);
    }

    private Proto registerElevatedFuel(ProtoRegistrator registrator, ProtosDb db, TrainStationFuelProto v,
        ResearchNodeProto unlockedBy)
    {
        var id = new StaticEntityProto.ID("ElevationPP_Elev_" + v.Id);
        Proto.Str strings = Proto.CreateStrFromLocalized(id, ELEVATED_PREFIX.Format(v.Strings.Name), v.Strings.DescShort);

        int w = v.Layout.LayoutSize.X;
        TrainTrackTrajectoryData trajectory = makeElevatedTrajectory(registrator,
            new Vector2f(0, 0), new Vector2f(w / 3, 0), new Vector2f(2 * w / 3, 0), new Vector2f(w, 0),
            trackOffsetFor(v), v.ElectrificationType != ElectrificationType.None);
        if (trajectory == null)
        {
            return null;
        }
        EntityLayout layout = parseHollowFootprint(registrator, footprintRows(v.Layout, () => fillGrid(w, 7)));
        var categoryArray = registrator.GetCategoryToArray(ELEVATED_CATEGORY_ID, false, vanillaOrder(v.Graphics.Categories, 120));
        var gfx = (TrainStationFuelProto.Gfx)cloneGfxWithCategory(v.Graphics, vanillaIconPath(v), categoryArray);
        recordAnimationPath(id, v);
        bool electrified = v.ElectrificationType != ElectrificationType.None;

        var proto = new ElevatedStationFuelProto(
            id, strings, layout, v.Costs, trajectory, v.TransferPeriod, v.PowerConsumption, v.AnimationParams,
            gfx, null, null, null, cannotBeReflected: false, electrified, v.RequiresAlignment);

        // A fuel station with no allowed fuels fails to initialize — copy them from the source proto.
        foreach (TrainStationFuelProto.FuelDefinition def in v.AllowedFuels)
        {
            proto.AddFuel(def.PrimaryProduct, def.SecondaryProduct, def.WasteProduct);
        }
        return addGated(db, proto, unlockedBy);
    }

    /// <summary>The primary fuel product of a fuel station, used to group its normal/electrified variants.</summary>
    private static ProductProto firstFuelProduct(TrainStationFuelProto v)
    {
        foreach (TrainStationFuelProto.FuelDefinition def in v.AllowedFuels)
        {
            return def.PrimaryProduct.Product;
        }
        return null;
    }

    /// <summary>A plain width x rows grid of [5] tiles (fallback footprint when no source string).</summary>
    private static string[] fillGrid(int width, int rows)
    {
        var row = new System.Text.StringBuilder();
        for (int i = 0; i < width; i++)
        {
            row.Append("[5]");
        }
        var result = new string[rows];
        for (int i = 0; i < rows; i++)
        {
            result[i] = row.ToString();
        }
        return result;
    }

    /// <summary>
    /// Adds the proto. If a research node unlocks the source station, lock the elevated clone on
    /// init and unlock it with that same node (hidden in the research UI to avoid duplicate icons).
    /// If no node is found, leave it unlocked so it never becomes permanently unbuildable.
    /// </summary>
    internal static Proto addGated(ProtosDb db, Proto proto, ResearchNodeProto unlockedBy)
    {
        Proto added = db.Add(proto, lockOnInit: unlockedBy != null);
        if (unlockedBy != null)
        {
            unlockedBy.AddProtoToUnlock((IProtoWithIcon)added, hideInUi: true);
            Log.Info($"Elevation++: '{proto.Id}' unlocks with research '{unlockedBy.Id}'.");
        }
        else
        {
            Log.Warning($"Elevation++: '{proto.Id}' has no unlocking research; left always-available.");
        }
        return added;
    }

    /// <summary>
    /// The exact building-footprint rows of a layout, taken from its original source string (rows
    /// are stored joined by '\n'). This preserves the real size, shape and port positions of any
    /// module. Falls back to <paramref name="fallback"/> if the source string isn't available.
    /// </summary>
    private static string[] footprintRows(EntityLayout layout, Func<string[]> fallback)
    {
        string src = layout.SourceLayoutStr;
        return string.IsNullOrEmpty(src) ? fallback() : src.Split('\n');
    }

    /// <summary>
    /// Fallback footprint for a cargo module: a 5x7 grid of [5] tiles, with the two cargo ports in
    /// the first row using the proto's own port symbol (# conveyor, ~ loose, @ pipe, molten, …).
    /// Modules with no ports (the empty module) use the vanilla empty footprint.
    /// </summary>
    private static string[] moduleFootprint(TrainStationModuleProto v)
    {
        if (v.Ports.IsNotEmpty)
        {
            char c = v.Ports[0].Shape.LayoutChar;
            return new[]
            {
                "   +A" + c + "   +B" + c + "   ",
                "[5][5][5][5][5]", "[5][5][5][5][5]", "[5][5][5][5][5]", "[5][5][5][5][5]",
                "[5][5][5][5][5]", "[5][5][5][5][5]", "[5][5][5][5][5]",
            };
        }
        return new[]
        {
            "[2][2][2][2][2]", "[2][2][2][2][2]", "[2][2][2][2][2]", "[2][2][2][2][2]", "[2][2][2][2][2]",
            "[4][4][4][4][4]", "[4][4][4][4][4]",
        };
    }

    private TrainTrackTrajectoryData makeElevatedTrajectory(ProtoRegistrator registrator,
        Vector2f p0, Vector2f p1, Vector2f p2, Vector2f p3, RelTile2f trackOffset, bool electrified)
    {
        var curve = new CubicBezierCurve2f(ImmutableArray.Create(p0, p1, p2, p3));
        // The `electrified` flag bakes the catenary/pole support data into the trajectory; without it
        // an electrified clone would swap to the electrified model but never grow catenary poles.
        if (!TrainTrackTrajectoryData.TryCreateFromCurve(0, curve, isElevated: true,
                registrator.LayoutParser.SomeOption(), out var traj, out _, out var error,
                default(ThicknessTilesF), default(ThicknessTilesF), TrainTrackGradeFactor.G0,
                TrainTrackGradeFactor.G0, null, null, trackOffset, default(RelTile2f), null, electrified))
        {
            Log.Error("Elevation++: failed to create elevated station trajectory: " + error);
            return null;
        }
        return traj;
    }

    /// <summary>
    /// The track offset (how far the track sits in front of the building). Almost every station uses
    /// -2; the nuclear fuel station is the lone exception at -5 (it places the track further out).
    /// </summary>
    private static RelTile2f trackOffsetFor(StaticEntityProto v)
    {
        int y = v.Id.ToString().Contains("Nuclear") ? -5 : -2;
        return new RelTile2f(0, y);
    }

    /// <summary>
    /// The source proto's generated icon path, reconstructed explicitly. We can't read
    /// <c>v.Graphics.IconPath</c> here because the vanilla protos aren't initialized yet at mod
    /// registration time (the path is set later), so it's still null.
    /// </summary>
    internal static string vanillaIconPath(StaticEntityProto v)
    {
        return Proto.Gfx.GetGeneratedIconPathRoot(v) + "/LayoutEntity/" + v.Id + ".png";
    }

    /// <summary>The order of the first toolbar entry, or <paramref name="fallback"/>.</summary>
    private static int vanillaOrder(ImmutableArray<ToolbarEntryData> cats, int fallback)
    {
        if (cats.IsNotEmpty && cats[0].Order.HasValue)
        {
            return cats[0].Order.Value;
        }
        return fallback;
    }

    /// <summary>
    /// Shallow-clones a proto Gfx, retargets its toolbar category to the new tab, points it at the
    /// source proto's icon, and marks the icon custom so the proto's Initialize won't overwrite it
    /// with a (missing) generated path for the new id. The vanilla proto is untouched (separate Gfx).
    /// </summary>
    internal static object cloneGfxWithCategory(object vanillaGfx, string iconPath, ImmutableArray<ToolbarEntryData> categoryArray)
    {
        object clone = typeof(object)
            .GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)
            .Invoke(vanillaGfx, null);
        Type t = clone.GetType();
        setField(t, clone, "<Categories>k__BackingField", categoryArray);
        setField(t, clone, "<IconPath>k__BackingField", iconPath);
        if (!setField(t, clone, "IconIsCustom", true))
        {
            Log.Warning("Elevation++: Gfx IconIsCustom field not found; elevated station icons may be missing.");
        }
        return clone;
    }

    /// <summary>
    /// Repairs <c>Graphics.AnimationDataAssetPathBase</c> of an elevated station proto.
    ///
    /// The path is recomputed in <c>Gfx.Initialize</c> from the asset root of the mod OWNING the
    /// proto — and for protos of a regular (non-core, non-DLC) mod that root is the placeholder
    /// "TODO". The baked semi-instanced animation metadata (e.g.
    /// <c>Assets/Unity/Generated/Animations/TrainStationFluid_LOD0.txt</c>, which drives the fluid
    /// module's extending loading pipe) is then looked up at <c>TODO/…</c>, never found, and those
    /// animated parts stay frozen in their rest pose (the game log shows "Animation data not
    /// found"). Parts driven by real Animator components (like the pump) are unaffected, which is
    /// why only PART of the loading animation played.
    ///
    /// The correct path can only be built from the SOURCE proto (its mod is the core game or a
    /// DLC), so it is recorded here at registration keyed by the clone's id, and the proto classes
    /// re-apply it in <c>OnInitialize</c> right after the base call ran <c>Gfx.Initialize</c>.
    /// </summary>
    internal static void FixAnimationDataPath(LayoutEntityProto proto)
    {
        if (s_animationPathOverrides.TryGetValue(proto.Id.ToString(), out string path))
        {
            setField(proto.Graphics.GetType(), proto.Graphics,
                "<AnimationDataAssetPathBase>k__BackingField", path);
        }
    }

    private static readonly Dictionary<string, string> s_animationPathOverrides
        = new Dictionary<string, string>();

    /// <summary>
    /// Records the animation-data base path the elevated clone <paramref name="id"/> must use: the
    /// source proto's asset root plus its animation name (which honours the proto's private
    /// animation-swap override, e.g. the electrified fluid module reusing "TrainStationFluid").
    /// </summary>
    private static void recordAnimationPath(StaticEntityProto.ID id, StaticEntityProto v)
    {
        string swap = getField(v.Graphics.GetType(), v.Graphics,
            "m_instancedRenderingAnimationProtoSwap") as string;
        s_animationPathOverrides[id.ToString()] =
            Proto.Gfx.GetGeneratedAnimationsPathRoot(v) + "/" + (swap ?? v.Id.ToString());
    }

    private static object getField(Type type, object target, string name)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                return f.GetValue(target);
            }
        }
        return null;
    }

    private static bool setField(Type type, object target, string name, object value)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                f.SetValue(target, value);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parses a station footprint and renders the auto-built support pillars as a hollow ring around
    /// the OUTSIDE edge only, instead of a solid block under the whole building.
    ///
    /// Each footprint tile would normally be ground-anchored (which triggers "terrain too low" when
    /// raised). A tile on the boundary — one with at least one orthogonal neighbour that is not part
    /// of the footprint — is given <see cref="LayoutTileConstraint.UsingPillar"/> so it gets a
    /// transport pillar; an interior tile is given <see cref="LayoutTileConstraint.None"/>, which the
    /// elevated-placement validator skips entirely (no pillar, no terrain anchor), so the deck simply
    /// floats on the ring of edge pillars. Ports keep their original spec.
    ///
    /// A first pass collects the footprint tiles so the second pass can tell edge from interior. This
    /// boundary rule works for any shape (thin footprints stay fully pillared; ports carved out of the
    /// footprint turn their neighbours into edges), not just solid rectangles.
    /// </summary>
    private static EntityLayout parseHollowFootprint(ProtoRegistrator registrator, params string[] rows)
    {
        var footprint = new HashSet<(int, int)>();
        registrator.LayoutParser.ParseLayoutOrThrow(
            new EntityLayoutParams(customPlacementRange: new ThicknessIRange(0, PlacementHeightMax),
                tokenPostProcesssor: (RelTile2i coord, LayoutTokenSpec spec) =>
                {
                    if (!spec.IsPort)
                    {
                        footprint.Add((coord.X, coord.Y));
                    }
                    return spec;
                }),
            rows);

        return registrator.LayoutParser.ParseLayoutOrThrow(
            new EntityLayoutParams(customPlacementRange: new ThicknessIRange(0, PlacementHeightMax),
                tokenPostProcesssor: (RelTile2i coord, LayoutTokenSpec spec) =>
                {
                    if (spec.IsPort)
                    {
                        return spec;
                    }
                    bool edge = !footprint.Contains((coord.X + 1, coord.Y))
                        || !footprint.Contains((coord.X - 1, coord.Y))
                        || !footprint.Contains((coord.X, coord.Y + 1))
                        || !footprint.Contains((coord.X, coord.Y - 1));
                    return new LayoutTokenSpec(spec.HeightFrom.Value, spec.HeightToExcl.Value,
                        edge ? LayoutTileConstraint.UsingPillar : LayoutTileConstraint.None);
                }),
            rows);
    }
}
