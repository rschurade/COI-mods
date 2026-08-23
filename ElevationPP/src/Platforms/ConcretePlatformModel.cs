using System;
using System.Collections.Generic;
using System.Reflection;
using Mafi;
using Mafi.Unity;
using UnityEngine;

namespace ElevationPP.Platforms;

/// <summary>
/// Procedural model for the concrete platforms: a plain slab, one tile thick, spanning the whole
/// footprint, in the mod's shared poured-concrete look (<see cref="ConcreteMaterial"/> — the same
/// material as the rail portal crossbeams). Box-projected UVs at the material's scale, so the
/// concrete grain runs continuously across the top and down the sides.
///
/// The template GameObject is injected into the game's <c>AssetsDb</c> under the size's
/// <see cref="PrefabPath"/> (the loader checks its loaded-assets cache before touching asset
/// bundles), so every consumer of the proto's prefab path — the placed-entity renderer, the
/// placement ghost and the blueprint visual — picks it up like a real prefab. The model pivot
/// follows the game's convention for layout entities (footprint centre at the base height); a
/// box collider makes the slab pickable like any building. The toolbar icon is rendered from
/// the same template with the game's own icon renderer at game init.
/// </summary>
internal static class ConcretePlatformModel
{
    private const float TILE = 2f;               // Unity units per tile
    private const float THICKNESS_TILES = 1f;    // the deck token is one tile tall

    private static readonly List<int> s_injectedSizes = new List<int>();

    public static string PrefabPath(int size) => $"Assets/ElevationPP/ConcretePlatform{size}.prefab";
    public static string IconPath(int size) => $"Assets/ElevationPP/ConcretePlatform{size}Icon.png";
    public const string TAB_ICON_PATH = "Assets/ElevationPP/PlatformsTabIcon.png";

    /// <summary>Injects the model and icon for each platform size (call at game init, before any
    /// UI asks for the icon — sprites are cached forever by path).</summary>
    public static void TryInject(DependencyResolver resolver, IEnumerable<int> sizes)
    {
        AssetsDb assetsDb;
        try
        {
            assetsDb = resolver.Resolve<AssetsDb>();
        }
        catch (Exception)
        {
            Log.Info("Elevation++: AssetsDb not available (headless run?); platform models skipped.");
            return;
        }

        tryInjectTabIcon(assetsDb);

        int largest = 0;
        foreach (int size in sizes)
        {
            largest = Math.Max(largest, size);
        }
        foreach (int size in sizes)
        {
            GameObject template = null;
            try
            {
                if (!assetsDb.ContainsAsset(PrefabPath(size)))
                {
                    template = buildTemplate(assetsDb, size);
                    inject(assetsDb, PrefabPath(size), template);
                    s_injectedSizes.Add(size);
                    Log.Info($"Elevation++: concrete platform {size}x{size} model injected.");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Elevation++: failed to inject the {size}x{size} platform model (the platform "
                    + $"falls back to its layout box): {ex}");
            }
            tryInjectIcon(resolver, assetsDb, size, largest, template);
        }
    }

    /// <summary>
    /// The toolbar tab glyph: a white silhouette in the style of the vanilla tab icons — a deck
    /// on two legs with a small building on top — drawn pixel by pixel (no art assets), injected
    /// under <see cref="TAB_ICON_PATH"/> so the tab picks it up like a bundled icon.
    /// </summary>
    private static void tryInjectTabIcon(AssetsDb assetsDb)
    {
        try
        {
            if (assetsDb.ContainsAsset(TAB_ICON_PATH))
            {
                return;
            }
            const int n = 96;
            var icon = new Texture2D(n, n, TextureFormat.ARGB32, mipChain: true)
            {
                name = "ElevationPP_PlatformsTabIcon",
            };
            var pixels = new Color32[n * n];
            var clear = new Color32(255, 255, 255, 0);
            var white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = clear;
            }
            // Coordinates in a 96 grid, y up.
            fill(8, 40, 88, 52);    // deck
            fill(16, 8, 26, 40);    // left leg
            fill(70, 8, 80, 40);    // right leg
            fill(34, 52, 62, 76);   // building on the deck
            fill(42, 76, 54, 88);   // its chimney/roof block
            icon.SetPixels32(pixels);
            icon.Apply(updateMipmaps: true);
            inject(assetsDb, TAB_ICON_PATH, icon);
            Log.Info("Elevation++: platforms tab icon injected.");

            void fill(int x0, int y0, int x1, int y1)
            {
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        pixels[y * n + x] = white;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Elevation++: platforms tab icon injection failed; the tab shows a "
                + $"placeholder: {ex.Message}");
        }
    }

    private static void tryInjectIcon(DependencyResolver resolver, AssetsDb assetsDb, int size,
        int largestSize,
        GameObject template)
    {
        try
        {
            if (assetsDb.ContainsAsset(IconPath(size)))
            {
                return;
            }
            if (template == null
                && !assetsDb.TryGetSharedAsset<GameObject>(PrefabPath(size), out template))
            {
                return;
            }
            Texture2D icon = renderIcon(resolver, template, size, largestSize);
            inject(assetsDb, IconPath(size), icon);
            Log.Info($"Elevation++: concrete platform {size}x{size} icon injected.");
        }
        catch (Exception ex)
        {
            Log.Warning($"Elevation++: platform icon rendering failed; the toolbar shows a "
                + $"placeholder: {ex.Message}");
        }
    }

