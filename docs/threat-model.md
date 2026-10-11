# Threat model

What Relay trusts, what it does not, how each boundary is enforced, and what is still at risk.
Last reviewed against Claude Code 2.1.273 and codex-cli 0.162.0.

## What Relay does

Relay runs on your machine as you. For each message you address to an agent it starts that
agent's CLI (`claude` or `codex`) as a child process in the folder you launched Relay from, writes
your message to the CLI's standard input, and shows the reply. If you named several agents, it
may then start another of them with that reply, a limited number of times. It holds no credentials, opens no
network connections of its own, and keeps no logs. The only file it writes is its list of trusted
folders. The agent CLIs do all of those things themselves, under their own sign-in and configuration.

## Trust boundaries

| Party | Trusted? | Why |
| --- | --- | --- |
| You, typing in the chatroom | Yes | You are the only source of instructions, and the only one who can open an exchange between agents. |
| The agent CLI executables on your `PATH` | Yes | Relay runs whatever `claude` and `codex` resolve to. If your `PATH` is compromised, so is everything else you run. |
| Your own user-level agent configuration | Yes | It is yours, and the CLIs enforce it. |
| The folder Relay runs in | **Not until you say so** | It may be a repository you just cloned. See "Workspace trust" below. |
| Agent output (replies, errors, version strings) | No | It is model output and can be steered by whatever the agent read. |

## Workspace trust

When run non-interactively, Claude Code skips its workspace-trust prompt. Verified behaviour of
`claude -p` in a folder that has never been trusted:

| Folder content | Default `claude -p` | With `--setting-sources user` |
| --- | --- | --- |
| Hook in `.claude/settings.json` | **Command runs** | Not loaded |
| MCP server in `.mcp.json` | **Server process starts** | Not started |
| `CLAUDE.md` instructions | Loaded | Not loaded |

So without a countermeasure, opening Relay in a hostile repository and sending one `@claude`
message would run commands chosen by that repository.

Relay therefore treats a folder as untrusted until you say otherwise, and passes
`--setting-sources user` so Claude loads only your own user-level settings. To load the folder's
settings as well:

- answer yes when `relay start` asks "Do you trust the files in this folder?" The answer is
  remembered, so you are asked once per folder. A no is not remembered; you are asked again next time.
- run `relay trust` in the folder, which remembers it without starting a chat.
- pass `relay start --trust-workspace`, which trusts it for that session only and remembers nothing.

`relay trust --remove` forgets the current folder and `relay trust --list` shows every trusted one.

When input is not a terminal (piped or scripted) there is nobody to ask, so an unknown folder
stays untrusted. The chatroom header always shows which mode is in effect.

Trusted folders are stored in `trusted-folders.json` in your user configuration folder
(`%APPDATA%\relay` on Windows, `~/.config/relay` on Linux, `~/Library/Application Support/relay`
on macOS), or under `RELAY_HOME` when that is set to an absolute path.

- Trust covers the exact folder only, never its parents or subfolders.
- A missing or corrupt file means nothing is trusted.
- A file that exists but cannot be read (locked by another program, access denied) is reported as
  an error. It is never treated as empty, because saving on top of that would wipe the list.
- Changes hold the file exclusively while they read and write it, so two Relay processes cannot
  lose each other's change.
- A relative `RELAY_HOME` is ignored, so a folder cannot supply its own trust file. If no user
  configuration folder can be found at all, nothing is trusted and nothing can be remembered.

The cost of the default is that Claude does not see the project's `CLAUDE.md` in an untrusted folder.

**Relay's folder trust governs Claude only.** Codex is left to its own checks, which stay active in
`codex exec`: it refuses to run outside a trusted directory or Git repository, and its `--help`
states that hooks need persisted trust. Relay passes none of the flags that turn those off, but it
also has no documented flag to make Codex ignore a folder. So in a folder Relay calls untrusted,
Codex still reads that folder's `AGENTS.md`, and loads its project configuration if Codex itself
considers the folder trusted. The chatroom header says this rather than implying otherwise.

