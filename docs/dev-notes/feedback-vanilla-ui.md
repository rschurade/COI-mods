---
name: feedback-vanilla-ui
description: "User wants mod UI to copy vanilla managers/windows, not custom panels — consistency beats convenience"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 568e6649-c510-41f3-aba1-69f2ce6d94e6
  modified: 2026-08-06T07:19:33.490Z
---

For CoI mod UI, mirror the vanilla equivalent (manager windows, pickers, interaction patterns) instead of inventing simpler custom panels — even when that is more work.

**Why:** In [[shippingpp-mod]] the first line-management UI (panels inside the terminal window) worked but was rejected: "copy the train line and network manager, yes that's more work but is more consistent with everything else." The user also flagged dropdown-based stop adding vs. vanilla's click-in-world selection as a (tolerated) inconsistency, and asked for line renaming because vanilla lines have it.

**How to apply:** Before building mod UI, find the vanilla feature closest to it, research its window/component structure (WindowController + toolbar registration is fully mod-usable via [GlobalDependency] auto-scan), and replicate its layout and interactions. Vanilla components (TitleWithRename, TabContainer, ConstructionUi, Dropdown, ButtonRow, PanelWithHeader, Panel, PanelFooterRow, ColorSplit, WarningLabel, Reorderable drag manipulator, VerticalSingleTrainLineDiagramUi, EntitiesRenderingManager.AddHighlight) are reusable directly. Lessons from the lines-manager redesign (2026-08-06, three user corrections): (1) swapping in vanilla widgets is NOT copying the design — replicate the actual layout tree of the vanilla window (read its decompiled ctor top-down); (2) section backgrounds come from Panel/PanelWithHeader components, never hand-rolled Background() containers — a window body area with no Panel looks like raw diamond-plate and is wrong; (3) TabContainer.Add defaults to a content-sized scroll wrapper — pass Scroll.No + FlexGrow for tabs that must fill the window.
