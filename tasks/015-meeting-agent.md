---
title: 'Task 015: Meeting agent — AI in the room'
description: >-
  Agent listens during standup, planning and other meetings, turns speech into proposed ticket actions, and applies them
  either after per-ticket approval (suggest mode, default) or immediately with undo (full auto, opt-in).
status: 'Todo'
phase: 2
depends_on: [004, 006, 010, 012]
purpose: 'Implementation spec for task 015.'
---

# Task 015: Meeting agent — AI in the room

## TL;DR

Listen to a meeting, propose ticket actions ("move FLOW-12 to next sprint"), and apply them. Two modes: **suggest**
(default — every action waits for approval, grouped by ticket) and **full auto** (applies at once, undoable). Users
will not trust this at first: suggest mode must prove itself before anyone turns on full auto.

See [product.md](../docs/product.md) (After v1), [ADR-001](../docs/adr/ADR-001-agent-parity.md),
[ADR-006](../docs/adr/ADR-006-eu-vendor-policy.md).

## Prerequisite

- New ADR choosing the speech-to-text and LLM vendors; promote them from Candidate in the ADR-006 register.
  Verify Danish + English accuracy and whether Hviske supports realtime streaming.

## Scope

### Meeting session

- Start a session from the board or sprint view; pick a type (standup, planning, refinement, other) and a mode.
- Browser captures audio and streams it to the API; API streams it to the STT vendor and keeps the transcript.
- Clear "listening" indicator for everyone in the session; one click to pause or stop.
- Transcript ingest also works as plain text via API, so an external agent or meeting tool can feed it.

### Proposed actions

- LLM turns transcript segments into proposed actions using the same operations as the MCP tools (status, sprint,
  assignee, estimate, comment, create issue).
- Each proposal stores: target issue, operation, before/after diff, triggering transcript snippet, confidence.
- Vague references ("the login bug") are matched with search. If more than one issue matches, the proposal asks
  "which issue?" with the candidates and is never applied automatically.

### Modes

- **Suggest** (default): proposals appear live in a side panel, grouped by ticket. Per proposal: approve, edit, reject.
  Per ticket: approve all. Nothing changes until approved.
- **Full auto**: proposals are applied as they come and shown in a live feed with undo per action.
  Still held for approval in full auto: low-confidence proposals, ambiguous references, destructive operations
  (delete, close as won't-do), and creating issues.
- Workspace setting picks the default mode; only admins can enable full auto. Optionally restrict full auto to chosen
  operation types (e.g. status and sprint changes only).

### Building trust

- Suggest mode keeps an approval history: proposals made / approved unchanged / edited / rejected, per operation type.
- Show these stats next to the full-auto toggle ("you approved 47 of 50 status changes unchanged").
- End-of-meeting summary: what was applied, rejected, and left pending; pending proposals stay reviewable afterwards.

### Audit and undo

- Changes record the agent as actor, plus the meeting session and the approving user (suggest mode).
- Activity feed links each change to its transcript snippet.
- Undo per action and "undo whole meeting".

### Parity

- Sessions and proposals are API resources: list sessions, list proposals, approve / edit / reject a proposal.
- CLI: `flowstate meeting proposals`, `flowstate meeting approve|reject <id>`. MCP: `list_proposals`, `review_proposals`.
- Audio capture is browser-only — explicit opt-out for CLI/MCP; text transcript ingest has full parity.

### Privacy

- Session start notes that audio is being processed; audio is not stored after transcription.
- Transcript retention is a workspace setting (default: 30 days).
- All processing stays with EU vendors from the ADR-006 register.

## Acceptance criteria

- [ ] In suggest mode, saying "move FLOW-12 to next sprint" in a meeting produces a proposal grouped under FLOW-12 that
      shows the snippet and the diff, and changes nothing until approved.
- [ ] Approve, edit and reject work per proposal; "approve all" works per ticket.
- [ ] In full auto, the same sentence moves FLOW-12 within 5 s of speech, and undo reverts it.
- [ ] In full auto, an ambiguous or low-confidence proposal waits for approval.
- [ ] Only admins can enable full auto; the toggle shows approval stats from suggest-mode history.
- [ ] Every agent change appears in the activity feed with actor, session and transcript snippet.
- [ ] Proposals can be listed, approved and rejected via REST, CLI and MCP; parity test passes.
- [ ] Danish and English meetings both work.
