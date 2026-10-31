---
name: git-workflow
description: Commit style (no co-author trailer) and how to push ElevationPP to GitHub (GCM quirks)
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 568e6649-c510-41f3-aba1-69f2ce6d94e6
  modified: 2026-08-05T18:38:00.897Z
---

Commit style: do NOT add the "Co-Authored-By: Claude ..." trailer to commits in the
CoI mods repos (monorepo D:\projects\privat\mods\coi\mods).

**Why:** The user explicitly rejected a commit for containing it ("please omit the
co-authored line", 2026-08-01).

**How to apply:** Plain commit messages only; otherwise keep the existing style
(version-prefixed summary + bullet body).

MONOREPO (2026-08-08): D:\projects\privat\mods\coi\mods is the single repo for all CoI
mods, one subdir each (ElevationPP/, ShippingPP/), full histories merged. Origin:
https://github.com/rschurade/COI-mods (the user renamed the former ElevationPP
repo on GitHub 2026-08-08; old URLs redirect). The old standalone checkouts were deleted 2026-08-08; only mods\ exists now.

Pushing to the GitHub repo (rschurade/COI-mods, formerly ElevationPP):
- Plain `git push origin main` works headlessly, but ONLY with the sandbox
  disabled — a sandboxed shell cannot reach the Windows credential store and
  fails with "could not read Username".
- The gh CLI's fine-grained PAT can READ the repo but CANNOT push (403; lacks
  Contents: write). Never use `gh auth setup-git` / the gh credential helper for
  pushing; Git Credential Manager ("manager", system config) is the working path.
- History (resolved 2026-08-01): GCM used to hold two duplicate "rschurade"
  accounts, which forced an account-picker dialog on every push and made
  headless pushes abort. The duplicate (git:https://rschurade@github.com) was
  deleted with the user's approval; a stray gh helper override in the global
  config was removed too. If pushes ever prompt or 403 again, re-check both.
- Related: [[build-command]]

NOTES MIRROR (since 2026-10-31): these memory files are mirrored in the PUBLIC repo at
`docs/dev-notes/` (user chose public; `docs/README.md` explains restoring them on another
machine). Whenever memory changes, copy the files there too and commit with the related work,
so the user's laptop gets them. The user was away for 2+ months from 2026-10-31, working
only on the laptop. Never put secrets in memory — it is published. MissingModsPP source is NOT
in the repo (user copies it by hand); keep it out.
