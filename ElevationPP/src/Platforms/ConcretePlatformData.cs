using System.Collections.Generic;
using Mafi;
using Mafi.Base;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities.Static;
using Mafi.Core.Entities.Static.Layout;
using Mafi.Core.Factory.Transports;
using Mafi.Core.Mods;
using Mafi.Core.Prototypes;
using Mafi.Core.Research;
using ElevationPP.Stations;

namespace ElevationPP.Platforms;

/// <summary>
/// Registers the concrete platforms: elevatable slabs (raised with the placement height keys, on
/// auto-built transport pillars like the mod's elevated stations) that ordinary buildings can be
/// built on. Sizes 1x1 to 5x5 tiles, each its own toolbar entry in the mod's own "Platforms" tab
/// (right after the vanilla Bridges tab), unlocked with the vanilla vehicle ramps research (the
/// game's own "concrete structures at height" tier).
///
/// Footprint: every tile is a one-tile-thick deck token without terrain constraints (the deck
/// floats), except the pillar tiles — the corners (for 1x1 the single tile, for 2x2 all four),
/// and for bigger sizes every fourth tile along the edges — which are
/// <see cref="LayoutTileConstraint.UsingPillar"/> so the vanilla elevation validator builds
/// transport pillars under them (and the terrain patch keeps the platform from being lowered into
/// the ground).
/// </summary>
internal class ConcretePlatformData : IModData
{
    /// <summary>Platform sizes (square, in tiles) to register.</summary>
    public static readonly int[] SIZES = { 1, 2, 3, 4, 5 };

    /// <summary>The platforms' own top-level toolbar tab, right after the vanilla Bridges tab.</summary>
    internal static readonly Proto.ID TOOLBAR_CATEGORY_ID = new Proto.ID("ElevationPP_Platforms");
    private const float TOOLBAR_CATEGORY_ORDER = 141f;

    /// <summary>Highest deck height with a support pillar under it: a transport pillar can be at
    /// most MAX_PILLAR_HEIGHT tall and its top tile IS the deck tile, so the deck can sit at most
    /// MAX_PILLAR_HEIGHT - 1 above the ground (the mod's TransportPillarMaxHeight, 16 by default →
    /// 15). Read at registration; the mod's config patch keeps it in sync when it changes.</summary>
    private static int PlacementHeightMax => TransportPillarProto.MAX_PILLAR_HEIGHT.Value - 1;
    private const int PILLAR_SPACING = 4;

    public static StaticEntityProto.ID ProtoId(int size)
        => new StaticEntityProto.ID($"ElevationPP_ConcretePlatform{size}");

    public void RegisterData(ProtoRegistrator registrator)
    {
        ProtosDb db = registrator.PrototypesDb;

        // Own toolbar tab (the tab icon is a small glyph injected by ConcretePlatformModel).
        db.Add(new ToolbarCategoryProto(TOOLBAR_CATEGORY_ID,
            Proto.CreateStr(TOOLBAR_CATEGORY_ID, "Platforms",
                "Concrete platforms other buildings can be built on.", null),
            TOOLBAR_CATEGORY_ORDER, ConcretePlatformModel.TAB_ICON_PATH));

        ResearchNodeProto unlockedBy = db.Get<ResearchNodeProto>(Ids.Research.VehicleRamps).ValueOrNull;
        if (unlockedBy == null)
        {
            Log.Warning("Elevation++: vehicle ramps research not found; concrete platforms are "
                + "available from the start.");
        }

        foreach (int size in SIZES)
        {
            registerPlatform(registrator, db, size, unlockedBy);
        }
    }

    private static void registerPlatform(ProtoRegistrator registrator, ProtosDb db, int size,
        ResearchNodeProto unlockedBy)
    {
        StaticEntityProto.ID id = ProtoId(size);
        Proto.Str strings = Proto.CreateStr(id, $"Concrete platform {size}x{size}",
            "A reinforced concrete slab on support pillars. Raise it with the placement height "
            + "keys like an elevated station; ordinary buildings can then be built on its deck "
            + "(the cursor snaps onto the platform). Trucks serve those buildings from the ground "
            + "beside or below the platform, so keep the platform high enough for them to drive "
            + "underneath. A platform cannot be removed while something stands on it.",
            null);

        EntityLayout layout = parsePlatformLayout(registrator, size);

        // Costs scale with the deck area (per-tile share of the vanilla flat vehicle ramp span).
        int tiles = size * size;
        EntityCosts costs = ((EntityCostsTpl)Costs.Build
                .Concrete((tiles * 2 + 4) / 5)
                .CP2((tiles + 4) / 5))
            .MapToEntityCosts(registrator);

        ImmutableArray<ToolbarEntryData> categories = registrator.GetCategoryToArray(
            TOOLBAR_CATEGORY_ID, false, size);
        var gfx = new LayoutEntityProto.Gfx(ConcretePlatformModel.PrefabPath(size),
            default(RelTile3f), ConcretePlatformModel.IconPath(size).SomeOption(),
            default(ColorRgba), hideBlockedPortsIcon: false, null, categories,
            useInstancedRendering: false);

        var proto = new ConcretePlatformProto(id, strings, layout, costs, gfx, size);
        ElevatedStationData.addGated(db, proto, unlockedBy);
        Log.Info($"Elevation++: registered '{id}'.");
    }

    /// <summary>
    /// The platform footprint: a size x size grid of one-tile-thick deck tokens. Pillar tiles get
    /// <see cref="LayoutTileConstraint.UsingPillar"/> (transport pillar down to the ground); the
    /// rest get no constraint, so the elevation validator skips them (no pillar, no terrain
    /// anchor) and the deck floats between the pillars. The parsed default token's terrain data
    /// (surface height, concrete floor) is deliberately dropped: the platform never shapes terrain.
    /// </summary>
    private static EntityLayout parsePlatformLayout(ProtoRegistrator registrator, int size)
    {
        var row = new System.Text.StringBuilder();
        for (int x = 0; x < size; x++)
        {
            row.Append("[1]");
        }
        var rows = new string[size];
        for (int y = 0; y < size; y++)
        {
            rows[y] = row.ToString();
        }

        return registrator.LayoutParser.ParseLayoutOrThrow(
            new EntityLayoutParams(customPlacementRange: new ThicknessIRange(0, PlacementHeightMax),
                tokenPostProcesssor: (RelTile2i coord, LayoutTokenSpec spec) =>
                {
                    if (spec.IsPort)
                    {
                        return spec;
                    }
                    bool pillar = isPillarLine(coord.X, size) && isPillarLine(coord.Y, size);
                    return new LayoutTokenSpec(spec.HeightFrom.Value, spec.HeightToExcl.Value,
                        pillar ? LayoutTileConstraint.UsingPillar : LayoutTileConstraint.None);
                }),
            rows);
    }

    /// <summary>Pillar rows/columns: both edges plus every PILLAR_SPACING-th line in between.</summary>
    private static bool isPillarLine(int coord, int size)
    {
        return coord == 0 || coord == size - 1 || coord % PILLAR_SPACING == 0;
    }
}
