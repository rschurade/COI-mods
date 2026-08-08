using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Mafi;
using Mafi.Collections;
using Mafi.Core;
using Mafi.Core.Buildings.Cargo.Ships;
using Mafi.Core.Trains;
using Mafi.Unity;
using UnityEngine;
using EntityId = Mafi.Core.EntityId;

namespace ShippingPP.Ships;

/// <summary>
/// Paints local ships in the color of their shipping line, the way trains wear their line's
/// color. The sim side publishes an immutable ship→color snapshot here (rebuilt on every
/// assignment or color change and on the manager's scan tick); the render side polls it from
/// <see cref="ShipLineTintMb"/> — a MonoBehaviour must never read the manager's live
/// dictionaries, they are mutated concurrently by the sim thread.
/// </summary>
internal static class ShipTint
{
    private static volatile System.Collections.Generic.Dictionary<EntityId, TrainColor>
        s_colors = new System.Collections.Generic.Dictionary<EntityId, TrainColor>();

    /// <summary>Sim side: replaces the snapshot. The dictionary must not be mutated after
    /// this call.</summary>
    public static void Publish(System.Collections.Generic.Dictionary<EntityId, TrainColor> colors)
    {
        s_colors = colors;
    }

    /// <summary>Render side: the line color the ship should currently wear, if any.</summary>
    public static bool TryGet(EntityId shipId, out TrainColor color)
    {
        return s_colors.TryGetValue(shipId, out color);
    }
}

/// <summary>
/// Repaints the white parts of a cargo ship (superstructure, railings) in its line color.
/// Added to every cargo ship MonoBehaviour by <see cref="ShipTintPatch"/>; ships without a
/// published color (world ships, local ships with no line) keep their vanilla look.
///
/// The whole hull — front, rear and the hull section under every cargo module — is a single
/// shared material ('CargoShip', Standard shader, one albedo atlas), so the white parts
/// cannot be isolated by material or by a shader color property; a whole-material tint
/// darkens the entire ship. Instead a copy of the albedo texture is baked per line color
/// with only the near-white, low-saturation texels shifted to the line color (shading
/// preserved via their brightness); saturated areas — blue hull, red waterline, orange
/// lifeboats — are untouched. Painted ships get the baked material, everything else keeps
/// the original: the shared material asset itself is never modified, so vanilla world ships
/// are unaffected. Bakes are cached per color for the session.
/// </summary>
internal sealed class ShipLineTintMb : MonoBehaviour
{
    /// <summary>Name of the shared hull material asset ('CargoShip'), the repaint target.
    /// Everything else on the ship (module containers, cargo piles, particles) keeps its
    /// own material.</summary>
    private const string HULL_MATERIAL_NAME = "CargoShip";

    private static readonly int COLOR_ID = Shader.PropertyToID("_Color");
    private static readonly int ACCENT_ID = Shader.PropertyToID("_AccentColor");

    private static Material s_originalMaterial;
    private static readonly Dict<ulong, Material> s_tintedMaterials = new Dict<ulong, Material>();
    private static bool s_dumped;

    private CargoShipV2 m_ship;
    private Lyst<Renderer> m_hullRenderers;
    private int m_collectedChildCount = -1;
    private bool m_hasApplied;
    private TrainColor m_applied;

    public void Init(CargoShipV2 ship)
    {
        m_ship = ship;
    }

    private void Update()
    {
        if (m_ship == null)
        {
            return;
        }
        if (transform.childCount != m_collectedChildCount)
        {
            // First run, or a cargo module was added/removed (each is its own child): the
            // module's hull section shares the repainted material, so recollect and reapply.
            collectHullRenderers();
            m_hasApplied = false;
        }
        if (ShipTint.TryGet(m_ship.Id, out TrainColor color))
        {
            if (!m_hasApplied || m_applied.Raw != color.Raw)
            {
                apply(color);
            }
        }
        else if (m_hasApplied)
        {
            clearTint();
        }
    }

