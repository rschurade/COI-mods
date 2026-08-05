using System;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Unity;
using UnityEngine;

namespace ShippingPP.Lines;

/// <summary>
/// Procedural model for the navigation buoy: a classic conical sea buoy (flared float barrel,
/// tapered top with a white band, thin mast carrying a small red light housing), lathe-built
/// from a revolved profile with smooth normals — no art assets needed, same approach as the
/// Elevation++ portal crossbeam.
///
/// The template GameObject is injected into the game's <c>AssetsDb</c> under
/// <see cref="PREFAB_PATH"/> (the loader checks its loaded-assets cache before touching asset
/// bundles), so every consumer of the proto's prefab path — the placed-entity renderer, the
/// placement ghost and the blueprint visual — picks it up like a real prefab. Materials are
/// clones of a vanilla Standard-shader material with the textures stripped, keeping lighting
/// native. Sized in Unity units (1 tile = 2 units); the water plane sits ~0.5 units above the
/// entity origin, so the flared bottom stays submerged and the barrel emerges like a real buoy.
/// </summary>
internal static class NavBuoyModel
{
    public const string PREFAB_PATH = "Assets/ShippingPP/NavBuoy.prefab";

    /// <summary>Toolbar icon, rendered from the injected template at game init.</summary>
    public const string ICON_PATH = "Assets/ShippingPP/NavBuoyIcon.png";

    /// <summary>Donor for a plain Standard-shader material (same donor the Elevation++
    /// crossbeam uses); only the shader setup is reused, all textures are stripped.</summary>
    private const string MATERIAL_DONOR_PREFAB = "Assets/Base/Transports/Pillars/Base.prefab";

    private const int SEGMENTS = 24;

    private static readonly Color RED = new Color(0.72f, 0.10f, 0.08f);
    private static readonly Color WHITE = new Color(0.92f, 0.92f, 0.88f);
    private static readonly Color DARK = new Color(0.20f, 0.20f, 0.22f);

