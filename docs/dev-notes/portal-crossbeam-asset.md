---
name: portal-crossbeam-asset
description: "Rail portal crossbeam ships placeholder graphics; a custom \"long saddle stone\" asset is awaited from a Discord modder"
metadata: 
  node_type: memory
  type: project
  originSessionId: 568e6649-c510-41f3-aba1-69f2ce6d94e6
  modified: 2026-08-02T10:21:01.911Z
---

The ElevationPP rail-portal crossbeam (PortalCrossbeamRenderer.cs) is now a
procedural mesh the user is happy with shape-wise (commit b905563, 2026-08-02):
two "long saddle stone" prisms whose profile was dimensioned interactively against
the vanilla saddles. The ONLY remaining gap is the MATERIAL — currently a flat
warm-gray Standard material (donor textures stripped; they read as riveted metal).
The user: "the only way to improve that is to use an actual material shader." A
custom asset from the COI Discord (asked 2026-08-02) could provide it.

**Why:** the saddle texture cannot be reused from game assets — the saddles are
baked into the pillar tower mesh with one shared material/atlas, UV region unknown.

**How to apply:** when an asset arrives: it must be a Unity AssetBundle built with
Unity 6000.3.19f1, dropped into %APPDATA%\Captain of Industry\Mods\ElevationPP\
AssetBundles\ (+ mafi_bundles.manifest listing the file); the game auto-registers
mod AssetBundles folders. Then change BEAM_PREFAB in PortalCrossbeamRenderer.cs to
the asset's project path and re-tune BEAM_CENTER_DROP/CROSS_SCALE. Spec given to
the modder: 1 tile = 2 Unity units, segment ~2 units long, cross-section ~1.4x0.8,
pivot at segment start, tiles seamlessly; two parallel rows can be rendered from a
single-beam segment. Fallback if no asset: procedural chamfered-box mesh in code
(material matching is the hard part). Related: [[build-command]]