    /// <summary>All mesh renderers using the shared hull material (or a repaint of it, when
    /// recollecting an already-painted ship).</summary>
    private void collectHullRenderers()
    {
        m_collectedChildCount = transform.childCount;
        m_hullRenderers = new Lyst<Renderer>();
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (!(renderer is MeshRenderer))
            {
                continue;
            }
            Material material = renderer.sharedMaterial;
            if (material == null)
            {
                continue;
            }
            if (s_originalMaterial == null && material.name == HULL_MATERIAL_NAME)
            {
                s_originalMaterial = material;
            }
            if (material == s_originalMaterial || isTintedMaterial(material))
            {
                m_hullRenderers.Add(renderer);
            }
        }
    }

    private static bool isTintedMaterial(Material material)
    {
        foreach (KeyValuePair<ulong, Material> pair in s_tintedMaterials)
        {
            if (pair.Value == material)
            {
                return true;
            }
        }
        return false;
    }

    private void apply(TrainColor color)
    {
        Material tinted = getOrCreateTintedMaterial(color);
        if (tinted == null)
        {
            return;
        }
        m_applied = color;
        m_hasApplied = true;
        foreach (Renderer renderer in m_hullRenderers)
        {
            if (renderer)
            {
                renderer.sharedMaterial = tinted;
            }
        }
        if (Diag.ENABLED)
        {
            dumpModelOnce();
        }
    }

    private void clearTint()
    {
        m_hasApplied = false;
        if (s_originalMaterial == null)
        {
            return;
        }
        foreach (Renderer renderer in m_hullRenderers)
        {
            if (renderer)
            {
                renderer.sharedMaterial = s_originalMaterial;
            }
        }
    }

    private static Material getOrCreateTintedMaterial(TrainColor color)
    {
        if (s_originalMaterial == null)
        {
            return null;
        }
        if (s_tintedMaterials.TryGetValue(color.Raw, out Material cached) && cached != null)
        {
            return cached;
        }
        var tinted = new Material(s_originalMaterial);
        tinted.name = HULL_MATERIAL_NAME + "_ShippingPP_" + color.Raw;
        var albedo = s_originalMaterial.mainTexture as Texture2D;
        if (albedo != null)
        {
            tinted.mainTexture = buildTintedAlbedo(albedo, color.Primary.ToColor());
        }
        else
        {
            Log.Warning("Shipping++: hull material has no 2D albedo texture; ships painted "
                + "with a whole-hull tint instead.");
            tinted.SetColor(COLOR_ID, color.Primary.ToColor());
            if (tinted.HasProperty(ACCENT_ID))
            {
                tinted.SetColor(ACCENT_ID, color.Secondary.ToColor());
            }
        }
        s_tintedMaterials[color.Raw] = tinted;
        return tinted;
    }

    /// <summary>
    /// A copy of the hull albedo with the white paint recolored. Near-white texels (bright
    /// and unsaturated — the superstructure walls and railings) are shifted to the line
    /// color scaled by their own brightness, so shading and edge wear survive; the blend
    /// ramps smoothly to zero for darker or saturated texels, leaving hull blue, waterline
    /// red and deck details untouched. The source texture is not CPU-readable, so it is
    /// read back through a temporary RenderTexture.
    /// </summary>
    private static Texture2D buildTintedAlbedo(Texture2D source, Color tint)
    {
        RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        RenderTexture previous = RenderTexture.active;
        Graphics.Blit(source, rt);
        RenderTexture.active = rt;
        var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32,
            mipChain: true, linear: false);
        copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);

        Color[] pixels = copy.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color pixel = pixels[i];
            Color.RGBToHSV(pixel, out _, out float saturation, out float value);
            float w = smoothStep(value, 0.55f, 0.72f)
                * (1f - smoothStep(saturation, 0.18f, 0.35f));
            if (w <= 0f)
            {
                continue;
            }
            var painted = new Color(tint.r * value, tint.g * value, tint.b * value, pixel.a);
            pixels[i] = Color.Lerp(pixel, painted, w);
        }
        copy.SetPixels(pixels);
        copy.filterMode = source.filterMode;
        copy.anisoLevel = source.anisoLevel;
        copy.wrapMode = source.wrapMode;
        copy.Apply(updateMipmaps: true, makeNoLongerReadable: true);
        return copy;
    }

    private static float smoothStep(float x, float from, float to)
    {
        float t = Mathf.Clamp01((x - from) / (to - from));
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// One-time survey of the ship model (first painted ship of the session): every renderer
    /// with its materials, shaders and color properties. This identified the shared hull
    /// atlas the repaint targets; kept behind the diagnostics switch for future model
    /// changes.
    /// </summary>
    private void dumpModelOnce()
    {
        if (s_dumped)
        {
            return;
        }
        s_dumped = true;
        Log.Info($"Shipping++[shipmodel]: model survey of ship {m_ship.Id} "
            + $"({m_hullRenderers.Count} hull renderers):");
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            var sb = new StringBuilder(256);
            sb.Append("Shipping++[shipmodel]:   ");
            sb.Append(m_hullRenderers.Contains(renderer) ? "hull " : "other");
            sb.Append(" '").Append(pathUnderShip(renderer.transform)).Append("' [")
                .Append(renderer.GetType().Name).Append(']');
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material == null)
                {
                    sb.Append(" | <null material>");
                    continue;
                }
                sb.Append(" | mat='").Append(material.name)
                    .Append("' shader='").Append(material.shader != null ? material.shader.name : "-")
                    .Append("' tex='").Append(material.mainTexture != null ? material.mainTexture.name : "-")
                    .Append('\'');
                if (material.HasProperty(COLOR_ID))
                {
                    sb.Append(" _Color=").Append(material.GetColor(COLOR_ID));
                }
                if (material.HasProperty(ACCENT_ID))
                {
                    sb.Append(" _AccentColor=").Append(material.GetColor(ACCENT_ID));
                }
            }
            Log.Info(sb.ToString());
        }
    }

    private string pathUnderShip(Transform t)
    {
        var sb = new StringBuilder(64);
        while (t != null && t != transform)
        {
            if (sb.Length > 0)
            {
                sb.Insert(0, '/');
            }
            sb.Insert(0, t.name);
            t = t.parent;
        }
        return sb.ToString();
    }
}

