# Work Routing

How to decide who handles what.

## Routing Table

| Work Type | Route To | Examples |
|-----------|----------|----------|
| Architecture, migration contracts, licensing, integration | Lead | Inspect the baseline, establish boundaries, resolve cross-surface decisions |
| Unity rendering and WebGL project | Unity | Tiles, cameras, board geometry, separate hands, touch picking, WebGL builds |
| Browser shell, lobby, chat, responsive UX | Frontend | JavaScript-Unity bridge, multiplayer client integration, mobile layout |
| Rule engine, server APIs, transport, Docker hosting | Backend | Preserve variants and protocol, import server, package reproducible hosting |
| Regression and compatibility evidence | Tester | Rules, hidden information, multiplayer, browser input, container smoke tests |

## Issue Routing

| Label | Action | Who |
|-------|--------|-----|
| `squad` | Triage: analyze issue, assign `squad:{member}` label | Lead |
| `squad:{name}` | Pick up issue and complete the work | Named member |

### How Issue Assignment Works

1. When a GitHub issue gets the `squad` label, the **Lead** triages it — analyzing content, assigning the right `squad:{member}` label, and commenting with triage notes.
2. When a `squad:{member}` label is applied, that member picks up the issue in their next session.
3. Members can reassign by removing their label and adding another member's label.
4. The `squad` label is the "inbox" — untriaged issues waiting for Lead review.

## Rules

1. Assign exactly one accountable specialist per work item. Lead owns the overall migration.
2. Parallelize only independently deliverable outcomes with explicit, non-overlapping write scopes.
3. Agree on protocol and renderer contracts before work modifies shared systems.
4. Keep support agents out of specialist routing. Activate Scribe only for accepted durable decisions; use other support agents for concrete bounded needs.
5. Quick factual status questions stay with the coordinator.
6. Select one blocking review gate when needed; Tester normally gates integration and behavior preservation.
7. Issue-labeled work routes to the named member; Lead triages the base `squad` label.
8. Preserve user changes. Agents must not stash, clean, restore, reset, switch branches, or commit another agent's files in a shared worktree.
9. Use configured runtime state tools for mutable team state. Do not hand-roll state branches or git notes.
