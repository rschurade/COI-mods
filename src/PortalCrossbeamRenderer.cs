using System;
using System.Collections.Generic;
using System.Globalization;
using Mafi;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.GameLoop;
using Mafi.Core.Trains;
using Mafi.Unity;
using UnityEngine;

namespace ElevationPP;

/// <summary>
/// Renders a concrete crossbeam between the two side pillars of a rail portal (placed by
/// <see cref="SideTrackPillarsPatch"/>), visually carrying the deck(s) that run over it.
///
/// No custom art: the beam is assembled from copies of the game's own transport-pillar concrete
/// segment prefab, rotated to lie horizontally and tiled from column to column — the same
/// clone-a-prefab technique as <see cref="VerticalConnectorStubRenderer"/>. The segments render
/// with their real materials, so the beam matches the game's concrete look and lighting.
///
/// A portal is detected purely from sim state: a (track, block) that owns exactly two pillars
/// standing at different positions. Tracks adopted by the same portal hold co-located duplicate
/// pairs, so beams are deduplicated by their (rounded) endpoint positions — one beam per physical
/// pair of columns. The set of beams is refreshed on SyncUpdateEnd (main thread, sim idle),
/// throttled to every few syncs; beams appear/disappear within a second of portals being placed
/// or removed, and stale ones are swept when entities vanish.
/// </summary>
internal static class PortalCrossbeamRenderer
{
    private const int SYNCS_PER_REFRESH = 5;

    // The tileable concrete piece used for beam segments. Candidates (all logged at first use):
    // - Transports/Pillars/Pillars.prefab: steel lattice segment (reads flimsy for a rail portal)
    // - Transports/Pillars/Base.prefab: solid concrete foundation block
    // - Transports/Pillars/PillarsWithFills.prefab, XFill.prefab: lattice with cross braces
    // - Trains/Pillars/Pillar-base.prefab: the rail pillar's stone plinth
    private const string BEAM_PREFAB = "Assets/Base/Transports/Pillars/Base.prefab";

    private static readonly string[] CANDIDATE_PREFABS =
    {
        "Assets/Base/Transports/Pillars/Pillars.prefab",
        "Assets/Base/Transports/Pillars/Base.prefab",
        "Assets/Base/Transports/Pillars/PillarsWithFills.prefab",
        "Assets/Base/Transports/Pillars/XFill.prefab",
        "Assets/Base/Trains/Pillars/Pillar-base.prefab",
        "Assets/Base/Trains/Pillars/Pillar.prefab",
    };

    // How far below the pillar top the beam's spine runs, in Unity units. The pillar top is the
    // deck underside; the segment's cross-section extends ±0.98 * CROSS_SCALE around the spine,
    // so the top edge tucks into the deck box while the underside stays high enough to clear the
    // catenary of an electrified track crossing 4 tiles below.
    private const float BEAM_CENTER_DROP = 0.6f;

    // Slims the segment's full-tile (1.96 units) cross-section so the beam reads as a girder and
    // gains extra clearance underneath.
    private const float CROSS_SCALE = 0.75f;

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
        private int m_syncCounter;
        private bool m_errorLogged;
        private bool m_prefabLogged;

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
            // Group constructed pillars by (track, block).
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
            if (!m_assetsDb.TryGetSharedAsset<GameObject>(BEAM_PREFAB, out GameObject prefab))
            {
                Log.Warning($"Elevation++: beam prefab '{BEAM_PREFAB}' not found, no crossbeam.");
                return null;
            }
            logPrefabsOnce();

            // Segment length = the mesh's extent along its (pre-rotation) vertical axis, so any
            // prefab tiles seamlessly regardless of its authored size.
            float step = 2f;
            foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(includeInactive: true))
            {
                if (filter.sharedMesh != null)
                {
                    step = Mathf.Max(0.4f, filter.sharedMesh.bounds.size.y * 0.98f);
                    break;
                }
            }

            var root = new GameObject("ElevationPP_PortalCrossbeam");
            Vector3 axis = b - a;
            axis.y = 0f;
            float length = axis.magnitude;
            Vector3 direction = axis / length;
            // Lay the (vertical) segment prefab on its side along the beam axis.
            Quaternion rotation = Quaternion.FromToRotation(Vector3.up, direction);
            float y = ((a.y + b.y) * 0.5f) - BEAM_CENTER_DROP;

            int segmentCount = Mathf.Max(1, Mathf.CeilToInt(length / step));
            for (int i = 0; i < segmentCount; i++)
            {
                Vector3 start = a + direction * (i * step);
                start.y = y;
                GameObject segment = UnityEngine.Object.Instantiate(prefab, start, rotation, root.transform);
                // Local y runs along the beam; x/z are the cross-section regardless of rotation.
                segment.transform.localScale = new Vector3(CROSS_SCALE, 1f, CROSS_SCALE);
                foreach (Component component in segment.GetComponentsInChildren<Component>(includeInactive: true))
                {
                    if (component is MonoBehaviour || component.GetType().Name.EndsWith("Collider"))
                    {
                        UnityEngine.Object.Destroy(component);
                    }
                }
            }
            return root;
        }

        private void logPrefabsOnce()
        {
            if (m_prefabLogged)
            {
                return;
            }
            m_prefabLogged = true;
            try
            {
                var sb = new System.Text.StringBuilder("Elevation++: beam candidate prefabs:");
                foreach (string path in CANDIDATE_PREFABS)
                {
                    if (!m_assetsDb.TryGetSharedAsset<GameObject>(path, out GameObject prefab))
                    {
                        sb.Append($"\n  {path}: NOT FOUND");
                        continue;
                    }
                    sb.Append($"\n  {path}:");
                    foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(includeInactive: true))
                    {
                        Bounds bounds = filter.sharedMesh != null ? filter.sharedMesh.bounds : default(Bounds);
                        sb.Append($"\n    '{filter.gameObject.name}' mesh='{(filter.sharedMesh != null ? filter.sharedMesh.name : "null")}'"
                            + $" center={bounds.center} size={bounds.size}"
                            + $" localPos={filter.transform.localPosition} localScale={filter.transform.localScale}");
                    }
                }
                Log.Info(sb.ToString());
            }
            catch (Exception)
            {
                // Diagnostics only.
            }
        }
    }
}