/// <summary>
/// Attaches <see cref="ShipLineTintMb"/> to every cargo ship MonoBehaviour the game creates
/// (postfix on the vanilla ship MB factory). All ships get the component; which ships are
/// painted is decided by the published snapshot alone.
/// </summary>
internal static class ShipTintPatch
{
    private const string HARMONY_ID = "com.roest.shippingpp.shiptint";

    private static bool s_applied;

    public static void TryApply()
    {
        if (s_applied)
        {
            return;
        }
        s_applied = true;

        Type factory = AccessTools.TypeByName("Mafi.Unity.Entities.Ships.CargoShipMbFactory");
        MethodInfo target = factory != null ? AccessTools.Method(factory, "Create") : null;
        if (target == null)
        {
            Log.Error("Shipping++: CargoShipMbFactory.Create not resolved; ships stay unpainted.");
            return;
        }

        try
        {
            var harmony = new Harmony(HARMONY_ID);
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(ShipTintPatch),
                nameof(CreatePostfix)));
            Log.Info("Shipping++: ship line-color patch applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to apply ship line-color patch: {ex}");
        }
    }

    private static void CreatePostfix(CargoShipV2 __0, object __result)
    {
        try
        {
            if (__result is Component mb && __0 != null)
            {
                mb.gameObject.AddComponent<ShipLineTintMb>().Init(__0);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to attach ship tint component: {ex.Message}");
        }
    }
}
