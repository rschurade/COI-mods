using System;
using System.Reflection;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Unity;
using UnityEngine;

namespace ElevationPP;

/// <summary>
/// Fixes the rendering gap on rail pillars taller than the vanilla tower mesh.
///
/// TrainTrackPillarsRenderer draws each rail pillar as exactly two GPU instances: a "base" mesh
/// at the ground and a fixed-length "tower" mesh anchored to the underside of the rail
/// (getTowerPosition = base + height in Unity units). The per-instance data is
/// position/rotation/color only — no scale — and the tower model is authored to cover the
/// vanilla 6-tile height cap. Pillars taller than that render the base plus ~6 tiles of column
/// hanging from the rail, with a gap in between.
///
/// This Harmony postfix on PillarsChunkBase.AddPillar(Tile3f, ThicknessTilesF, RelTile2f, bool,
/// ColorRgba) stacks additional tower instances below the original until the column reaches the
/// ground (the lowest copy may poke below terrain, which is invisible). The extra packed
/// part-ids are appended to the returned PooledArray, so the vanilla RemovePillarParts — which
/// iterates that array — removes them correctly when the pillar goes away, and the chunk's
/// InstancesCount is bumped to keep the add/remove bookkeeping balanced. The stack offset is
/// snapped to whole tiles so the segment pattern of stacked copies stays aligned.
///
/// (Transport/pipe/belt pillars are rendered tile-by-tile and need no such fix.)
///
/// Everything private (the nested chunk class, the PillarPartInstanceData struct, the
/// InstancedMeshesRenderer lists) is reached via reflection that is resolved once in TryApply;
/// the patch is skipped gracefully when Mafi.Unity is not loaded (headless runs).
/// </summary>
internal static class RailPillarRenderPatch
{
    private const string HARMONY_ID = "com.roest.elevationpp";
    private const int TOWER_MESH_INDEX = 1;
    // Safety cap; at the mod's max pillar height of 30 tiles and a 6-tile step this is ~4.
    private const int MAX_EXTRA_INSTANCES = 16;

    private static bool s_applied;
    private static bool s_runtimeErrorLogged;

    // Resolved once in TryApply.
    private static FieldInfo s_parentRendererField;     // PillarsChunkBase.ParentRenderer
    private static FieldInfo s_renderersField;          // PillarsChunkBase.m_renderers
    private static FieldInfo s_previewRenderersField;   // PillarsChunkBase.m_previewRenderers
    private static FieldInfo s_lystItemsField;          // LystStruct<...>.m_items
    private static PropertyInfo s_instancesCountProp;   // PillarsChunkBase.InstancesCount
    private static ConstructorInfo s_instanceDataCtor;  // PillarPartInstanceData(Vector3, AngleDegrees1f, ColorRgba)
    private static MethodInfo s_addInstanceMethod;      // InstancedMeshesRenderer<...>.AddInstance
    private static FieldInfo s_towerMatMeshField;       // TrainTrackPillarsRenderer.m_pillarTowerMatMesh
    private static FieldInfo s_sharedMeshLodsField;     // PillarMeshMat.SharedMeshLods

