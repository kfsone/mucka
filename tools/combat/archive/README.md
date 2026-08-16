# Archive

Superseded design drafts, kept only because a still-live document cites them as the historical
justification for a decision or a shipped constant. Not living references — do not implement
against anything here; check the citing document (DESIGN_FINAL.md, Audio/CombatMetronome.cs) for
what actually shipped.

- `DESIGN_LIVE_A.md`, `DESIGN_LIVE_B.md`, `UX_PROPOSAL.md` — the two competing drafts and the
  original proposal that DESIGN_FINAL.md reconciled into the real spec. Cited by DESIGN_FINAL.md's
  own decision table (D2, D6, D7) as the reasoning behind specific settled decisions.
- `TICK-PHASE-REVIEW.md`, `analyze_tick_phase.py` — the empirical tick-timing analysis (arrival
  jitter percentiles) that `Audio/CombatMetronome.cs`'s shipped constants (`TrailMilliseconds`,
  etc.) cite verbatim as their justification.

2026-08-16.
