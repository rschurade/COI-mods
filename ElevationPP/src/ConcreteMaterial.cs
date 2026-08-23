using System;
using Mafi;
using Mafi.Unity;
using UnityEngine;

namespace ElevationPP;

/// <summary>
/// The mod's shared "poured concrete" material for procedural geometry: the portal crossbeams
/// (<see cref="PortalCrossbeamRenderer"/>) and the concrete platforms
/// (<see cref="Platforms.ConcretePlatformModel"/>) use the very same material, so they read as one
/// family of structures.
///
/// It is a clone of the game's transport-pillar concrete material (a plain Unity Standard
/// material, so lighting stays native) with its atlas/rivet maps stripped and the seamlessly
/// tiling terrain-concrete albedo/normal maps swapped in — the model atlases are made for
/// authored UVs, the terrain surface maps for arbitrary box-projected UVs, which is what
/// procedural meshes need. Only the borderless interior of one flooring block is used (the
/// texture is a 4x4 grid of blocks with grooves in between), copied into a mirror-wrapping
/// texture so the repeats stay continuous.
///
/// Built once per session and cached; the cache is dropped on <see cref="Reset"/> (new game
/// session) because Unity objects do not survive scene reloads.
/// </summary>
internal static class ConcreteMaterial
{
    // Donor for the MATERIAL (shader setup, not geometry): the transport pillar's concrete base.
    private const string MATERIAL_SOURCE_PREFAB = "Assets/Base/Transports/Pillars/Base.prefab";

    // Seamlessly tiling concrete maps from the game's terrain surface set.
    private const string CONCRETE_ALBEDO =
        "Assets/Base/Terrain/Surfaces/Concrete/concreteBlock1a-256-albedo.png";
    private const string CONCRETE_NORMALS =
        "Assets/Base/Terrain/Surfaces/Concrete/concreteBlock1a-256-normals.png";

    // The concrete flooring texture is a 4x4 grid of blocks (grooves every 1/4 of the image).
    // Only the groove-free interior of one block is used: a patch starting at this fraction
    // into the image, PATCH_SIZE of the image wide/tall. The patch copy wraps in MIRROR mode
    // so repeats stay continuous (mirroring is invisible on noisy concrete).
    private const float PATCH_START = 70f / 256f;
    private const float PATCH_SIZE = 52f / 256f;

    /// <summary>UV scale the material is authored for: the texture repeats every
    /// 1/UV_SCALE Unity units (1 tile = 2 units).</summary>
    public const float UV_SCALE = 1f;

    private static Material s_material;
    private static bool s_failed;

    /// <summary>Forgets the cached material (call at the start of a game session).</summary>
    public static void Reset()
    {
        s_material = null;
        s_failed = false;
    }

    /// <summary>
    /// The shared concrete material, built on first use. False (with a warning logged once)
    /// when the donor material is unavailable; callers then skip their geometry.
    /// </summary>
    public static bool TryGet(AssetsDb assetsDb, out Material material)
    {
        if (s_material != null)
        {
            material = s_material;
            return true;
        }
        material = null;
        if (s_failed)
        {
            return false;
        }
        if (!assetsDb.TryGetSharedAsset<GameObject>(MATERIAL_SOURCE_PREFAB, out GameObject prefab))
        {
            s_failed = true;
            Log.Warning($"Elevation++: material donor '{MATERIAL_SOURCE_PREFAB}' not found, "
                + "no concrete material.");
            return false;
        }
        MeshRenderer donor = prefab.GetComponentInChildren<MeshRenderer>(includeInactive: true);
        if (donor == null || donor.sharedMaterial == null)
        {
            s_failed = true;
            Log.Warning("Elevation++: material donor has no renderer/material, no concrete material.");
            return false;
        }
        logMaterialInfo(donor.sharedMaterial);
        s_material = flatConcrete(assetsDb, donor.sharedMaterial, Color.white);
        material = s_material;
        return true;
    }

    /// <summary>
    /// Clones the donor material, strips its atlas/rivet textures and swaps in the game's
    /// seamlessly tiling terrain-concrete maps. Falls back to a flat matte color when the
    /// textures are unavailable.
    /// </summary>
    private static Material flatConcrete(AssetsDb assetsDb, Material source, Color color)
    {
        var material = new Material(source);
        foreach (string property in new[] { "_MainTex", "_MetallicGlossMap", "_BumpMap" })
        {
            if (material.HasProperty(property))
            {
                material.SetTexture(property, null);
            }
        }
        material.DisableKeyword("_METALLICGLOSSMAP");
        material.DisableKeyword("_NORMALMAP");
        if (assetsDb.TryGetSharedAsset<Texture2D>(CONCRETE_ALBEDO, out Texture2D albedo))
        {
            material.SetTexture("_MainTex", makeSeamlessInterior(albedo, linear: false));
        }
        else
        {
            Log.Warning($"Elevation++: concrete albedo '{CONCRETE_ALBEDO}' not found, "
                + "concrete stays flat-colored.");
        }
        if (assetsDb.TryGetSharedAsset<Texture2D>(CONCRETE_NORMALS, out Texture2D normals))
        {
            material.SetTexture("_BumpMap", makeSeamlessInterior(normals, linear: true));
            material.EnableKeyword("_NORMALMAP");
        }
        if (material.HasProperty("_Color"))
        {
            material.color = color;
        }
        if (material.HasProperty("_Metallic"))
        {
            material.SetFloat("_Metallic", 0f);
        }
        if (material.HasProperty("_Glossiness"))
        {
            material.SetFloat("_Glossiness", 0.1f);
        }
        return material;
    }

    /// <summary>
    /// Copies the borderless interior of a tile texture into a new mirror-wrapping texture
    /// (GPU blit + readback, so compressed non-readable sources work). Mirror wrap makes
    /// the repeats seamless by construction.
    /// </summary>
    private static Texture2D makeSeamlessInterior(Texture2D source, bool linear)
    {
        int width = Mathf.RoundToInt(source.width * PATCH_SIZE);
        int height = Mathf.RoundToInt(source.height * PATCH_SIZE);
        RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0,
            RenderTextureFormat.ARGB32,
            linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, temporary,
            new Vector2(PATCH_SIZE, PATCH_SIZE),
            new Vector2(PATCH_START, PATCH_START));
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = temporary;
        var result = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: true, linear);
        result.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
        result.Apply(updateMipmaps: true);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(temporary);
        result.wrapMode = TextureWrapMode.Mirror;
        result.name = source.name + "_seamless";
        return result;
    }

    private static void logMaterialInfo(Material source)
    {
        try
        {
            var sb = new System.Text.StringBuilder(
                $"Elevation++: concrete donor material '{source.name}' shader '{source.shader.name}':");
            int count = source.shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = source.shader.GetPropertyName(i);
                var type = source.shader.GetPropertyType(i);
                string value = "";
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        value = source.GetFloat(name).ToString("F2");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        value = source.GetColor(name).ToString();
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        Texture texture = source.GetTexture(name);
                        value = texture != null ? $"{texture.name} {texture.width}x{texture.height}" : "null";
                        break;
                }
                sb.Append($"\n  {name} ({type}) = {value}");
            }
            Log.Info(sb.ToString());
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }
}
