using System;
using System.Collections.Generic;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.GameLoop;
using Mafi.Core.Trains;
using Mafi.Unity;
using UnityEngine;

namespace ElevationPP;

/// <summary>
/// Renders a crossbeam between the two side pillars of a rail portal (placed by
/// <see cref="SideTrackPillarsPatch"/>), visually carrying the deck(s) that run over it.
///
/// The beam imitates "long versions" of the saddle stones found on rail pillar tops: two
/// parallel chamfered concrete prisms (one per saddle row), each sitting on a thin darker base
/// strip — the same silhouette as the vanilla saddle blocks, stretched from column to column.
/// The saddle geometry cannot be reused from game assets (it is baked into the pillar tower mesh
/// with a single atlas material), so the mesh is generated procedurally as one seamless piece
/// per portal, and the material is a clone of the game's transport-pillar concrete material
/// (tinted towards the saddles' darker gray), so lighting and texture stay native.
///
/// A portal is detected purely from sim state: a (track, block) that owns exactly two pillars
/// standing at different positions. Tracks adopted by the same portal hold co-located duplicate
/// pairs, so beams are deduplicated by their (rounded) endpoint positions — one beam per
/// physical pair of columns. The set of beams is refreshed on SyncUpdateEnd (main thread, sim
/// idle), throttled to every few syncs; beams appear/disappear within a second of portals being
/// placed or removed, and stale ones are swept when entities vanish.
/// </summary>
internal static class PortalCrossbeamRenderer
{
    private const int SYNCS_PER_REFRESH = 5;

    // Donor for the beam MATERIAL (not geometry): the transport pillar's concrete base.
    private const string MATERIAL_SOURCE_PREFAB = "Assets/Base/Transports/Pillars/Base.prefab";

    // Seamlessly tiling concrete maps from the game's terrain surface set — unlike the model
    // atlases these are made to repeat under arbitrary UVs, which is exactly what the
    // procedural beam mesh needs.
    private const string CONCRETE_ALBEDO =
        "Assets/Base/Terrain/Surfaces/Concrete/concreteBlock1a-256-albedo.png";
    private const string CONCRETE_NORMALS =
        "Assets/Base/Terrain/Surfaces/Concrete/concreteBlock1a-256-normals.png";

    // Saddle-beam proportions, in Unity units (1 tile = 2 units). Cross-section per prism:
    // BLOCK_WIDTH wide, BLOCK_HEIGHT tall with CHAMFER on the two top edges, on a base strip
    // PLATE_HEIGHT tall and PLATE_EXTRA wider on each side. Two prisms ROW_SPACING apart
    // (centre to centre) mirror the two saddle rows on a pillar top.
    private const float BLOCK_WIDTH = 1.05f;
    private const float ROW_SPACING = 1.74f;

    // Block skirt shape (all measured down from the block top). The side faces run vertical to
    // the shoulder, then chamfer inward to the bottom edge. The outer side's shoulder sits
    // higher than the inner one, matching the vanilla saddle stones.
    private const float BOTTOM_DROP = 0.495f;
    private const float BOTTOM_INSET_INNER = 0.33f;
    private const float BOTTOM_INSET_OUTER = 0.34f;
    private const float SHOULDER_DROP_INNER = 0.345f;
    private const float SHOULDER_DROP_OUTER = 0.345f;

    // Central V-notch in the block's underside, arching over the roller pin (points 5-6-7 of
    // the reference profile). Narrow enough to leave a small flat bottom strip between the
    // notch base and the bottom corners; slightly asymmetric per the reference.
    private const float NOTCH_HALF_OUTER = 0.11f;
    private const float NOTCH_HALF_INNER = 0.1f;
    private const float NOTCH_RISE = 0.12f;

    // Vertical placement of the beam top relative to the pillar top: negative raises it above,
    // so the beam covers the pillars' own saddle stones instead of them poking through it.
    private const float TOP_DROP = -0.27f;

    // How far past each column centre the beam runs — far enough to swallow the pillar's
    // whole outer saddle stone without poking past the pillar cap.
    private const float END_EXTEND = 1.1f;

    // UV scale: texture repeats every 1/UV_SCALE units.
    private const float UV_SCALE = 1f;

    // The concrete flooring texture is a 4x4 grid of blocks (grooves every 1/4 of the image).
    // The beam uses only the groove-free interior of one block: a patch starting at this
    // fraction into the image, PATCH_SIZE of the image wide/tall. The patch copy wraps in
    // MIRROR mode so repeats stay continuous (mirroring is invisible on noisy concrete).
    private const float PATCH_START = 70f / 256f;
    private const float PATCH_SIZE = 52f / 256f;