    public static void TryInject(DependencyResolver resolver)
    {
        AssetsDb assetsDb;
        try
        {
            assetsDb = resolver.Resolve<AssetsDb>();
        }
        catch (Exception)
        {
            Log.Info("Shipping++: AssetsDb not available (headless run?); buoy model skipped.");
            return;
        }

        GameObject template = null;
        try
        {
            if (!assetsDb.ContainsAsset(PREFAB_PATH))
            {
                template = buildTemplate(assetsDb);
                inject(assetsDb, PREFAB_PATH, template);
                Log.Info("Shipping++: buoy model injected.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to inject buoy model (the buoy falls back to its "
                + $"layout box): {ex}");
        }

        tryInjectIcon(resolver, assetsDb, template);
    }

    /// <summary>Renders the toolbar icon from the buoy template (vanilla beacon icon as
    /// fallback) and injects it under <see cref="ICON_PATH"/>. Must happen before any UI asks
    /// for the icon sprite — sprites are cached forever by path, including the placeholder.
    /// </summary>
    private static void tryInjectIcon(DependencyResolver resolver, AssetsDb assetsDb,
        GameObject template)
    {
        try
        {
            if (assetsDb.ContainsAsset(ICON_PATH))
            {
                return;
            }
            if (template == null
                && !assetsDb.TryGetSharedAsset<GameObject>(PREFAB_PATH, out template))
            {
                return;
            }
            Texture2D icon = null;
            try
            {
                icon = renderIcon(resolver, template);
            }
            catch (Exception ex)
            {
                Log.Warning($"Shipping++: buoy icon rendering failed, falling back to the "
                    + $"beacon icon: {ex.Message}");
            }
            icon = icon ?? tryGetBeaconIconTexture(resolver, assetsDb);
            if (icon == null)
            {
                Log.Warning("Shipping++: no buoy icon available; the toolbar shows a "
                    + "placeholder.");
                return;
            }
            inject(assetsDb, ICON_PATH, icon);
            Log.Info("Shipping++: buoy icon injected.");
        }
        catch (Exception ex)
        {
            Log.Error($"Shipping++: failed to inject buoy icon: {ex}");
        }
    }

    /// <summary>Renders the template with the game's own icon-baking renderer (vanilla light
    /// setup; camera framing computed from the mesh bounds). Runs at game init, far below the
    /// map so nothing else is in frame.</summary>
    private static Texture2D renderIcon(DependencyResolver resolver, GameObject template)
    {
        UnityEngine.Camera mainCamera =
            resolver.Resolve<Mafi.Unity.Camera.CameraController>().Camera;
        if (mainCamera == null)
        {
            throw new InvalidOperationException("main camera not available yet");
        }
        var root = new GameObject("ShippingPP_BuoyIconRenderRoot");
        root.transform.position = new Vector3(0f, -500f, 0f);
        GameObject model = UnityEngine.Object.Instantiate(template);
        model.hideFlags = HideFlags.None;
        model.SetActive(true);
        var renderer = new Mafi.Unity.TexturesGenerators.GameObjectRenderer(root, 20f,
            mainCamera);
        bool isSetUp = false;
        try
        {
            renderer.SetUpRendering();
            isSetUp = true;
            renderer.SetImageSize(new Vector2i(256, 256));
            renderer.SetCamera(24.Degrees(), 0.Degrees(), 20.Degrees());
            renderer.SetLight(Color.white, 40.Degrees(), 110.Degrees());
            Bounds bounds = model.GetComponentInChildren<MeshRenderer>().bounds;
            float maxSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            float distance = 1.25f * maxSize * 0.5f / Mathf.Tan(10f * Mathf.Deg2Rad);
            Texture2D rendered = renderer.RenderToTexture(model, bounds.center, distance);
            var icon = new Texture2D(rendered.width, rendered.height, TextureFormat.ARGB32,
                mipChain: true)
            {
                name = "ShippingPP_NavBuoyIcon",
            };
            icon.SetPixels32(rendered.GetPixels32());
            icon.Apply(updateMipmaps: true);
            return icon;
        }
        finally
        {
            if (isSetUp)
            {
                renderer.TearDownRendering();
            }
            UnityEngine.Object.Destroy(model);
            UnityEngine.Object.Destroy(root);
        }
    }

    private static Texture2D tryGetBeaconIconTexture(DependencyResolver resolver,
        AssetsDb assetsDb)
    {
        try
        {
            Mafi.Core.Entities.Static.StaticEntityProto beacon =
                resolver.Resolve<Mafi.Core.Prototypes.ProtosDb>()
                    .Get<Mafi.Core.Entities.Static.StaticEntityProto>(
                        new Mafi.Core.Entities.Static.StaticEntityProto.ID("Beacon"))
                    .ValueOrNull;
            if (beacon != null && assetsDb.TryGetSharedAsset<Texture2D>(
                ProtoUtils.VanillaIconPath(beacon), out Texture2D texture))
            {
                return texture;
            }
        }
        catch (Exception)
        {
            // Fallback only; the caller logs the placeholder case.
        }
        return null;
    }

    /// <summary>Adds an asset under the given path to the asset loader's loaded-assets cache
    /// (checked before any bundle lookup). Reflection because the cache is private; the same
    /// exact-cased key is used by the proto, hitting the fast path.</summary>
    private static void inject(AssetsDb assetsDb, string path, UnityEngine.Object asset)
    {
        FieldInfo loaderField = typeof(AssetsDb).GetField("m_bundleLoader",
            BindingFlags.NonPublic | BindingFlags.Instance);
        object loader = loaderField?.GetValue(assetsDb)
            ?? throw new InvalidOperationException("AssetsDb.m_bundleLoader not found.");
        FieldInfo assetsField = loader.GetType().GetField("m_loadedAssets",
            BindingFlags.NonPublic | BindingFlags.Instance);
        object dict = assetsField?.GetValue(loader)
            ?? throw new InvalidOperationException("AssetBundleLoader.m_loadedAssets not found.");
        MethodInfo add = dict.GetType().GetMethod("Add",
            new[] { typeof(string), typeof(UnityEngine.Object) })
            ?? throw new InvalidOperationException("loaded-assets Add(string, Object) not found.");
        add.Invoke(dict, new object[] { path, asset });
    }

    private static GameObject buildTemplate(AssetsDb assetsDb)
    {
        var go = new GameObject("ShippingPP_NavBuoy");
        // Survives scene reloads and stays out of the hierarchy, like a bundle prefab; the
        // model factory clones it and activates the clone.
        go.hideFlags = HideFlags.HideAndDontSave;
        go.SetActive(false);

        var mesh = buildMesh();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        Material donor = findDonorMaterial(assetsDb);
        renderer.sharedMaterials = new[]
        {
            makeFlat(donor, RED),
            makeFlat(donor, WHITE),
            makeFlat(donor, DARK),
        };
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        return go;
    }

    private static Material findDonorMaterial(AssetsDb assetsDb)
    {
        if (assetsDb.TryGetSharedAsset<GameObject>(MATERIAL_DONOR_PREFAB, out GameObject donor))
        {
            MeshRenderer renderer = donor.GetComponentInChildren<MeshRenderer>(
                includeInactive: true);
            if (renderer != null && renderer.sharedMaterial != null)
            {
                return renderer.sharedMaterial;
            }
        }
        Log.Warning($"Shipping++: material donor '{MATERIAL_DONOR_PREFAB}' not found; buoy "
            + "uses a default Standard material.");
        return new Material(Shader.Find("Standard"));
    }

    private static Material makeFlat(Material source, Color color)
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
        if (material.HasProperty("_Color"))
        {
            material.color = color;
        }
        if (material.HasProperty("_Metallic"))
        {
            material.SetFloat("_Metallic", 0.15f);
        }
        if (material.HasProperty("_Glossiness"))
        {
            material.SetFloat("_Glossiness", 0.35f);
        }
        return material;
    }

