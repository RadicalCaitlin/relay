# ADR 0005: Asking the user before a step an agent may not take

- **Status:** Accepted
- **Date:** 2026-10-12
- **Issue:** #11

## Context

Relay runs each agent's CLI without a terminal of its own, so the CLI has nobody to ask. A step
the agent's own settings do not allow is refused inside the CLI, and the agent either gives up or
tries another way. In the sessions where two agents worked on an issue (#25, #18, #26), each such
refusal cost a turn, and the user watching could do nothing about it but change their settings
and ask again.

Claude Code also has a tool for asking the user a question with a few options. In print mode
there is nobody to answer it either.

What each CLI offers was checked against its documentation and against real output (Claude Code
2.1.273, codex-cli 0.162.0, on Windows):

| | Claude Code | Codex |
| --- | --- | --- |
| Rules set ahead of time | Settings files, `--allowedTools`, permission modes | Sandbox mode and its configuration |
| Asking during a turn | `--permission-prompt-tool`: a step that would have prompted is put to a tool the caller names, on an MCP server the caller supplies with `--mcp-config` | None. `codex exec` never asks, and a step its sandbox forbids fails |
| Its own questions to the user | Sent to the same tool, as a step named `AskUserQuestion` | None |
| Reports a refusal | In the result, and as its own event when nobody could be asked | No. Only a failed command |

Two ways of answering Claude Code were tried:

- **The documented one, used here.** Claude Code calls the named tool with the name of the step
  and what it would be given, and expects back `{"behavior":"allow","updatedInput":...}` or
  `{"behavior":"deny","message":...}`. It waited 200 seconds for an answer and carried on.
- **The one its SDK uses.** `--permission-prompt-tool stdio` with JSON on standard input makes
  Claude Code send the request to the program that started it. It works, and needs no second
  program, but neither the value `stdio` nor the messages are described in the CLI reference, and
  `AGENTS.md` allows only documented arguments.

## Decision

**When Relay runs in a terminal, a step Claude's own settings do not allow is put to the user, who
answers yes or no for that one step. Claude's questions are put to them the same way. Relay never
answers for the user, and an agent that cannot ask is run as before.**

- **Relay supplies the tool.** `relay approval-tool <pipe>` is a small MCP server with one tool.
  Claude Code is given a file naming it (`--mcp-config`) and told to ask it
  (`--permission-prompt-tool mcp__relay_approval__ask`). The file is written in a folder of
  Relay's own, outside the working directory, and is removed when Relay exits.
- **The tool decides nothing.** It passes each request to the Relay running the chatroom, over a
  named pipe whose name is made up for that run and which only the same user can open, and passes
  the answer back. If that Relay cannot be reached, the answer is no.
- **Only when somebody can be asked.** With input or output redirected, or a console that cannot
  interact, Relay passes neither argument and nothing changes: the CLI refuses the step itself.
- **Claude's own settings still decide first.** Nothing here allows anything by rule. What the
  settings allow happens without a question, what they deny is denied, and only what would have
  prompted reaches the user.
- **One step, once.** The answer covers the step shown and nothing after it. Relay does not offer
  to remember an answer, and ignores Claude Code's suggestions for rules that would. To be asked
  less, the user can switch Claude into its own auto mode (ADR 0006).
- **Only a typed yes allows.** `yes` or `y` allows; `no`, `n`, the end of input and Ctrl+C refuse;
  an empty line or anything else is asked again and is said not to have been sent to anyone. Keys
  pressed before the question was shown are thrown away first.
- **Typed at this question, on one line.** Input that arrived as several lines at once was pasted,
  not typed here, so the whole block is dropped — including a `yes` on its first line — and the
  question is asked again. This covers the answer to a permission question and the answer to one of
  Claude's own questions alike (#63). What Relay has to go on is timing, not a paste boundary the
  terminal reports: lines already waiting, or arriving within 30ms, are one block. A paste delivered
  more slowly splits, and its first line can then be read as an answer, so this narrows the window
  rather than closing it. Closing it needs the key-by-key input editor (#67).
- **The user is shown what will happen, not the agent's account of it.** Every field of the
  request is shown under its name. A command is shown in full, and a step whose command is too
  long to show in full (4,000 characters or 60 lines) is refused without asking. Other values are
  cut at 12 lines and 300 characters a line, and say how much is left out.
- **It is the agent's text.** Each line is cleaned the way a folder name is and starts with
  Relay's own indent, so it cannot reach the terminal or pass for a line of Relay's.
- **Questions.** Each question is shown with its options numbered. A number picks an option,
  several numbers pick several where the question allows it, and anything else is sent as the
  user's own answer. `/skip` leaves the questions unanswered, and the agent is told so.
- **A question belongs to a turn.** It is asked only while the agent that can ask is running. When
  that turn ends or is cancelled, the question is withdrawn and the next line typed is for the
  room. A request that arrives outside a turn, from a process an agent left behind, is refused,
  and so is one that was still waiting for the keyboard when its turn ended.
- **A question nobody is waiting on is withdrawn too.** When the CLI withdraws its call, or the
  program it started goes away, the pipe closes and the question is taken back and said to be.
- **A line typed across a withdrawal goes nowhere.** A read of the console cannot be abandoned, so
  the line the user was typing when a question was withdrawn may have been meant as its answer.
  It is not sent to the room or to another question: the user is told so and asked again.
- **`/approvals off`** refuses everything without asking, for when the user is not watching.
  `/approvals on` is the default in a terminal. Like `/pause`, it is set and never toggled.
- **Codex is unchanged**, and Relay says so: `/approvals` and the header name which agents ask
  and which cannot.

## Consequences

- A refused step no longer has to cost a turn, and the user decides with the command in front of
  them. A chained or piped command, which Claude Code will not allow by rule, can now be allowed
  once.
- A turn now waits for the user for as long as they take. A turn has no time limit unless the user
  starts Relay with `--turn-timeout` (#29), so a user who walks away with approvals on finds the
  turn where they left it. For Claude this needs the `timeout` Relay writes for its approval tool
  (#68): without it, Claude Code abandons a tool call that has been silent for 30 minutes and
  Relay withdraws the question. Claude Code has no value that turns the limit off, so Relay writes
  24 hours, the most `--turn-timeout` allows; a question left longer is withdrawn. It is set for
  Relay's own server only; Relay does not set `CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT`, which would
  apply to the user's other MCP servers too.
- What the user answers is said to Claude and is not a message in the room. It is not in
  `/history` and no other agent is sent it. ADR 0001's rule, that everything an agent is shown is
  a message in the room, now has this exception; the reply that follows is still the only thing
  that reaches anyone else.
- Claude Code starts one more process per turn, and waits for it to be ready before the turn.
- The tool is one of Claude's tools, so Claude can call it directly. The user would then be asked
  about a step that nothing is waiting to take, and a yes would allow nothing: the answer goes
  back to Claude as text. It cannot be told apart from a real request.
- When the user says no, Claude Code reports no refusal as an event, so no `!` line follows. The
  question and the answer are already on the screen.
- For a value that is cut short the user decides without seeing all of it, and is told how much
  they have not seen. That includes the content of a file being written.
- Relay is run a second way, as `relay approval-tool`, which is not listed in its help.

## Verified, and not

- **Verified against the real Claude Code** by `ClaudeApprovalLiveTests` (opt-in, on Windows):
  through Relay's own provider, tool and pipe, a refused write does not happen and an allowed one
  does. By hand, with a stand-in tool: a wait of 200 seconds, and a question with two parts
  answered and reported back correctly.
- **The approval tool's `timeout` (#68), against Claude Code 2.1.273 on Windows**, with a stand-in
  tool that answers after a set time. At Claude Code's default 30-minute idle window, with the
  `timeout` Relay writes, a question answered after 35 minutes was applied. With the idle window
  shortened to 60 seconds, a question answered after 150 seconds was applied; without the
  `timeout`, Claude Code abandoned it after 60 seconds and asked again. A tool that never finishes
  starting is still given up after about 30 seconds: the `timeout` does not lengthen startup.
- **Covered by tests that need no agent:** every answer and every reason not to ask, what is
  shown and cut, hostile text, the questions, the tool as a real process speaking the protocol,
  an unreachable Relay, a request that cannot be read, and that nothing changes without a terminal.
- **Run by hand, not by a test:** the chatroom's own wiring in a real terminal, which an automated
  test cannot reach because it redirects the streams. The maintainer ran it in a
  terminal on Windows and reported that it worked.
- **Not verified:** a wait longer than 200 seconds through the chatroom itself. Several requests
  at once, as from Claude's subagents; they are asked one after another. macOS and Linux with the
  real Claude Code; CI runs the tests that need no agent there.

## Not done here

- **Codex.** Its only interactive route is its app server, which is marked experimental.
- **Remembering an answer**, for the session or for good. That changes an agent's permissions,
  which Relay does not do.
- **Changing a step before allowing it.** The protocol lets the answer carry a changed input.
  Relay sends back what was asked, unchanged.
- **Recording questions and answers in the room.**
- **Other things a CLI may ask**, such as an MCP server's own requests for input.