    // Tint multiplied over the concrete albedo (white = texture as authored).
    private static readonly Color BLOCK_COLOR = Color.white;

    private static Session s_session;

    public static void TryInitialize(DependencyResolver resolver)
    {
        s_session?.Clear();
        s_session = null;

        AssetsDb assetsDb;
        try
        {
            assetsDb = resolver.Resolve<AssetsDb>();
        }
        catch (Exception)
        {
            Log.Info("Elevation++: AssetsDb not available (headless run?), "
                + "portal crossbeam renderer skipped.");
            return;
        }

        var session = new Session(resolver.Resolve<IEntitiesManager>(), assetsDb);
        resolver.Resolve<IGameLoopEvents>().SyncUpdateEnd.AddNonSaveable(session, session.OnSyncUpdateEnd);
        s_session = session;
        Log.Info("Elevation++: portal crossbeam renderer initialized.");
    }

    private sealed class Session
    {
        private readonly IEntitiesManager m_entitiesManager;
        private readonly AssetsDb m_assetsDb;
        private readonly Dictionary<string, GameObject> m_beams = new Dictionary<string, GameObject>();
        private readonly HashSet<string> m_neededTmp = new HashSet<string>();
        private readonly List<string> m_toRemoveTmp = new List<string>();
        private readonly Dictionary<long, List<TrainTrackPillar>> m_groupsTmp
            = new Dictionary<long, List<TrainTrackPillar>>();
        private Material m_blockMaterial;
        private int m_syncCounter;
        private bool m_errorLogged;

        public Session(IEntitiesManager entitiesManager, AssetsDb assetsDb)
        {
            m_entitiesManager = entitiesManager;
            m_assetsDb = assetsDb;
        }

        public void Clear()
        {
            foreach (GameObject go in m_beams.Values)
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
            m_beams.Clear();
        }

        public void OnSyncUpdateEnd(GameTime time)
        {
            if (++m_syncCounter % SYNCS_PER_REFRESH != 0)
            {
                return;
            }
            try
            {
                refreshBeams();
            }
            catch (Exception ex)
            {
                if (!m_errorLogged)
                {
                    m_errorLogged = true;
                    Log.Error($"Elevation++: portal crossbeam refresh failed (logged once): {ex}");
                }
            }
        }

        private void refreshBeams()
        {
            m_groupsTmp.Clear();
            foreach (IEntity entity in m_entitiesManager.Entities)
            {
                if (!(entity is TrainTrackPillar pillar) || !pillar.IsConstructed)
                {
                    continue;
                }
                long key = ((long)pillar.TrainTrack.Id.Value << 8) | (uint)(pillar.BlockIndex & 0xFF);
                if (!m_groupsTmp.TryGetValue(key, out List<TrainTrackPillar> group))
                {
                    m_groupsTmp.Add(key, group = new List<TrainTrackPillar>(2));
                }
                group.Add(pillar);
            }

            m_neededTmp.Clear();
            foreach (List<TrainTrackPillar> group in m_groupsTmp.Values)
            {
                if (group.Count != 2)
                {
                    continue;
                }
                Vector3 a = pillarTop(group[0]);
                Vector3 b = pillarTop(group[1]);
                if ((a - b).sqrMagnitude < 1f)
                {
                    continue; // co-located or a plain single-position block — not a portal
                }
                string key = beamKey(a, b);
                m_neededTmp.Add(key);
                if (!m_beams.ContainsKey(key))
                {
                    m_beams.Add(key, createBeam(a, b));
                }
            }

            m_toRemoveTmp.Clear();
            foreach (KeyValuePair<string, GameObject> pair in m_beams)
            {
                if (!m_neededTmp.Contains(pair.Key))
                {
                    if (pair.Value != null)
                    {
                        UnityEngine.Object.Destroy(pair.Value);
                    }
                    m_toRemoveTmp.Add(pair.Key);
                }
            }
            foreach (string gone in m_toRemoveTmp)
            {
                m_beams.Remove(gone);
            }
        }

        /// <summary>Unity-space point at the top center of the pillar's column.</summary>
        private static Vector3 pillarTop(TrainTrackPillar pillar)
        {
            Vector3 basePos = pillar.PillarPosition.ToVector3();
            return new Vector3(basePos.x, basePos.y + pillar.Height.Value.ToFloat() * 2f, basePos.z);
        }