    /// <summary>
    /// The buoy as three lathe submeshes (0 red, 1 white band, 2 gray mast). Origin at the
    /// entity position, +Y up; the waterline crosses the flared barrel bottom (~0.5 above
    /// origin). Profile corners get hard edges (rings are per segment), circumference is
    /// smooth (radial normals from the profile slope).
    /// </summary>
    private static Mesh buildMesh()
    {
        var b = new MeshBuilder(subMeshes: 3);

        // Red: flared float barrel, tapered top, light housing with a conical cap.
        b.Disc(0, r: 0.55f, y: 0f, up: false);
        b.Revolve(0, 0.55f, 0f, 0.90f, 0.45f);
        b.Revolve(0, 0.90f, 0.45f, 0.90f, 1.15f);
        b.Revolve(0, 0.90f, 1.15f, 0.16f, 2.45f);
        b.Disc(0, r: 0.16f, y: 2.45f, up: true);
        b.Disc(0, r: 0.24f, y: 3.35f, up: false);
        b.Revolve(0, 0.24f, 3.35f, 0.24f, 3.75f);
        b.Revolve(0, 0.24f, 3.75f, 0.0f, 4.05f);

        // White band riding 0.02 proud of the taper (r on the taper at y=1.35 / 1.85 is
        // 0.786 / 0.502), with pinched ends so no open edge shows.
        b.Revolve(1, 0.786f, 1.35f, 0.806f, 1.37f);
        b.Revolve(1, 0.806f, 1.37f, 0.522f, 1.83f);
        b.Revolve(1, 0.522f, 1.83f, 0.502f, 1.85f);

        // Gray mast between the taper top and the light housing.
        b.Revolve(2, 0.09f, 2.45f, 0.09f, 3.35f);

        return b.Build("ShippingPP_NavBuoy");
    }

    /// <summary>Minimal lathe-mesh builder: revolved profile segments and horizontal discs
    /// around the +Y axis, with smooth per-ring normals.</summary>
    private sealed class MeshBuilder
    {
        private readonly List<Vector3> m_vertices = new List<Vector3>();
        private readonly List<Vector3> m_normals = new List<Vector3>();
        private readonly List<int>[] m_triangles;

        public MeshBuilder(int subMeshes)
        {
            m_triangles = new List<int>[subMeshes];
            for (int i = 0; i < subMeshes; i++)
            {
                m_triangles[i] = new List<int>();
            }
        }

        /// <summary>Revolves the profile segment (r0, y0)-(r1, y1) around +Y. Normals follow
        /// the profile slope, so stacked segments shade smoothly around the circumference but
        /// keep hard edges between segments.</summary>
        public void Revolve(int subMesh, float r0, float y0, float r1, float y1)
        {
            // Outward normal of the profile line in the (r, y) plane.
            var slope = new Vector2(y1 - y0, r0 - r1).normalized;
            int baseIndex = m_vertices.Count;
            for (int i = 0; i <= SEGMENTS; i++)
            {
                float angle = i * (2f * Mathf.PI / SEGMENTS);
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);
                m_vertices.Add(new Vector3(r0 * cos, y0, r0 * sin));
                m_vertices.Add(new Vector3(r1 * cos, y1, r1 * sin));
                var normal = new Vector3(slope.x * cos, slope.y, slope.x * sin);
                m_normals.Add(normal);
                m_normals.Add(normal);
            }
            List<int> triangles = m_triangles[subMesh];
            for (int i = 0; i < SEGMENTS; i++)
            {
                int lower = baseIndex + 2 * i;
                // Front faces wind clockwise seen from outside (+radial).
                triangles.Add(lower);
                triangles.Add(lower + 1);
                triangles.Add(lower + 3);
                triangles.Add(lower);
                triangles.Add(lower + 3);
                triangles.Add(lower + 2);
            }
        }

        /// <summary>Horizontal disc of the given radius at height y, facing up or down.</summary>
        public void Disc(int subMesh, float r, float y, bool up)
        {
            int centerIndex = m_vertices.Count;
            Vector3 normal = up ? Vector3.up : Vector3.down;
            m_vertices.Add(new Vector3(0f, y, 0f));
            m_normals.Add(normal);
            for (int i = 0; i <= SEGMENTS; i++)
            {
                float angle = i * (2f * Mathf.PI / SEGMENTS);
                m_vertices.Add(new Vector3(r * Mathf.Cos(angle), y, r * Mathf.Sin(angle)));
                m_normals.Add(normal);
            }
            List<int> triangles = m_triangles[subMesh];
            for (int i = 0; i < SEGMENTS; i++)
            {
                triangles.Add(centerIndex);
                if (up)
                {
                    triangles.Add(centerIndex + i + 2);
                    triangles.Add(centerIndex + i + 1);
                }
                else
                {
                    triangles.Add(centerIndex + i + 1);
                    triangles.Add(centerIndex + i + 2);
                }
            }
        }

        public Mesh Build(string name)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(m_vertices);
            mesh.SetNormals(m_normals);
            mesh.subMeshCount = m_triangles.Length;
            for (int i = 0; i < m_triangles.Length; i++)
            {
                mesh.SetTriangles(m_triangles[i], i);
            }
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
