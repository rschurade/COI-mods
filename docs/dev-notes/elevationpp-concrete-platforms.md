---
name: elevationpp-concrete-platforms
description: ElevationPP concrete platforms — 0.9.10 released 2026-08-23; 0.9.11 fixed orphan pillars on removal; 0.9.12 (2026-09-21) fixed lifts/elevatables on decks collapsing
metadata:
  type: project
---

Concrete platforms (elevatable 1x1..5x5 slabs buildings can stand on; PlatformSupport rule,
Harmony gates, cursor snap, deck-based belt/pipe pillars, over-water) shipped in
ElevationPP 0.9.10 on 2026-08-23 together with the shift-straight-line fix in
LongTransportPathFinder and the shared ConcreteMaterial for crossbeams + platforms.
Commit dc05f56 on rschurade/COI-mods main; zip at %APPDATA%\Captain of Industry\Mods\ElevationPP_0.9.10.zip.

0.9.11 (2026-08-27, commit 9c1274d) fixed platform support pillars staying behind on
cut/delete/deconstruct: vanilla TransportsManager.entityRemoved only re-checks pillars for
protos with CanPillarsPassThrough == true (the platform says false for deck-based belt
pillars), so PlatformPillarPatch now queues the platform's ground pillars into
m_pillarsToCheck itself. Forum Q "do platforms hold dumped material?" — no, engine terrain
is a heightmap (one height + material layer stack per tile), tunnels impossible.

0.9.12 (2026-09-21, commit ff3c896) fixed elevatable structures (lifts, balancers,
connectors — ILayoutEntityProtoWithElevation) on a deck being demolished: the vanilla
watchdog only grants suspended tolerance when a TransportPillar sits in the tile below the
base, which on a platform is the deck. PlatformSupport.IsElevatedRestingOnDeck now counts
deck-rested UsingPillar tiles as supported; platform removal rechecks elevatables too.

**Why:** earlier note said round 2 was untested/uncommitted — that is resolved.
**How to apply:** future platform work is incremental on a released feature; watch user
reports for platform issues. Related: [[build-command]], [[git-workflow]].

0.9.13 RELEASED 2026-10-29 (commit `7bd98e0`, tag `ElevationPP-0.9.13` — first ElevationPP tag; pushed; min/max game 0.8.8). Game 0.8.8 broke three things: (1) TrainStationModuleProto ctor gained isRadiationShielded + transferAtWagonEndOfAnimation (pass v.IsRadiationShielded / v.FinishPassBeforeDisconnect, both public FIELDS); (2) new vanilla TrainStationShielded(_ELEC) layout uses a custom `|n|` token (sunken pit, CustomLayoutToken defined only in vanilla's own ParseLayoutOrThrow call) → our footprint re-parse throws; now every elevated clone goes through `tryRegister` (skip + warning) and the shielded station has NO elevated version (user may want one later: pit is 2 tiles below ground); (3) TrainTrackPillarsRenderer.AddPillar now takes private enum PillarKind {Constructed, Preview, PlacedBlueprint} instead of bool + a third blueprint renderer list; RailPillarRenderPatch reads kind from `object[] __args` and calls vanilla getRenderer/packRendererAndInstanceIds via reflection (don't mirror private bit layouts). Arch/portal UX reminder (user forgot it): while BUILDING track portals are automatic at the closest stance (no modifiers — Shift/Ctrl/Alt there belong to the vanilla track tool); only the vanilla add/remove-pillars tool uses Shift (closest) / Ctrl +2 / Ctrl+Shift +4 / Alt +6 / Alt+Shift +8 tiles.
