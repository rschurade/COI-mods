---
name: modconfigpp-mod
description: "ModConfig++ mod (in-game editor for any mod's settings) at D:\\projects\\privat\\coi\\mods\\ModConfigPP; 0.1.0 tested & committed 2026-09-08"
metadata: 
  node_type: memory
  type: project
  originSessionId: 519a8531-c89b-4fec-8472-9f0ee2e8aed4
  modified: 2026-09-08T07:08:39.214Z
---

ModConfig++ (`ModConfigPP`) lives in the mods monorepo — see [[git-workflow]]; built via its
own build.ps1 ([[build-command]] pattern). v0.1.0 tested in-game, committed a87736b 2026-09-08.

What it does: vanilla enables a mod's settings button only while the mod is NOT yet in the
selected save (ModTile in Mafi.Unity: `Enabled(SaveName.IsNone || ModsInSaveFile.All(id !=))`);
afterwards no UI can change mod settings. ModConfig++ patches the GameEscapeMenu ctor (postfix
adds a "Mod settings" button above the seed/version block) opening a window that edits the LIVE
ModJsonConfig of every installed mod via the vanilla ModJsonConfigPanel.

Key mechanics: GameBuilder.AttachModJsonConfigs swaps each mod's own JsonConfig (subscribers
preserved) into the configs array, so `resolver.ResolveAll<IConfig>()` + filter yields the live
instances (ResolveAll needs [MultiDependency], which IConfig has but ModJsonConfig does NOT —
ResolveAll<ModJsonConfig> throws). TrySetValue fires OnValueChanged (Elevation++ repatches
live); vanilla ResetToDefaults fires NO events, so restore re-sets params via TrySetValue.
Values persist in the save automatically. The feature was first drafted inside ElevationPP
0.9.12 (never released) and extracted; ElevationPP stayed at 0.9.11. A third-party
"ModConfigEditor" mod exists (awkward UX, closed source) — the user has it in the Mods folder;
both patch the escape menu.
