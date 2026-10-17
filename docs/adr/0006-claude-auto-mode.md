# ADR 0006: Letting Claude run in its own auto mode

- **Status:** Accepted
- **Date:** 2026-10-17
- **Issue:** #64

## Context

ADR 0005 puts to the user every step Claude's own settings do not allow, one step at a time, and
Relay never remembers an answer. In the run on #63 that was most of what Claude did: 20 of its 28
shell commands were piped or chained, which no allow rule matches as a whole, and each could reach
the question. The maintainer left the room early because of it.

Claude Code has a permission mode for this, auto mode. What it does was checked against its
documentation ([permission modes](https://code.claude.com/docs/en/permission-modes)) and, where
marked, against Claude Code 2.1.273 on Windows:

- A second model, the classifier, reviews each step that no rule decides, instead of the user. It
  blocks what goes beyond the request, targets infrastructure it does not recognise, or appears
  driven by hostile content Claude read. A blocked step is not run, and Claude is told why.
- Deny rules still deny and ask rules still ask. With `--permission-prompt-tool`, an ask goes to
  that tool, which for Relay is the user's question from ADR 0005.
- After 3 blocks in a row or 20 in a session the classifier stops deciding and Claude Code asks
  instead, which again reaches the user's question.
- `-p` starts in Manual mode unless told otherwise. `defaultMode: "auto"` is ignored in a
  repository's `.claude/settings.json` and `.claude/settings.local.json`, so a folder cannot turn
  it on. `--permission-mode auto` works with `-p`. **Verified:** the `system`/`init` event of the
  run reports `"permissionMode": "auto"`.
- Each Relay turn is a new `claude -p --resume` run. **Verified:** a run with `--permission-mode
  auto` resumed the conversation of one without it and reported `auto`, and the run after it,
  without the flag, reported `default` and still remembered the conversation.
- Where auto mode is unavailable (plan, model, provider, an organisation that turned it off, or
  Anthropic turning it off), Claude Code starts in Manual mode instead.

`AGENTS.md` forbids passing "flags that auto-approve tool use or weaken sandboxing, such as
`--dangerously-skip-permissions` [or] `--permission-mode bypassPermissions`".

## Decision

**The user can switch Claude into its auto mode and out of it during a session, with `/auto on` and
`/auto off`. While it is on, Relay passes `--permission-mode auto` to each Claude turn. Relay still
decides nothing: Claude's classifier, rules and settings do, and what they would put to a person
still reaches the user through ADR 0005.**

- **Set, never toggled.** `/auto on` and `/auto off`, and `/auto` alone says which it is, like
  `/pause` and `/approvals`. It is off when Relay starts.
- **From the next turn.** The mode is read when a turn starts. Commands are only read between
  turns, never during one or at a pause, so a change never reaches a turn or an exchange already in
  progress.
- **Only the user changes it.** It is a `/command`, read only from the first line of the user's own
  input. Nothing an agent writes, and nothing in the working folder, can turn it on.
- **Off means unchanged.** With auto mode off, Relay passes no permission mode, exactly as before,
  and Claude's own settings decide the mode.
- **Relay reports what Claude ran in.** The mode is taken from the run's `init` event. When auto
  mode was asked for and Claude reports another mode, Relay says that auto mode was not available
  and that steps will be put to the user.
- **Claude only.** Codex has no equivalent that Relay can pass, and its own sandbox and approval
  settings already decide (ADR 0005). `/auto` says so.
- **Seen in the header and in `/approvals`**, so the user knows that few questions will come.
- **`--permission-mode auto` is the only mode Relay passes**, and only while the user has turned it
  on. It is not one of the flags `AGENTS.md` forbids: it approves nothing by itself, keeps every
  deny and ask rule, and has each remaining step reviewed. `bypassPermissions`, `acceptEdits`,
  `dontAsk` and the rest stay unused.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Remember the user's answer for the session | Relay would be writing permissions for an agent, which ADR 0005 rules out. |
| Document `defaultMode: "auto"` in the user's own settings | Changes every Claude Code session that uses those settings, cannot be turned on for Relay alone, and cannot be switched off for one step. |
| A start option only (`relay start --auto`) | The maintainer leaves auto mode to approve one thing by hand and then returns to it, as Shift+Tab allows in Claude Code. A start option cannot do that. |
| On by default | Relay would be changing how an agent decides without the user asking. |
| Pass the mode the user sets in Claude Code | Relay cannot see it without reading the agent's configuration. |

## Consequences

- **Fewer questions.** Routine steps go ahead without one. The user's question remains for ask
  rules, for repeated blocks and for Claude's own questions.
- **A blocked step costs the agent, not the user.** Claude is told why and carries on. The user
  sees it only if the agent says so, or as a refusal if Claude reports one.
- **Steering between agents.** In an exchange, a handover from another agent is content Claude
  read, and the classifier is meant to block a step driven by it. Auto mode does not guarantee
  that, and Claude Code says so. With pause mode off, the user is trusting the classifier with
  what one agent asks another to do. `docs/threat-model.md` records this, and pause mode remains
  the way to read each handover first.
- **Cost.** Each classifier review is a model call on the user's account.
- **Tests** (`AutoModeTests`) cover the commands, that the flag is passed only while auto mode is
  on and from the next turn, never to Codex, that a reported mode other than `auto` is shown and
  cannot reach the terminal as markup, and that nothing an agent writes changes the mode.

## Verified, and not

- **Verified with Claude Code 2.1.273 on Windows:** `-p --permission-mode auto` runs and reports
  `auto`; the mode follows each run's flag across `--resume`.
- **Not verified:** how a blocked step appears in the stream; that an ask rule in auto mode reaches
  Relay's tool; the fallback after repeated blocks; macOS and Linux.
