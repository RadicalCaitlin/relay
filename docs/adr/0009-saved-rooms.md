# ADR 0009: Saving a room between runs

- **Status:** Accepted
- **Date:** 2026-10-24
- **Issue:** #12

## Context

A room has lived in memory since the first release, and it was gone when Relay exited: every
message, which messages each agent had seen, each agent's role, and the id that lets Relay continue
each agent's own conversation in its CLI. Work that runs over more than one sitting had to start the
agents from nothing every time.

Issue #12 asks for rooms and transcripts that survive a restart. It also asks that their contents and
the provider's session ids be treated as potentially sensitive. Five things constrain the answer:

- **A transcript is sensitive.** It holds what the user typed, which may include pasted secrets,
  and what the agents read back from the code. Until now the only file Relay wrote was the list of
  trusted folder paths (`docs/threat-model.md`).
- **A session id is a capability.** Passed to `claude --resume` or `codex exec resume`, it continues
  a conversation under the user's account. It also goes on a command line, so it must never be able
  to become a flag (`AGENTS.md`).
- **A conversation belongs to a folder.** Both CLIs keep their sessions per working folder, so a
  conversation cannot be continued from another one.
- **No database and no new packages** (`AGENTS.md`).
- **What an agent has seen decides what it is sent** (ADR 0001). A saved room that gets this wrong
  either sends an agent a whole transcript again or leaves it missing messages.

## Decision

**A room is saved only when the user names it: `relay start --room <name>`. It is saved for the
current folder, in a folder of the user's own that only they can read, after every change. Starting
Relay again in the same folder with the same name reopens it, and each agent continues its own
conversation where its CLI still has it. `relay rooms` lists saved rooms, and
`relay rooms --delete <name>` deletes one.**

- **Off unless asked for.** Plain `relay start` saves nothing, exactly as before. The header of a
  saved room says where it is saved and how to delete it.
- **One file per room and folder**, named from a hash of the folder and the room name, in `rooms`
  under `RELAY_HOME` or the user's local data folder (local, so it does not follow a roaming
  profile). The name is letters, digits, `-` and `_`, so it can never reach another folder. Nothing
  in the working folder decides where the file is.
- **Only the user can read it.** On macOS and Linux the folder is `0700` and each file `0600`. On
  Windows the folder gets an access list that names only the user, inherited by everything in it,
  with nothing inherited from above. It is not encrypted: anything running as the user can read it,
  as it can read the agents' own transcripts.
- **What is saved:** the messages, what each agent has seen, what was sent to it in turns that did
  not complete, how much of the room `/reset` told it to forget, its role, and its CLI's session id.
  **Never saved:** activity, the user's answers to approval questions, `/pause`, `/approvals` and
  `/auto`, which start off again, and `--max-turns`, which stays a start-up option.
- **Saved as it happens.** Every change writes the whole room to a new file that is renamed over the
  old one, as the trust list is, so a crash or a kill leaves a complete room. A save that fails is
  said once, the room goes on in memory, and the next save that works catches up.
- **One Relay at a time.** A lock beside the file is held while the room is open. A second Relay
  that asks for the same room is refused, and so is deleting it.
- **Continuing a conversation.** The saved session id is checked again exactly as one read from the
  CLI's output, before it can reach a command line. When it is accepted, the agent's view of the
  room is restored with it. When there is none, or it is refused, the agent starts a new
  conversation and is shown the room again, except what `/reset` told it to forget. When its CLI
  no longer has the conversation, the turn fails as any other does, and `/restart <agent>` shows it
  the room again.
- **An agent missing this time keeps its place.** An agent that is not installed, or not chosen
  with `--agents`, is saved as it was, so it can continue the next time it joins.
- **A file that does not hold together is set aside, not repaired.** It is renamed beside itself
  with `.corrupt-` and the time, the room starts empty, and Relay says where it went. A file that
  exists but cannot be read is an error, and Relay does not start: starting empty would replace it
  on the first change.
- **Deleting removes every copy**, including anything set aside.
- **No automatic expiry.** A saved room stays until the user deletes it.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Save every room by default, with an option to turn it off | Transcripts would reach the disk without anybody deciding to keep them. |
| Encrypt with DPAPI on Windows | A new package, Windows only, and no protection from software running as the user, which is what could read the file anyway. |
| Save only the transcript, never the session id | No session id on disk, but every reopened agent loses the context it built in its own conversation, which is most of the value. |
| Expire rooms after a period | A default that deletes the user's data without asking. |
| An append-only log of changes | Smaller writes, but each kind of change needs its own entry and its own recovery. Rooms are small, and writing the whole room reuses the trust list's tested replace. |
| One room for every folder | A conversation cannot be continued from another folder. |

## Consequences

- **The threat model changes.** Relay now writes transcripts and session ids to disk when asked to.
  `docs/threat-model.md` says what is written, who can read it, and how to delete it.
- **Restore is checked as hard as the file is.** Anything that does not hold together, such as a
  message an agent is said to have seen that is not in the room, or a role longer than `/role`
  allows, sets the file aside rather than being guessed at.
- **Tests:** `SavedRoomTests` covers the round trip, what each agent is sent after a reopen with and
  without its conversation, `/reset` across a reopen, the lock, names, corrupt and unreadable files,
  failed saves, permissions, listing and deletion. `CliAgentTests` covers a saved session id that
  could be read as a flag. `CliSmokeTests` runs `--room` and `relay rooms` end to end.

## Verified, and not

- **Verified on Windows:** everything the tests cover, including the access list on the folder and
  its files.
- **Not verified:** that a real Claude Code or Codex continues a conversation from an earlier run of
  Relay; the tests use fake CLIs. The arguments are the ones each turn already uses. macOS and Linux
  permissions are covered by the same test, which CI runs there.