    private static float s_towerCoverageUnits = -1f;    // computed lazily from the tower mesh bounds

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }

        Type rendererType = null;
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            rendererType = asm.GetType("Mafi.Unity.Trains.TrainTrackPillarsRenderer");
            if (rendererType != null)
            {
                break;
            }
        }

        if (rendererType == null)
        {
            Log.Info("Elevation++: TrainTrackPillarsRenderer not found (headless run?), " +
                     "rail pillar render patch skipped.");
            return;
        }

        Type chunkBaseType = rendererType.GetNestedType("PillarsChunkBase", BindingFlags.NonPublic);
        if (chunkBaseType == null)
        {
            Log.Error("Elevation++: PillarsChunkBase nested type not found!");
            return;
        }

        MethodInfo addPillarMethod = chunkBaseType.GetMethod("AddPillar",
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(Tile3f), typeof(ThicknessTilesF), typeof(RelTile2f), typeof(bool), typeof(ColorRgba) },
            null);
        Type instanceDataType = chunkBaseType.GetNestedType("PillarPartInstanceData", BindingFlags.NonPublic);

        s_parentRendererField = chunkBaseType.GetField("ParentRenderer",
            BindingFlags.NonPublic | BindingFlags.Instance);
        s_renderersField = chunkBaseType.GetField("m_renderers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        s_previewRenderersField = chunkBaseType.GetField("m_previewRenderers",
            BindingFlags.NonPublic | BindingFlags.Instance);
        s_instancesCountProp = chunkBaseType.GetProperty("InstancesCount",
            BindingFlags.Public | BindingFlags.Instance);
        s_towerMatMeshField = rendererType.GetField("m_pillarTowerMatMesh",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (addPillarMethod == null || instanceDataType == null || s_parentRendererField == null
            || s_renderersField == null || s_previewRenderersField == null
            || s_instancesCountProp == null || s_instancesCountProp.GetSetMethod(nonPublic: true) == null
            || s_towerMatMeshField == null)
        {
            Log.Error("Elevation++: Failed to resolve rail pillar renderer internals, " +
                      "render patch skipped. Tall rail pillars will show a gap.");
            return;
        }

        s_instanceDataCtor = instanceDataType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(Vector3), typeof(AngleDegrees1f), typeof(ColorRgba) }, null);
        s_lystItemsField = s_renderersField.FieldType.GetField("m_items",
            BindingFlags.NonPublic | BindingFlags.Instance);
        s_sharedMeshLodsField = s_towerMatMeshField.FieldType.GetField("SharedMeshLods",
            BindingFlags.Public | BindingFlags.Instance);

        if (s_instanceDataCtor == null || s_lystItemsField == null || s_sharedMeshLodsField == null)
        {
            Log.Error("Elevation++: Failed to resolve rail pillar instance-data internals, " +
                      "render patch skipped. Tall rail pillars will show a gap.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(addPillarMethod,
                postfix: new HarmonyMethod(typeof(RailPillarRenderPatch), nameof(AddPillarPostfix)));
            s_applied = true;
            Log.Info("Elevation++: Rail pillar render patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Elevation++: Harmony patching failed: {ex}");
        }
    }

    private static void AddPillarPostfix(object __instance, ref PooledArray<uint> __result,
        Tile3f position, ThicknessTilesF height, RelTile2f direction, bool isBlueprint, ColorRgba color)
    {
        try
        {
            // Empty result means the pillar was below the minimum render height — nothing to extend.
            if (!__result.IsValid || __result.Length == 0)
            {
                return;
            }

            float coverage = getTowerCoverageUnits(__instance);
            if (coverage <= 0f)
            {
                return;
            }

            // 1 tile = 2 Unity units (see UnityExtensions.ToVector3 / getTowerPosition).
            float heightUnits = height.Value.ToFloat() * 2f;
            if (heightUnits <= coverage)
            {
                return;
            }

            // Snap the stacking offset down to whole tiles so the segment pattern of the
            // stacked copies stays aligned with the original.
            float step = (float)Math.Floor(coverage / 2f) * 2f;
            if (step < 2f)
            {
                return;
            }

            int extraCount = (int)Math.Ceiling((heightUnits - coverage) / step);
            if (extraCount <= 0)
            {
                return;
            }
            if (extraCount > MAX_EXTRA_INSTANCES)
            {
                extraCount = MAX_EXTRA_INSTANCES;
            }

            object towerRenderer = getTowerRenderer(__instance, isBlueprint);
            if (towerRenderer == null)
            {
                return;
            }

            if (s_addInstanceMethod == null)
            {
                s_addInstanceMethod = towerRenderer.GetType().GetMethod("AddInstance",
                    BindingFlags.Public | BindingFlags.Instance);
                if (s_addInstanceMethod == null)
                {
                    return;
                }
            }

            Vector3 basePos = position.ToVector3();
            float towerY = basePos.y + heightUnits;
            AngleDegrees1f angle = direction.Angle;

            PooledArray<uint> oldParts = __result;
            int oldLen = oldParts.Length;
            PooledArray<uint> extended = PooledArray<uint>.GetPooled(oldLen + extraCount);
            Array.Copy(oldParts.BackingArray, extended.BackingArray, oldLen);

            for (int k = 1; k <= extraCount; k++)
            {
                var pos = new Vector3(basePos.x, towerY - k * step, basePos.z);
                object data = s_instanceDataCtor.Invoke(new object[] { pos, angle, color });
                var instanceId = (ushort)s_addInstanceMethod.Invoke(towerRenderer, new[] { data });
                extended[oldLen + k - 1] = packPartId(TOWER_MESH_INDEX, isBlueprint, instanceId);
            }

            // RemovePillarParts subtracts parts.Length, so account for the extras here.
            int count = (int)s_instancesCountProp.GetValue(__instance);
            s_instancesCountProp.SetValue(__instance, count + extraCount);

            oldParts.ReturnToPool();
            __result = extended;
        }
        catch (Exception ex)
        {
            if (!s_runtimeErrorLogged)
            {
                s_runtimeErrorLogged = true;
                Log.Error($"Elevation++: Rail pillar render extension failed (logged once): {ex}");
            }
        }
    }

    /// <summary>
    /// How far (in Unity units) the tower mesh extends downward from its anchor at the rail.
    /// Measured from the LOD0 mesh bounds; bounds metadata is available even for non-readable meshes.
    /// </summary>
    private static float getTowerCoverageUnits(object chunkBase)
    {
        if (s_towerCoverageUnits >= 0f)
        {
            return s_towerCoverageUnits;
        }

        object parentRenderer = s_parentRendererField.GetValue(chunkBase);
        object matMesh = s_towerMatMeshField.GetValue(parentRenderer);
        var lods = (Mesh[])s_sharedMeshLodsField.GetValue(matMesh);
        if (lods == null || lods.Length == 0 || lods[0] == null)
        {
            s_towerCoverageUnits = 0f;
            return 0f;
        }

        s_towerCoverageUnits = Math.Max(0f, -lods[0].bounds.min.y);
        Log.Info($"Elevation++: Rail tower mesh covers {s_towerCoverageUnits} units " +
                 $"({s_towerCoverageUnits / 2f} tiles) below the rail.");
        return s_towerCoverageUnits;
    }

    /// <summary>
    /// Fetches m_renderers[1] / m_previewRenderers[1] (the tower InstancedMeshesRenderer).
    /// LystStruct is a struct, but its private m_items array is shared with the boxed copy.
    /// </summary>
    private static object getTowerRenderer(object chunkBase, bool isBlueprint)
    {
        object lystBoxed = (isBlueprint ? s_previewRenderersField : s_renderersField).GetValue(chunkBase);
        var items = (Array)s_lystItemsField.GetValue(lystBoxed);
        if (items == null || items.Length <= TOWER_MESH_INDEX)
        {
            return null;
        }
        return items.GetValue(TOWER_MESH_INDEX);
    }

    /// <summary>Mirrors PillarsChunkBase.packRendererAndInstanceIds.</summary>
    private static uint packPartId(int rendererIndex, bool isBlueprint, ushort instanceId)
    {
        return ((isBlueprint ? 1u : 0u) << 31) | (uint)(rendererIndex << 16) | instanceId;
    }
}