        /// <summary>Canonical key for an unordered endpoint pair, tolerant to tiny numeric
        /// differences between the co-located pairs of adopted tracks.</summary>
        private static string beamKey(Vector3 a, Vector3 b)
        {
            string ka = $"{a.x:F0}_{a.y:F0}_{a.z:F0}";
            string kb = $"{b.x:F0}_{b.y:F0}_{b.z:F0}";
            return string.CompareOrdinal(ka, kb) <= 0 ? ka + "|" + kb : kb + "|" + ka;
        }

        private GameObject createBeam(Vector3 a, Vector3 b)
        {
            if (!tryGetMaterials())
            {
                return null;
            }
            Vector3 axis = b - a;
            axis.y = 0f;
            float span = axis.magnitude;
            Vector3 direction = axis / span;

            var go = new GameObject("ElevationPP_PortalCrossbeam");
            // Anchor one overhang-length before column A, so the beam extends symmetrically past
            // both column centres.
            Vector3 anchor = a - direction * END_EXTEND;
            go.transform.position = new Vector3(anchor.x, Mathf.Min(a.y, b.y) - TOP_DROP, anchor.z);
            // Yaw-only rotation mapping local +X onto the beam direction. (FromToRotation is
            // unusable here: for directions near -X it picks an arbitrary 180-degree axis and
            // can roll the beam upside down.)
            float yawDegrees = Mathf.Atan2(-direction.z, direction.x) * Mathf.Rad2Deg;
            go.transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);

            Mesh mesh = buildBeamMesh(span + 2f * END_EXTEND);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = m_blockMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            return go;
        }

        private bool tryGetMaterials()
        {
            if (m_blockMaterial != null)
            {
                return true;
            }
            if (!m_assetsDb.TryGetSharedAsset<GameObject>(MATERIAL_SOURCE_PREFAB, out GameObject prefab))
            {
                Log.Warning($"Elevation++: material donor '{MATERIAL_SOURCE_PREFAB}' not found, no crossbeam.");
                return false;
            }
            MeshRenderer donor = prefab.GetComponentInChildren<MeshRenderer>(includeInactive: true);
            if (donor == null || donor.sharedMaterial == null)
            {
                Log.Warning("Elevation++: material donor has no renderer/material, no crossbeam.");
                return false;
            }
            logMaterialInfo(donor.sharedMaterial);
            m_blockMaterial = flatConcrete(donor.sharedMaterial, BLOCK_COLOR);
            return true;
        }