    /// <summary>Renders the template with the game's own icon-baking renderer (vanilla light
    /// setup). The camera framing is computed from the LARGEST size's footprint for every size,
    /// so the icons show the platforms at their relative sizes (a 1x1 icon is a small block, the
    /// 5x5 fills the frame). Runs at game init, far below the map so nothing else is in frame.</summary>
    private static Texture2D renderIcon(DependencyResolver resolver, GameObject template, int size, int largestSize)
    {
        UnityEngine.Camera mainCamera =
            resolver.Resolve<Mafi.Unity.Camera.CameraController>().Camera;
        if (mainCamera == null)
        {
            throw new InvalidOperationException("main camera not available yet");
        }
        var root = new GameObject("ElevationPP_PlatformIconRenderRoot");
        root.transform.position = new Vector3(0f, -500f, 0f);
        GameObject model = UnityEngine.Object.Instantiate(template);
        model.hideFlags = HideFlags.None;
        model.SetActive(true);
        var renderer = new Mafi.Unity.TexturesGenerators.GameObjectRenderer(root, 20f, mainCamera);
        bool isSetUp = false;
        try
        {
            renderer.SetUpRendering();
            isSetUp = true;
            renderer.SetImageSize(new Vector2i(256, 256));
            renderer.SetCamera(35.Degrees(), 0.Degrees(), 30.Degrees());
            renderer.SetLight(Color.white, 40.Degrees(), 110.Degrees());
            Bounds bounds = model.GetComponentInChildren<MeshRenderer>().bounds;
            float maxSize = Mathf.Max(largestSize * TILE, THICKNESS_TILES * TILE);
            float distance = 1.35f * maxSize * 0.5f / Mathf.Tan(10f * Mathf.Deg2Rad);
            Texture2D rendered = renderer.RenderToTexture(model, bounds.center, distance);
            var icon = new Texture2D(rendered.width, rendered.height, TextureFormat.ARGB32,
                mipChain: true)
            {
                name = $"ElevationPP_ConcretePlatform{size}Icon",
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

    private static GameObject buildTemplate(AssetsDb assetsDb, int size)
    {
        var go = new GameObject($"ElevationPP_ConcretePlatform{size}");
        // Survives scene reloads and stays out of the hierarchy, like a bundle prefab; the
        // model factory clones it and activates the clone.
        go.hideFlags = HideFlags.HideAndDontSave;
        go.SetActive(false);

        float half = size * TILE * 0.5f;
        float height = THICKNESS_TILES * TILE;
        go.AddComponent<MeshFilter>().sharedMesh = buildSlabMesh(half, height, size);
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        if (ConcreteMaterial.TryGet(assetsDb, out Material material))
        {
            renderer.sharedMaterial = material;
        }
        else
        {
            renderer.sharedMaterial = new Material(Shader.Find("Standard"))
            {
                color = new Color(0.62f, 0.62f, 0.6f),
            };
        }
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

        // Pickable like any building (the model factory would otherwise add a layout box).
        BoxCollider collider = go.AddComponent<BoxCollider>();
        collider.size = new Vector3(2f * half, height, 2f * half);
        collider.center = new Vector3(0f, height * 0.5f, 0f);
        return go;
    }

    /// <summary>
    /// A closed box from (-half, 0, -half) to (half, height, half): the pivot is the footprint
    /// centre at the base height, which is where the game places layout-entity models. Flat
    /// shaded via per-face vertices; UVs box-projected in world units at the material's scale.
    /// </summary>
    private static Mesh buildSlabMesh(float half, float height, int size)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        Vector3 min = new Vector3(-half, 0f, -half);
        Vector3 max = new Vector3(half, height, half);

        // Top (+Y), bottom (-Y).
        addQuad(new Vector3(min.x, max.y, min.z), new Vector3(min.x, max.y, max.z),
            new Vector3(max.x, max.y, max.z), new Vector3(max.x, max.y, min.z), Vector3.up);
        addQuad(new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z), Vector3.down);
        // Sides.
        addQuad(new Vector3(min.x, min.y, max.z), new Vector3(max.x, min.y, max.z),
            new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z), Vector3.forward);
        addQuad(new Vector3(max.x, min.y, min.z), new Vector3(min.x, min.y, min.z),
            new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z), Vector3.back);
        addQuad(new Vector3(max.x, min.y, max.z), new Vector3(max.x, min.y, min.z),
            new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z), Vector3.right);
        addQuad(new Vector3(min.x, min.y, min.z), new Vector3(min.x, min.y, max.z),
            new Vector3(min.x, max.y, max.z), new Vector3(min.x, max.y, min.z), Vector3.left);

        var mesh = new Mesh
        {
            name = $"ElevationPP_ConcretePlatform{size}",
        };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;

        // Corners a→b→c→d around the face; cross(b-a, c-a) must equal the outward normal.
        void addQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            int start = vertices.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            vertices.Add(d);
            for (int i = 0; i < 4; i++)
            {
                normals.Add(normal);
            }
            uvs.Add(projectUv(a, normal));
            uvs.Add(projectUv(b, normal));
            uvs.Add(projectUv(c, normal));
            uvs.Add(projectUv(d, normal));
            // Two triangles; with the corner order above, cross(b-a, c-a) is the outward
            // normal — Unity's (left-handed) front-face convention, same as the crossbeam.
            triangles.Add(start);
            triangles.Add(start + 1);
            triangles.Add(start + 2);
            triangles.Add(start);
            triangles.Add(start + 2);
            triangles.Add(start + 3);
        }
    }

    private static Vector2 projectUv(Vector3 p, Vector3 normal)
    {
        float s = ConcreteMaterial.UV_SCALE;
        if (Mathf.Abs(normal.y) > 0.5f)
        {
            return new Vector2(p.x * s, p.z * s);
        }
        if (Mathf.Abs(normal.x) > 0.5f)
        {
            return new Vector2(p.z * s, p.y * s);
        }
        return new Vector2(p.x * s, p.y * s);
    }
}