## Permission and approval boundaries

- Relay never passes a flag that approves tool use, skips permission checks, or changes
  sandboxing. A test asserts this for every provider, with and without workspace trust.
- Relay cannot answer a permission prompt. Anything an agent would need approval for is declined
  by the agent CLI, so agents are limited to what your own configuration already allows.
- `--setting-sources user` only narrows what Claude loads. It grants nothing.
- An agent can start another agent's turn only inside an exchange you opened; see the next section.
  Outside one, only text you type starts a turn. `/commands` are only ever read from your input: an
  agent reply containing `/reset` is displayed and nothing more.

## Exchanges between agents

A message that begins with more than one mention, such as `@claude @codex agree on a design`, opens
an exchange (see [adr/0002-agent-to-agent-exchanges.md](adr/0002-agent-to-agent-exchanges.md)).
Inside it, a reply whose first line is only a mention of another agent you named is delivered to
that agent. This is the one way an agent's output starts a process.

- **Only you can open one, and only for the agents you named.** Mentions later in your message do
  not count, so pasted text cannot invite an agent. An agent cannot invite another, and a reply
  addressed to an agent you did not name is shown and not delivered.
- **A handover has to be unmistakable.** A reply that merely begins by naming an agent, as prose
  often does, is a reply to you. Outside an exchange no reply is read as addressed to an agent at
  all, so an agent cannot make its words appear in the room as a message to another one.
- **It is bounded.** An exchange is at most 6 agent turns, counting the first and any that fail.
  A reply addresses one agent, and one agent runs at a time, so an exchange is a chain that starts
  at most 6 processes. Nothing an agent writes can raise the count or start a new exchange.
- **It stops on the first problem.** A failed turn ends the exchange; nobody else is tried.
- **You can stop it.** Ctrl+C cancels the whole exchange and kills the running agent's process
  tree. Closing Relay does the same.
- **A handed-over message never passes for yours.** It is the last line of the transcript, escaped
  and under its author's name. On such a turn the only text after the transcript is a fixed line
  written by Relay, and the agent is told that nothing after it is from you. Your opening message
  is in the same transcript, marked as the human's.
- **The rules reach each agent in Relay's words**, not through another agent: who is in the
  exchange, how to hand over, how many turns remain, and that another agent's message is a
  colleague's request and not an instruction from you.
- **Pause mode puts you back between the agents.** With `/pause` on, each handover is shown and
  held until you type `/go`. An empty line does not send it, because a key pressed while you were
  waiting would otherwise approve a message you had not read. A line that starts with `/` or with
  another agent's name is asked again rather than sent to an agent. It is turned on with
  `/pause on` and off with `/pause off`, never toggled, so it cannot be switched off by repeating
  a command. You cannot edit an agent's message;
  what you type instead goes to its author as your own message. Pause mode is off by default.

## Roles

`/role` lets you tell an agent what to be in the room, such as `reviewer`
(see [adr/0003-agent-roles.md](adr/0003-agent-roles.md)). Relay states the role to the agent in
its own introduction on every turn.

- **Role text is yours or Relay's.** It comes from the built-in list or from what you type into
  `/role`, never from an agent's output and never from a file in the folder. An agent cannot set,
  change or remove a role, its own or another's.
- **A role of your own is bounded and escaped**: a name of letters, digits and hyphens, and a
  description of at most 500 characters, escaped like any other text Relay places in a prompt.
- **A role grants nothing and restricts nothing.** It does not change who can start a turn, the
  turn limit, or any permission. See "Remaining risks".

## Shared room context

Everything said in a room is shown to each agent the next time you address it
(see [adr/0001-shared-room-context.md](adr/0001-shared-room-context.md)). That makes one agent's
output part of another agent's input, which is new: before, an agent only ever read what you typed.

