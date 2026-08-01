using System;
using System.Collections.Generic;
using Mafi;
using Mafi.Collections.ImmutableCollections;
using Mafi.Core;
using Mafi.Core.Entities;
using Mafi.Core.Factory.Zippers;
using Mafi.Core.GameLoop;
using Mafi.Core.Ports.Io;
using Mafi.Unity;
using UnityEngine;

namespace ElevationPP;

/// <summary>
/// Renders the missing bottom stub on pipe connectors whose down port is connected.
///
/// The connector prefab has a riser stub only on top, so a pipe connecting from below (enabled by
/// <see cref="VerticalConnectorPortsPatch"/>) shows a visible gap between the connector body and the
/// bottom tile face. The connector is drawn through the instanced layout-entity pipeline whose
/// per-instance data is position + 90-degree yaw + a horizontal-mirror bit — a vertical flip is not
/// expressible there, so the fix cannot ride on the vanilla renderer.
///
/// Instead, for every constructed pipe connector with a connected bottom port this spawns a plain
/// GameObject copy of the connector prefab, rotated 180 degrees around a horizontal axis through the
/// connector's center (a proper rotation — no mirroring, so triangle winding and backface culling
/// stay correct; the model is 4-way symmetric so the result simply looks like the stub moved to the
/// bottom) and scaled to 97% about that center so the duplicated body and side flanges tuck just
/// inside the original's surfaces instead of z-fighting them. Only the flipped stub protrudes,
/// filling the gap. This works for any combination of connected ports, including top+bottom.
///
/// The set of stubs is refreshed on the game-loop's SyncUpdateEnd (main thread, sim idle, so reading
/// entity/port state and touching GameObjects are both safe), throttled to every few syncs. Stubs
/// appear/disappear within a second of a pipe being connected/removed, and entities that vanish
/// (deconstruction, save load) are cleaned up by the same sweep.
/// </summary>
internal static class VerticalConnectorStubRenderer
{
    private const float SHRINK = 0.97f;
    private const int SYNCS_PER_REFRESH = 5;

    private static Session s_session;

    public static void TryInitialize(DependencyResolver resolver)
    {
        // Drop state of any previous game session (its GameObjects died with the scene).
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
                + "vertical connector stub renderer skipped.");
            return;
        }

        var session = new Session(resolver.Resolve<IEntitiesManager>(), assetsDb);
        resolver.Resolve<IGameLoopEvents>().SyncUpdateEnd.AddNonSaveable(session, session.OnSyncUpdateEnd);
        s_session = session;
        Log.Info("Elevation++: vertical connector stub renderer initialized.");
    }

    private sealed class Session
    {
        private readonly IEntitiesManager m_entitiesManager;
        private readonly AssetsDb m_assetsDb;
        private readonly Dictionary<MiniZipper, GameObject> m_stubs = new Dictionary<MiniZipper, GameObject>();
        private readonly List<MiniZipper> m_toRemoveTmp = new List<MiniZipper>();
        private readonly HashSet<MiniZipper> m_neededTmp = new HashSet<MiniZipper>();
        private int m_syncCounter;
        private bool m_errorLogged;

        public Session(IEntitiesManager entitiesManager, AssetsDb assetsDb)
        {
            m_entitiesManager = entitiesManager;
            m_assetsDb = assetsDb;
        }

        public void Clear()
        {
            foreach (GameObject go in m_stubs.Values)
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
            m_stubs.Clear();
        }

        public void OnSyncUpdateEnd(GameTime time)
        {
            if (++m_syncCounter % SYNCS_PER_REFRESH != 0)
            {
                return;
            }
            try
            {
                refreshStubs();
            }
            catch (Exception ex)
            {
                if (!m_errorLogged)
                {
                    m_errorLogged = true;
                    Log.Error($"Elevation++: vertical connector stub refresh failed (logged once): {ex}");
                }
            }
        }

        private void refreshStubs()
        {
            m_neededTmp.Clear();
            foreach (IEntity entity in m_entitiesManager.Entities)
            {
                if (entity is MiniZipper zipper && zipper.IsConstructed && hasConnectedBottomPort(zipper))
                {
                    m_neededTmp.Add(zipper);
                    if (!m_stubs.ContainsKey(zipper))
                    {
                        m_stubs.Add(zipper, createStub(zipper));
                    }
                }
            }

            m_toRemoveTmp.Clear();
            foreach (KeyValuePair<MiniZipper, GameObject> pair in m_stubs)
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
            foreach (MiniZipper gone in m_toRemoveTmp)
            {
                m_stubs.Remove(gone);
            }
        }

        private static bool hasConnectedBottomPort(MiniZipper zipper)
        {
            ImmutableArray<IoPort>.Enumerator enumerator = zipper.Ports.GetEnumerator();
            while (enumerator.MoveNext())
            {
                IoPort port = enumerator.Current;
                if (port.Direction.DirectionVector.Z < 0 && port.IsConnected)
                {
                    return true;
                }
            }
            return false;
        }

        private GameObject createStub(MiniZipper zipper)
        {
            string prefabPath = zipper.Prototype.Graphics.PrefabPath;
            if (!m_assetsDb.TryGetSharedAsset<GameObject>(prefabPath, out GameObject prefab))
            {
                Log.Warning($"Elevation++: connector prefab '{prefabPath}' not found, no bottom stub.");
                return null;
            }

            // Same anchor the instanced renderer uses (PrefabOrigin is zero for mini-zippers), so the
            // copy starts exactly where the vanilla model is drawn.
            Vector3 pos = zipper.Prototype.Layout.GetModelOrigin(zipper.Transform).ToVector3();
            GameObject go = UnityEngine.Object.Instantiate(prefab, pos, Quaternion.identity);
            go.name = "ElevationPP_ConnectorBottomStub";

            // Rendering-only copy: no colliders (would block picking/selection) and no scripts.
            // Colliders are matched by type name to avoid referencing UnityEngine.PhysicsModule.
            foreach (Component component in go.GetComponentsInChildren<Component>(includeInactive: true))
            {
                if (component is MonoBehaviour || component.GetType().Name.EndsWith("Collider"))
                {
                    UnityEngine.Object.Destroy(component);
                }
            }

            // GetModelOrigin is the layout's XY center at base height, so the connector's volumetric
            // center is only half a tile (1 Unity unit) straight up from it. Flip upside down by a
            // 180-degree rotation about a horizontal axis through that center, then shrink about the
            // same center.
            Vector3 center = pos + new Vector3(0f, 1f, 0f);
            go.transform.RotateAround(center, Vector3.right, 180f);
            go.transform.localScale *= SHRINK;
            go.transform.position = center + (go.transform.position - center) * SHRINK;
            return go;
        }
    }
}