        /// <summary>
        /// Clones the donor material (a plain Unity Standard material), strips its atlas/rivet
        /// textures and swaps in the game's seamlessly tiling terrain-concrete maps, which work
        /// under the beam's box-projected UVs. Falls back to a flat matte color when the
        /// textures are unavailable.
        /// </summary>
        private Material flatConcrete(Material source, Color color)
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
            if (m_assetsDb.TryGetSharedAsset<Texture2D>(CONCRETE_ALBEDO, out Texture2D albedo))
            {
                material.SetTexture("_MainTex", makeSeamlessInterior(albedo, linear: false));
            }
            else
            {
                Log.Warning($"Elevation++: concrete albedo '{CONCRETE_ALBEDO}' not found, beam stays flat-colored.");
            }
            if (m_assetsDb.TryGetSharedAsset<Texture2D>(CONCRETE_NORMALS, out Texture2D normals))
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
                    $"Elevation++: beam donor material '{source.name}' shader '{source.shader.name}':");
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

        /// <summary>
        /// Builds the beam mesh in local space: X along the beam (0..length), the beam TOP at
        /// y = 0, centred on Z. Two chamfered prisms at ±ROW_SPACING/2. Flat-shaded via
        /// per-face vertices.
        /// </summary>
        private static Mesh buildBeamMesh(float length)
        {
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var blockTris = new List<int>();

            foreach (float zc in new[] { -ROW_SPACING * 0.5f, ROW_SPACING * 0.5f })
            {
                addChamferedPrism(vertices, uvs, blockTris, length, zc, outerSign: Mathf.Sign(zc));
            }

            var mesh = new Mesh
            {
                name = "ElevationPP_PortalBeam",
            };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(blockTris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void addChamferedPrism(List<Vector3> vertices, List<Vector2> uvs,
            List<int> triangles, float length, float zc, float outerSign)
        {
            float w = BLOCK_WIDTH * 0.5f;
            // Anvil profile like the vanilla saddle stones: full width at the TOP, chamfered
            // inward toward the bottom, with the outer side's shoulder sitting higher. Corners
            // counter-clockwise seen from +X (start cap).
            float shoulderRight = -(outerSign > 0f ? SHOULDER_DROP_OUTER : SHOULDER_DROP_INNER);
            float shoulderLeft = -(outerSign > 0f ? SHOULDER_DROP_INNER : SHOULDER_DROP_OUTER);
            float insetRight = outerSign > 0f ? BOTTOM_INSET_OUTER : BOTTOM_INSET_INNER;
            float insetLeft = outerSign > 0f ? BOTTOM_INSET_INNER : BOTTOM_INSET_OUTER;
            float notchRight = outerSign > 0f ? NOTCH_HALF_OUTER : NOTCH_HALF_INNER;
            float notchLeft = outerSign > 0f ? NOTCH_HALF_INNER : NOTCH_HALF_OUTER;
            // Numbered per the reference profile sketch (1/2 top corners, 3/9 shoulders, 4/8
            // bottom corners, 5-6-7 the central notch over the roller). Counter-clockwise, and
            // deliberately STARTING at the notch peak: the end-cap fan is built from the first
            // point, and the peak is the only vertex that sees the whole (concave) profile.
            var p = new Vector2[]
            {
                new Vector2(zc, -BOTTOM_DROP + NOTCH_RISE),                // 6 notch peak
                new Vector2(zc + notchRight, -BOTTOM_DROP),                // 5
                new Vector2(zc + w - insetRight, -BOTTOM_DROP),            // 4 bottom right
                new Vector2(zc + w, shoulderRight),                        // 3 right shoulder
                new Vector2(zc + w, 0f),                                   // 2 top right
                new Vector2(zc - w, 0f),                                   // 1 top left
                new Vector2(zc - w, shoulderLeft),                         // 9 left shoulder
                new Vector2(zc - w + insetLeft, -BOTTOM_DROP),             // 8 bottom left
                new Vector2(zc - notchLeft, -BOTTOM_DROP),                 // 7
            };
            addProfilePrism(vertices, uvs, triangles, p, length);
        }

        /// <summary>Extrudes a counter-clockwise cross-section profile (in (z, y), as seen from
        /// +X) along the beam's X axis, with end caps. The caps are triangulated as a fan from
        /// the FIRST profile point, so the profile must be star-shaped as seen from it (any
        /// convex profile qualifies; for concave ones start at a point that sees all
        /// others).</summary>
        private static void addProfilePrism(List<Vector3> vertices, List<Vector2> uvs,
            List<int> triangles, Vector2[] p, float length)
        {
            for (int i = 0; i < p.Length; i++)
            {
                Vector2 e0 = p[i];
                Vector2 e1 = p[(i + 1) % p.Length];
                addQuad(vertices, uvs, triangles,
                    new Vector3(0f, e0.y, e0.x), new Vector3(length, e0.y, e0.x),
                    new Vector3(length, e1.y, e1.x), new Vector3(0f, e1.y, e1.x));
            }
            addCap(vertices, uvs, triangles, p, 0f, flip: false);
            addCap(vertices, uvs, triangles, p, length, flip: true);
        }

        private static void addQuad(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles,
            Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
        {
            int baseIndex = vertices.Count;
            vertices.Add(v0);
            vertices.Add(v1);
            vertices.Add(v2);
            vertices.Add(v3);
            // Project UVs along the quad's dominant axes.
            Vector3 side = v1 - v0;
            Vector3 up = v3 - v0;
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(side.magnitude * UV_SCALE, 0f));
            uvs.Add(new Vector2(side.magnitude * UV_SCALE, up.magnitude * UV_SCALE));
            uvs.Add(new Vector2(0f, up.magnitude * UV_SCALE));
            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 3);
        }

        private static void addCap(List<Vector3> vertices, List<Vector2> uvs, List<int> triangles,
            Vector2[] profile, float x, bool flip)
        {
            int baseIndex = vertices.Count;
            foreach (Vector2 point in profile)
            {
                vertices.Add(new Vector3(x, point.y, point.x));
                uvs.Add(new Vector2(point.x * UV_SCALE, point.y * UV_SCALE));
            }
            for (int i = 1; i < profile.Length - 1; i++)
            {
                triangles.Add(baseIndex);
                if (flip)
                {
                    triangles.Add(baseIndex + i + 1);
                    triangles.Add(baseIndex + i);
                }
                else
                {
                    triangles.Add(baseIndex + i);
                    triangles.Add(baseIndex + i + 1);
                }
            }
        }
    }
}
