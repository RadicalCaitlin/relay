# Threat model

What Relay trusts, what it does not, how each boundary is enforced, and what is still at risk.
Last reviewed against Claude Code 2.1.273 and codex-cli 0.162.0.

## What Relay does

Relay runs on your machine as you. For each message you address to an agent it starts that
agent's CLI (`claude` or `codex`) as a child process in the folder you launched Relay from, writes
your message to the CLI's standard input, and shows the reply. It holds no credentials, opens no
network connections of its own, and keeps no logs. The only file it writes is its list of trusted
folders. The agent CLIs do all of those things themselves, under their own sign-in and configuration.

## Trust boundaries

| Party | Trusted? | Why |
| --- | --- | --- |
| You, typing in the chatroom | Yes | You are the only source of instructions. |
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
- A missing, unreadable or corrupt file means nothing is trusted.
- A relative `RELAY_HOME` is ignored, so a folder cannot supply its own trust file.

The cost of the default is that Claude does not see the project's `CLAUDE.md` in an untrusted folder.

Codex is left to its own checks, which stay active in `codex exec`: it refuses to run outside a
trusted directory or Git repository, and its `--help` states that hooks need persisted trust.
Relay passes none of the flags that turn those off.

## Permission and approval boundaries

- Relay never passes a flag that approves tool use, skips permission checks, or changes
  sandboxing. A test asserts this for every provider, with and without workspace trust.
- Relay cannot answer a permission prompt. Anything an agent would need approval for is declined
  by the agent CLI, so agents are limited to what your own configuration already allows.
- `--setting-sources user` only narrows what Claude loads. It grants nothing.
- Agents never trigger each other. Only text you type is routed, and `/commands` are only read
  from your input. An agent reply containing `@codex ...` or `/reset` is displayed and nothing more.

## Command-line and executable safety

- **Your text goes to standard input, never onto the command line.** On Windows the CLIs are
  `cmd.exe` shims, where argument quoting cannot be made safe.
- **Session ids are the only agent-supplied value placed on a command line.** They must match
  `^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$`, so they can be neither shell syntax nor a flag.
- **No shell is involved** (`UseShellExecute = false`), and arguments are passed as a list.
- **Executables are resolved from absolute `PATH` entries only.** A relative entry such as `.`
  would resolve against the working directory and let a repository supply its own `claude`.
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

- **CI** scans the commits each pull request or push to `main` adds. Every commit is scanned, so a
  secret added and then removed within the same pull request is still caught. It does not rescan
  older history; that was scanned in full, and found clean, when the check was introduced.
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
| **Codex trust is not independently verified.** Relay relies on Codex's own trusted-directory and hook-trust checks, confirmed from its help text and its refusal to run outside a Git repository, but not tested with a hostile project configuration. | Open. |
| **The trust list is an ordinary file in your profile.** Anything running as you, including an agent whose configuration allows it to write there, could add a folder to it. | Accepted for now; the same is true of the agents' own trust records. |
| **The trust prompt is not covered by an automated test.** It only appears in a real terminal. The remembered, session-only and non-interactive paths are tested end to end. | Open. |
| **A forced kill of Relay** (`taskkill /F`, `kill -9`, a crash) leaves the agent running. | Open (#22). |
| **Background processes started by an agent** outlive the turn and are not tracked. | Open (#20). |
| **Display spoofing with plain text.** An agent can print lines that look like chat lines, such as `You: ...`, though not in the real prompt's colours. | Accepted for now. |
| **Unbounded output.** A CLI that printed gigabytes would exhaust memory. | Accepted for now. |
| **Dependencies.** NuGet packages are pinned to exact versions. GitHub Actions are pinned to major tags, not commit hashes. | Accepted for now. |
| **Permission prompts are not surfaced.** Denials happen inside the agent CLI with no explanation in the room. | Open (#11). |

## Reporting a problem

Until the project has a published security policy (#15), report a suspected vulnerability
privately to the maintainer rather than in a public issue.
