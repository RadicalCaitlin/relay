# ADR 0004: Showing what an agent is doing during a turn

- **Status:** Accepted
- **Date:** 2026-10-11
- **Issue:** #10

## Context

Relay starts an agent's CLI, waits for it to exit, and shows the reply. A turn in which the agent
reads files, edits them and runs the tests takes minutes, and for all of that time the room shows
one line: "Waiting for Claude...". In the first session where two agents worked on an issue, that
looked like a hang, or like an agent waiting for an answer it could not ask for. It was neither.

The same session showed a second gap. The agent was refused twice, when it tried to create a
branch, and the user had no way to know (#11).

Both CLIs can report what they are doing as they go, in documented output modes:

- `codex exec --json`, which Relay already uses, prints one event per line while it works. Relay
  only read them after the process had exited.
- `claude -p --output-format stream-json --verbose` prints one object per line: what Claude says,
  each tool it uses, each tool it is refused, and at the end the same `result` object that
  `--output-format json` prints on its own.

What they report differs, and this was checked against real output (Claude Code 2.1.273,
codex-cli 0.162.0):

- Claude's thinking is present in the stream but has no readable text.
- Claude names a refusal as its own event. Codex has no such event. In the output captured, Codex
  did not attempt what its read-only sandbox forbids and said so in a note; a command that fails
  for any reason is reported as failed.
- Codex wraps each command in the shell it ran it with.

## Decision

**While an agent works, Relay shows what the agent reports doing, one short line per step, under
the waiting line. Activity is for the user only: it is not part of the room, and no agent ever
sees another's.**

- **Three kinds of item.** A *note* is something the agent said along the way. An *action* is a
  step it took: a tool it used, a command it started. A *problem* is a step that was refused or
  that failed. Notes and actions are shown dim with `-`; problems in yellow with `!`.
- **The reply is not activity.** The last thing an agent says in a turn is its reply. A session
  holds each note back until something follows it and drops the one still held at the end, so the
  reply appears once, as the reply. A turn that fails has no reply, so the note still held is
  shown: it is the last thing the agent said before it stopped.
- **Display only.** Activity is not recorded in the room. It is not in `/history`, it is not in
  what any agent is sent, and it does not survive the turn. Only a reply can reach another agent,
  which keeps ADR 0001's rule intact: everything an agent is shown is a message in the room.
- **Relay reads a refusal and does nothing else with it.** It is shown. Relay does not approve,
  retry, or suggest a flag. Answering permission prompts remains #11.
- **One line, cut short, cleaned.** Line breaks become spaces, a path under the working folder is
  shown relative to it, an item is cut at 200 characters, and the result goes through the same
  clean-up as a folder name.
- **Claude Code is run with `--output-format stream-json --verbose`** instead of
  `--output-format json`. Print mode only offers the stream together with `--verbose`. The reply
  is read from the `result` object at the end, exactly as before, so a version that printed only
  that object would still work.
- **An agent that reports nothing shows nothing.** A provider whose CLI has no such output leaves
  `ParseActivity` alone, and the turn looks as it did before.

## Consequences

- The user can tell a working agent from a stuck one, and sees a refusal when Claude reports it.
- Relay now holds Claude's whole stream in memory until the turn ends, which includes the result
  of every tool, such as the contents of files it read. The threat model records this.
- What is shown is the agent's own account, shortened. It is a sign of life, not an audit trail.
- A note is taken for the reply only if nothing follows it. If an agent's last words were followed
  by a step and the turn still ended with them as its reply, they would be shown twice: once as a
  note, once as the reply. Neither CLI is expected to end a turn that way, and a note already
  shown cannot be taken back; holding every note until the turn ends would stop the view being
  live.
- The callback that receives activity runs on the thread reading the process's output, while the
  command loop waits. Nothing else writes to the console during a turn. It is not called once the
  turn has ended, whatever a process the agent left behind goes on to print, and if it fails the
  rest of the turn is not shown: the output is still read, so the reply still arrives.
- A parser sees every line as it is printed, including lines that are not for it, cut-off lines
  and output from versions Relay has not seen. It must ignore what it does not recognise.

## Not done here

- **Streaming the reply itself** as it is written. Claude Code offers partial messages; this would
  be a second step for #10.
- **A time limit for a turn** (#29) and **an animated waiting indicator** (#19).
- **Rendering** the reply's Markdown and highlighting mentions (#40).
- **Codex's reasoning summaries**, file changes reported as their own event, MCP tool calls and
  web searches. None appeared in the output captured for this change, and a parser is only
  written against output that has been seen.
- **Results of steps**, such as a command's output. Only that a step started, was refused or
  failed is shown.