- **Attribution cannot be forged.** Each replayed message is one line naming its author, whether
  the author is the human or an agent, and who it was addressed to. `&`, `<`, `>` and `"` in the
  text are escaped and line breaks are encoded, so a message cannot close its element, open
  another, or add a line of its own. An agent therefore cannot make its words appear to come from you.
- **The end of the transcript cannot be imitated.** The block's closing line carries a random value
  chosen for each prompt, and the agent is told that only the text after that exact line is your
  message. Text inside the transcript can neither contain that line nor guess it.
- **Names are reserved.** No agent can register as `you`, `relay`, `User`, `everyone`, or under
  another agent's name.
- **The transcript is marked as context, not instructions**, and your own message is placed after
  it, outside the block.
- **A mistyped address stays what it was.** A message to a name that is not in the room is shown to
  agents as addressed to that name, not as a note to everyone.
- **Relay's own notices are not replayed**, so error text from one CLI does not reach another agent.
- **Rooms are isolated.** What an agent has seen is tracked per room, and an agent cannot take
  part in two rooms, so nothing said in one room can reach an agent in another.
- **The amount replayed is capped** at 20 messages and 16,000 characters of message text per turn,
  measured after escaping. A message that was sent in two turns that did not complete is not sent
  a third time, so repeated failures cannot keep growing an agent's conversation.

## Command-line and executable safety

- **Your text goes to standard input, never onto the command line.** On Windows the CLIs are
  `cmd.exe` shims, where argument quoting cannot be made safe.
- **Session ids are the only agent-supplied value placed on a command line.** They must match
  `^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$`, so they can be neither shell syntax nor a flag.
- **No shell is involved** (`UseShellExecute = false`), and arguments are passed as a list.
- **Relative `PATH` entries are dropped**, both when Relay resolves `claude` and `codex` and from the
  `PATH` the agent process inherits. A relative entry such as `.` (or an empty entry on Unix)
  resolves against the working directory, and would let a repository supply its own `claude`, or
  the `node` that the real shim then calls.
