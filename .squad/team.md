# Squad Team

> mahjong-unity

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Lead | Architecture & Migration | .squad/agents/lead/charter.md | Active |
| Unity | Unity WebGL Rendering | .squad/agents/unity/charter.md | Active |
| Frontend | Browser & Mobile UX | .squad/agents/frontend/charter.md | Active |
| Backend | Rules, APIs & Hosting | .squad/agents/backend/charter.md | Active |
| Tester | Quality & Compatibility | .squad/agents/tester/charter.md | Active |
| Scribe | Durable Decisions | .squad/agents/scribe/charter.md | Support |
| Ralph | Work Monitor | .squad/agents/ralph/charter.md | Support |
| Rai | Responsible AI Reviewer | .squad/agents/rai/charter.md | Support |
| Fact Checker | Verification | .squad/agents/fact-checker/charter.md | Support |

## Coding Agent

<!-- copilot-auto-assign: false -->

| Name | Role | Charter | Status |
|------|------|---------|--------|
| @copilot | Coding Agent | — | 🤖 Coding Agent |

### Capabilities

**🟢 Good fit — auto-route when enabled:**
- Bug fixes with clear reproduction steps
- Test coverage (adding missing tests, fixing flaky tests)
- Lint/format fixes and code style cleanup
- Dependency updates and version bumps
- Small isolated features with clear specs
- Boilerplate/scaffolding generation
- Documentation fixes and README updates

**🟡 Needs review — route to @copilot but flag for squad member PR review:**
- Medium features with clear specs and acceptance criteria
- Refactoring with existing test coverage
- API endpoint additions following established patterns
- Migration scripts with well-defined schemas

**🔴 Not suitable — route to squad member instead:**
- Architecture decisions and system design
- Multi-system integration requiring coordination
- Ambiguous requirements needing clarification
- Security-critical changes (auth, encryption, access control)
- Performance-critical paths requiring benchmarking
- Changes requiring cross-team discussion

## Project Context

- **Project:** mahjong-unity
- **Created:** 2026-10-10
- **Requested by:** Stephen Long
- **Purpose:** Replace the autotable rendering layer with Unity rendering in a browser-hosted mahjong game, improving mobile presentation without rewriting the established game.
- **Client target:** Unity WebGL and C#, integrated with a responsive browser shell.
- **Behavioral baseline:** `long2know/mahjong-autotable`; reuse its existing rule variants, including Changsha, and preserve lobby, chat, multiplayer, and backend API behavior.
- **Hosting requirement:** Continue to provide a Docker image for self-hosting.
- **Rendering requirements:** Perspective and flat views, independently laid-out hands, and rectangular mobile boards.
- **Accountable migration owner:** Lead. Other specialists own only their assigned implementation surfaces.

## Reference Sources

- Existing game: https://github.com/long2know/mahjong-autotable
- Original renderer: https://github.com/pwmarcz/autotable
- Original renderer background: https://pwmarcz.pl/autotable/about.html
- Unity renderer example: https://github.com/ArcturusZhang/Mahjong
- Rule reference: https://www.themahjongproject.com/how-to-play/basics
- Changsha guide: https://mahjongpros.com/blogs/how-to-play/beginners-guide-to-changsha-mahjong
- Changsha discussion: https://www.reddit.com/r/Mahjong/comments/xp6crv/better_know_a_variant_changsha_mahjong_%E9%95%BF%E6%B2%99%E9%BA%BB%E5%B0%86/
- Changsha reference: https://baike.baidu.com/en/item/Changsha%20Mahjong/36618

## Migration Constraints

- Existing implementation and regression tests define game behavior; rule guides are supporting references, not authorization to change rules.
- Keep game authority and rule evaluation on the existing backend.
- Preserve compatible multiplayer message shapes and lobby/chat functionality.
- Replace rendering rather than maintain autotable as the primary renderer or disguise a JavaScript canvas as Unity.
- Audit code and asset licenses before reuse; retain required attribution.
- Surface missing Unity builds, unavailable services, and failed validation explicitly.
- Do not expand scope into new rule variants, authentication redesign, or unrelated cleanup without approval.
