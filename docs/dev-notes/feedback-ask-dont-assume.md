---
name: feedback-ask-dont-assume
description: "Ask when unclear instead of implementing a guess; and find the game's existing mechanism before offering design options"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 61877661-7268-4877-88af-dc5912a7d821
  modified: 2026-08-07T09:42:42.939Z
---

When something about a requested feature is unclear, ASK before implementing. And when
offering the user a choice, first research how the base game already does that thing —
options must include the vanilla mechanism, not just inventions.

**Why:** 2026-08-07, [[shippingpp-mod]] ship selling. The user said the refund should go
into "terminal storage". Instead of finding out how CoI disposes of stray materials, I
offered a menu of my own inventions (push into the terminal's cargo modules / register
output buffers) and built the module version. Terminal cargo modules only accept the one
product each is assigned, so construction materials never fit and the panel reported the
entire refund as lost. The user's reaction: "why are you making things up... refund into
shipyard, do you hear me?" The correct mechanism already existed and was easy to find:
`IAssetTransactionManager.StoreProduct(pq, reason)` fills matching global receivers and
drops the remainder into the shipyard via `IOverflowProductsStorage` — the standard
overflow path for every stray material in the game.

Worse, I had already noticed the design was wrong ("this will silently drop the entire
refund"), said so, and built it anyway under a stated assumption. A noticed-but-shipped
flaw is not a delivered feature.

**How to apply:** Same principle as [[feedback-vanilla-ui]] but for game logic, not just
UI — grep the decompiled sources (D:\projects\privat\mods\coi\decompiled) for the vanilla
mechanism BEFORE designing. If a question's options are all my own inventions, that is the
signal I have not researched enough to ask a good question yet. If a design still looks
wrong after the user picks, stop and say so rather than building it under an assumption.