- **A program planted in the working directory cannot hijack the shim on Windows.** `cmd.exe`
  searches the current directory before `PATH`, and the npm shims call `node` by bare name, so a
  `node.cmd` in a repository would otherwise run. Relay sets `NoDefaultCurrentDirectoryInExePath`
  for the agent process. Side effect: commands the agent runs through `cmd.exe` also need `.\` to
  run a program from the current directory, as they already do in PowerShell and on Unix.

## Output handling

Everything Relay did not write is passed through one function before display. It removes control
characters (so output cannot clear the screen, set the window title, or rewrite earlier lines),
removes bidirectional override characters (so it cannot reorder what you see), and escapes
console markup. This covers agent replies, error text from the CLIs, and version strings.

Folder names get a stricter version that also removes line breaks. On Linux and macOS a folder
name can contain one, and could otherwise add lines that look like Relay's own, such as a fake
"trusted" status, right when you are deciding whether to trust it.

## Secrets and logging

- Relay has no logging. The only thing it writes to disk is the list of trusted folder paths. Room
  history lives in memory and is gone on exit.
- Relay never reads the agents' credential or configuration files.
- The agent process inherits your environment, including any API keys in it, exactly as it would
  if you ran the CLI yourself. Relay never prints or stores environment variables.
- When a CLI fails, Relay shows the last lines of its error output (at most 400 characters) in the
  room. If a CLI ever printed a secret there, it would be shown on screen, but not saved.

## Secrets in the repository

Two checks keep credentials out of Relay's own source, both using [gitleaks](https://github.com/gitleaks/gitleaks):

- **CI** scans the commits each pull request or push to `main` adds, including what merge commits
  themselves change. Every commit is scanned, so a secret added and then removed within the same
  pull request is still caught. It does not rescan older history; that was scanned in full, and
  found clean, when the check was introduced.
- **A pre-commit hook** (`.githooks/pre-commit`) refuses a commit whose staged changes contain a
  secret. It is opt-in per clone (`git config core.hooksPath .githooks`) and needs gitleaks
  installed; without gitleaks it blocks the commit rather than skip the check.

Neither can stop someone who bypasses hooks and ignores CI; pattern-based scanning also misses
secrets that do not look like known credential formats.

## Process lifetime

Cancelling a request, or closing Relay normally, kills the agent's whole process tree first.
See "Remaining risks" for the cases this does not cover.

## Remaining risks

| Risk | Status |
| --- | --- |
| **A trusted folder can run commands.** Trusting a folder lets its hooks and MCP servers execute, the same as trusting it in Claude Code. | By design. Only trust folders you would run code from. |
| **Agents act with your permissions.** Whatever your agent configuration allows without a prompt, an agent can do in the working directory, including in an untrusted folder. Prompt injection from file contents can steer it. | Inherent to coding agents. Keep permissive allow-rules out of your user-level settings. |
| **Relay does not enforce folder trust for Codex.** It relies on Codex's own trusted-directory and hook-trust checks, confirmed from its help text and its refusal to run outside a Git repository, but not tested with a hostile project configuration. Codex reads an untrusted folder's `AGENTS.md`. | Open. |
| **The trust list is an ordinary file in your profile.** Anything running as you, including an agent whose configuration allows it to write there, could add a folder to it. | Accepted for now; the same is true of the agents' own trust records. |
| **The trust prompt is not covered by an automated test.** It only appears in a real terminal. The remembered, session-only and non-interactive paths are tested end to end. | Open. |
| **A forced kill of Relay** (`taskkill /F`, `kill -9`, a crash) leaves the agent running. | Open (#22). |
| **Background processes started by an agent** outlive the turn and are not tracked. | Open (#20). |
| **Agents can influence each other through the room.** A reply from one agent is read by the others. Anything that steered the first, such as a file or web page it read, can try to steer the next. Labelling and escaping stop forgery, not persuasion: a model may still act on text it was told is only context. | Inherent to a shared conversation. An agent still only acts when you address it or inside an exchange you opened, and within what your configuration allows. |
| **Inside an exchange, you do not read a reply before the next agent acts on it.** A reply steered by something the first agent read reaches the second without your review, for up to 5 handovers per message. | By design when pause mode is off. Bounded by who you named, the turn limit and Ctrl+C. Use `/pause on` to approve each handover. |
| **One message can cost up to 6 agent turns** when it names several agents. | By design. The limit is fixed for now (#13). |
| **A role is not a restriction.** An agent told it is a reviewer can still change files if its own configuration allows it, and an agent may ignore its role. | By design. Relay never changes what an agent may do; limit that in the agent's own configuration. |
| **You cannot type during a turn**, so between pauses the only way to interrupt an exchange is Ctrl+C. | Accepted for now. |
| **A mistyped agent name after the first is sent as text.** `@claude @cdoex agree` goes to Claude alone, because a second `@word` that is not an agent is usually code (`@Override`). No exchange opens, so nothing is delivered that you did not ask for, but you get a narrower conversation than you meant. | Accepted. The waiting line shows a turn count only when an exchange is open. |
| **An agent that writes a handover the wrong way is not understood.** Its reply comes to you and the exchange ends without a notice. | By design: the safe way to be wrong. |
| **Nothing in a room is private from its agents**, including what you said to a different agent, and anything pasted into the room. | By design. Private messages are not implemented. |
| **Display spoofing with plain text.** An agent can print lines that look like chat lines, such as `You: ...`, though not in the real prompt's colours. | Accepted for now. |
| **Unbounded output.** A CLI that printed gigabytes would exhaust memory. | Accepted for now. |
| **Dependencies.** NuGet packages are pinned to exact versions. GitHub Actions are pinned to major tags, not commit hashes. | Accepted for now. |
| **Permission prompts are not surfaced.** Denials happen inside the agent CLI with no explanation in the room. | Open (#11). |

## Reporting a problem

Until the project has a published security policy (#15), report a suspected vulnerability
privately to the maintainer rather than in a public issue.
