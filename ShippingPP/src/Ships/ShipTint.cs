using System;
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
/// Applies the line color to a cargo ship's hull. Added to every cargo ship MonoBehaviour by
/// <see cref="ShipTintPatch"/>; ships without a published color (world ships, local ships with
/// no line) keep their vanilla look.
///
/// The color is applied per renderer via <see cref="MaterialPropertyBlock"/> — the same
/// <c>_Color</c>/<c>_AccentColor</c> properties the game stamps on colorizable train and
/// billboard materials, but without cloning materials, so clearing the block restores the
/// vanilla look exactly. Only hull renderers are painted (the front and back prefab, not the
/// cargo modules), and only mesh renderers (tinting a particle material would color the
/// exhaust smoke).
/// </summary>
internal sealed class ShipLineTintMb : MonoBehaviour
{
    private static readonly int COLOR_ID = Shader.PropertyToID("_Color");
    private static readonly int ACCENT_ID = Shader.PropertyToID("_AccentColor");
    private static MaterialPropertyBlock s_mpb;
    private static bool s_dumped;

    private CargoShipV2 m_ship;
    private Renderer[] m_hullRenderers;
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
        if (m_hullRenderers == null)
        {
            collectHullRenderers();
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

    /// <summary>
    /// The paintable renderers: everything under the ship root except the cargo module
    /// sub-objects (each module lives in its own child with a <c>CargoShipModule*Mb</c>
    /// component; modules added later create new children, which never enter this list) and
    /// except particle/trail renderers. Collected once — the hull children are created before
    /// the ship is first activated and never change.
    /// </summary>
    private void collectHullRenderers()
    {
        var renderers = new Lyst<Renderer>();
        foreach (Transform child in transform)
        {
            if (isModuleGo(child))
            {
                continue;
            }
            foreach (Renderer renderer in child.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                {
                    renderers.Add(renderer);
                }
            }
        }
        m_hullRenderers = renderers.ToArray();
    }

    private static bool isModuleGo(Transform child)
    {
        foreach (MonoBehaviour mb in child.GetComponents<MonoBehaviour>())
        {
            if (mb != null && mb.GetType().Name.StartsWith("CargoShipModule", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private void apply(TrainColor color)
    {
        m_applied = color;
        m_hasApplied = true;
        MaterialPropertyBlock mpb = s_mpb ?? (s_mpb = new MaterialPropertyBlock());
        mpb.Clear();
        mpb.SetColor(COLOR_ID, color.Primary.ToColor());
        mpb.SetColor(ACCENT_ID, color.Secondary.ToColor());
        foreach (Renderer renderer in m_hullRenderers)
        {
            if (renderer)
            {
                renderer.SetPropertyBlock(mpb);
            }
        }
        dumpModelOnce();
    }

    private void clearTint()
    {
        m_hasApplied = false;
        foreach (Renderer renderer in m_hullRenderers)
        {
            if (renderer)
            {
                renderer.SetPropertyBlock(null);
            }
        }
    }

    /// <summary>
    /// One-time survey of the ship model (first painted ship of the session): every renderer
    /// with its materials, shaders and color properties. This is the ground truth for
    /// restricting the tint to the white parts of the model — which materials those are cannot
    /// be read from the game code, only from the loaded assets.
    /// </summary>
    private void dumpModelOnce()
    {
        if (s_dumped)
        {
            return;
        }
        s_dumped = true;
        Log.Info($"Shipping++[shipmodel]: model survey of ship {m_ship.Id} "
            + $"({m_hullRenderers.Length} hull renderers):");
        foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
        {
            var sb = new StringBuilder(256);
            sb.Append("Shipping++[shipmodel]:   ");
            sb.Append(Array.IndexOf(m_hullRenderers, renderer) >= 0 ? "hull " : "other");
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
