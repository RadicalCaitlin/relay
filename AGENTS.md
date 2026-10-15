# AGENTS.md

Guidance for coding agents (Claude Code, Codex, and others) and humans working on Relay.

## Purpose

Relay is a local, cross-platform terminal chatroom in which a human and several AI coding agents
share one conversation. It drives agents through their installed CLIs. See `README.md` for the
user-facing description, limitations, and roadmap.

## Architecture

Three concepts: an **agent** receives input and returns a response, a **room** is a shared
conversation, and the **relay** (the router) decides which participant receives a message.

| Project            | Contains                                                                 | May reference      |
| ------------------ | ------------------------------------------------------------------------ | ------------------ |
| `src/Relay.Core`   | Domain models, provider/session contracts, rooms, routing                | nothing            |
| `src/Relay.Agents` | Process execution, executable discovery, Claude and Codex providers      | Core               |
| `src/Relay.Cli`    | Spectre.Console commands, chat UI, dependency injection wiring           | Core, Agents       |
| `tests/Relay.Tests`| xUnit tests                                                              | Core, Agents, Cli  |

Boundaries that must hold:

- `Relay.Core` has no package dependencies and knows nothing about processes, CLIs, or the console.
- `Relay.Agents` never references `Relay.Cli` and never writes to the console.
- `System.Diagnostics.Process` is used only in `Relay.Agents/Processes/ProcessRunner.cs`.

Key types:

- `Relay.Core.Agents`: `IAgentProvider` (discovery plus session creation), `IAgentSession`
  (send a message, lifecycle state), `AgentDiscoveryService`, `AgentException` (a failure whose
  message is safe to show the user), `AgentActivity` (a step an agent reports while it works).
- `Relay.Core.Rooms`: `ChatRoom`, `ChatMessage`, `SenderType`, `AgentParticipant`, `IRoomManager`,
  `AgentRole` (what the user has asked an agent to be; held by the room, per agent).
- `Relay.Core.Routing`: `Mention` (parses a leading `@target`, and an agent's handover), `MessageRouter`
  (runs an exchange and reports why it stopped), `ExchangeLimits`, `SharedContext` (builds what an
  agent is sent: the room messages it has missed, then the user's message or another agent's handover).
- `Relay.Core.Workspaces`: `WorkspaceTrustStore` (which folders the user trusts).
- `Relay.Cli.Commands`: `WorkspaceTrustPrompt` (whether this folder is trusted, asking the user if
  it must, and what the header says about it).
- `Relay.Agents.Processes`: `IProcessRunner`, `IExecutableLocator`.
- `Relay.Agents.Cli`: `CliAgentProvider` and `CliAgentSession`, shared by all CLI-backed providers.

Message flow: `StartCommand` reads a line, `MessageRouter` records it in the room, and if it
starts with `@id` the router calls that participant's `IAgentSession`. The reply, or a system
notice explaining the failure, is recorded in the room and returned for display.

Shared context: before calling the agent, the router asks the room what that agent has not seen
and `SharedContext` puts it in front of the message as a labelled transcript. The decisions behind
this are in `docs/adr/0001-shared-room-context.md`; read it before changing what agents are shown.

Exchanges: a message that begins with several mentions opens an exchange between those agents.
Inside it the router delivers a reply whose first line is a mention of an invited agent, for at most
`ExchangeLimits.MaxTurns` turns, and returns a `StopReason`. `StartCommand` shows each message as
the router records it and, in pause mode, answers the router's question about each handover.
Read `docs/adr/0002-agent-to-agent-exchanges.md` before changing who can start a turn.

Roles: `/role` gives an agent an `AgentRole` for the session. `SharedContext` states it in Relay's
introduction on every turn and names each agent's role in an exchange. A role changes nothing
about routing, limits or permissions. See `docs/adr/0003-agent-roles.md`.

Activity: `ProcessRunner` hands each line of the CLI's output to the session as it is written.
The provider's `ParseActivity` turns a line into `AgentActivity` items, the session holds back the
last note of a turn because that is the reply (and reports it if the turn fails, when there is no
reply), and the router passes the rest to
`ExchangeEvents.Activity` for `StartCommand` to show. See `docs/adr/0004-agent-activity.md`.

## Commands

```
dotnet build                                      # build everything
dotnet test                                       # run all tests
dotnet test --filter "FullyQualifiedName~Router"  # run a subset
dotnet run --project src/Relay.Cli -- agents      # run the CLI
dotnet run --project src/Relay.Cli -- start
git config core.hooksPath .githooks               # once per clone: block commits that contain secrets
```

The build must stay free of warnings and the tests must pass before a change is finished.

## Coding conventions

- .NET 10, C# with nullable reference types and implicit usings enabled.
- File-scoped namespaces, primary constructors, collection expressions, records for immutable data.
- Async methods end in `Async` and accept a `CancellationToken`.
- Expected agent failures are `AgentException`s with a user-readable message. Do not catch
  exceptions you cannot handle, and do not add handling for cases that cannot occur.
- A failure message says what went wrong in terms the user can act on. `MessageRouter` turns any
  exception from a provider into a notice in the room, so one agent failing never ends the chat;
  that catch-all is for the provider boundary only, not a pattern to copy elsewhere.
- A failed, cancelled or timed-out turn must leave a session usable and its conversation intact.
  `relay start --turn-timeout` sets `AgentSessionOptions.TurnTimeout`; a turn that runs past it is
  an `AgentException` with `TimedOut` set, and the router ends with `StopReason.TimedOut`.
- Comments explain why, not what. No commented-out code.
- Add an abstraction only when something concrete needs it (a second implementation or a test seam).
- No new package dependencies without a clear need. No database, networking, or Aspire.

## Testing

- Tests must not require Claude or Codex to be installed, signed in, or reachable.
- Test routing with fake sessions, and providers with `FakeProcessRunner` and `FakeLocator`
  (`tests/Relay.Tests/Fakes.cs`).
- Test output parsers with captured sample output.
- `ProcessRunner` tests may launch only tools guaranteed to exist (`dotnet`, `ping`, `sleep`, `sh`,
  and Windows PowerShell on Windows).
- `CliSmokeTests`, `ChatSecurityTests` and `ChatSignalTests` run the built CLI through `RelayHarness`.
  Use it for anything that must hold end to end, and add a smoke test for every new command or
  chat command.
- `RelayHarness` gives Relay a `PATH` holding only its scratch folder and the operating system's
  own tools, so the tests behave the same whether or not real agent CLIs are installed.
  `InstallAgent` adds a fake `claude` or `codex` that records its arguments and standard input and
  prints whatever the test tells it to.
- Assert on Relay's output with `RelayHarness.AssertShown`, which ignores line wrapping.
- `RelayHarness` redirects every stream, so the folder-trust question can never be reached through
  it. `WorkspaceTrustPromptTests` drives `WorkspaceTrustPrompt` in process instead, with Spectre's
  `TestConsole` and a fake `IConsoleStreams`. That seam is supplied in code only: nothing in the
  environment may make Relay ask, or answer, the trust question.
- `ChatSignalTests` sends the CLI real signals.
  It is marked `[UnixFact]` and is skipped on Windows, where signals cannot be sent to a process.
- `CodexFolderTrustLiveTests` is the one exception to the first rule: it runs the installed Codex
  against a hostile folder, and is the evidence for what the threat model says about Codex. It is
  marked `[LiveCodexFact]` and skipped unless `RELAY_LIVE_CODEX=1`. It gives Codex a throwaway
  `CODEX_HOME`, no credentials and no reachable model, and plants only commands that write a marker
  file. Keep all of that true, and run it when Codex is upgraded or its arguments change.
- `ClaudeApprovalLiveTests` is the other exception: it runs the installed Claude Code through
  Relay's approval tool, and is the evidence for what ADR 0005 says Claude Code does. It is marked
  `[LiveClaudeFact]` and skipped unless `RELAY_LIVE_CLAUDE=1`. It uses the account Claude Code is
  signed in to, for two short turns. Run it when Claude Code is upgraded or its arguments change.
- `ApprovalTests` asks the questions in process, through `ApprovalDesk`, for the same reason the
  folder-trust question is: with redirected streams Relay asks nobody.
- `StartCommandApprovalTests` runs the chatroom itself in process, with a fake `IConsoleStreams`
  as the keyboard and an agent that asks over the real pipe. Use it for what depends on when a
  line is typed, or on how Relay leaves.

## Adding an agent provider

1. Read the CLI's own `--help` and documentation. Identify its documented non-interactive mode
   and confirm by running it that it works with redirected stdin and stdout.
2. Add a folder under `src/Relay.Agents/` with a provider deriving from `CliAgentProvider` and a
   separate static output parser.
3. In the provider, supply the id (the `@mention` handle), display name, command name,
   `BuildArguments`, and `ParseReply`. Discovery and session handling are inherited. Override
   `ParseActivity` if the CLI reports what it is doing as it goes.
4. Register it in `ServiceCollectionExtensions.AddRelayAgents`.
5. Add parser and argument tests, and update the support table in `README.md`.

An agent that is not CLI-backed implements `IAgentProvider` and `IAgentSession` directly.

## Security and process execution rules

`docs/threat-model.md` is the security model. Read it before changing anything in this section,
and update it in the same change.

- The working directory (the current folder, or `relay start --workspace`) is untrusted unless
  `AgentSessionOptions.TrustWorkspace` is set. A provider must not let folder content configure the
  agent or run commands when it is false. For Claude that means `--setting-sources user`. A new provider must document how it meets this, with evidence.
- Use only documented CLI arguments. Never guess or invent flags.
- Never pass flags that auto-approve tool use or weaken sandboxing, such as
  `--dangerously-skip-permissions`, `--permission-mode bypassPermissions`,
  `--dangerously-bypass-approvals-and-sandbox`, or `--sandbox danger-full-access`. The agent's
  own configuration decides what it may do.
- Never bypass or work around authentication, subscription limits, or approval mechanisms.
- User text goes to the agent on stdin, never as a command-line argument. On Windows the CLIs
  are `cmd.exe` shims, where argument quoting is not safe.
- Any value taken from agent output and placed on a command line must be validated first, as
  `CliAgentSession` does for session ids. It must not be able to start with `-`, or it becomes a flag.
- Discovery must not launch the agent unless the caller asks for its version.
- Never start a process through a shell (`UseShellExecute` stays `false`).
- Resolve executables through `IExecutableLocator`. Never hardcode install paths, and never resolve
  from a relative `PATH` entry or the working directory. `ProcessRunner` also removes relative
  entries from the `PATH` the child inherits.
- Every process run must be cancellable and must kill the whole process tree on cancellation.
- When Relay is told to stop (Ctrl+C, terminal closed, SIGTERM, SIGHUP) the request in flight is
  cancelled first, so no agent process outlives Relay. A forced kill of Relay cannot be handled.
- Never log, print, or persist environment variables, tokens, or credentials. Never read an
  agent's credential files.
- Never commit a secret, including in tests: build fake credentials at run time rather than
  writing them as literals. CI scans each pull request's commits with gitleaks, and `.githooks/pre-commit`
  does the same for staged changes once enabled with `git config core.hooksPath .githooks`.
  Do not bypass the hook.
- Folder trust is remembered by `WorkspaceTrustStore`, per exact folder, in the user's own
  configuration folder. Nothing in the working directory may influence where that file is read from.
- Treat agent output, CLI error text and version strings as untrusted: print them only through
  `RelayConsole.Safe`, which strips control and bidirectional characters and escapes markup.
  Print folder names and anything else that must stay on one line through `RelayConsole.SafeLine`.
- Activity is for the user's eyes only. Never record it in the room, never include it in what an
  agent is sent, and never act on it: a reported refusal is shown, not approved or retried. Print
  it through `RelayConsole.ActivityLine`, which keeps each item to one short line.
- `ParseActivity` runs on every line an agent CLI prints, as it prints it. It must cope with any
  text and never throw.
- Never put an invisible or control character in source as a literal. Write it as a `\uXXXX` escape.
- A security fix needs a regression test that fails without it. Check that by removing the fix.
- An agent starts another agent's turn only inside an exchange: the user named both agents at the
  start of their message, and the reply's first line is only a mention of the other
  (`Mention.TryParseHandover`). Outside an exchange a reply is never parsed for one. Nothing else an agent
  writes starts a turn, invites an agent, or is read as a `/command`.
- Every exchange is bounded by `ExchangeLimits.MaxTurns`, one agent runs at a time, and a reply
  addresses at most one agent. Do not add a path that starts a turn without counting it.
- A handed-over message is sent with `SharedContext.ComposeHandover`: inside the transcript, under
  its author's name. Only Relay's own words follow the transcript on such a turn.
- The user never edits an agent's message. At a pause they deliver it, stop, or write to its author.
- Role text comes only from Relay's built-in list or from what the user types into `/role`. Never
  take it from agent output or from a file in the working folder: it is sent to the agent in
  Relay's voice on every turn.
- A role is never described as, or used as, a limit on what an agent can do. Relay does not touch
  an agent's permissions, so it must not suggest that it has.
- No role description may name a provider. A test asserts this for the built-in ones.
- Pause mode is set with `/pause on` and `/pause off`, never toggled. At a pause, a line that looks
  like a command or is addressed to another agent is asked again, never sent to an agent.
- Read the console through `StartCommand.ReadLine`, which keeps a read that was interrupted. A
  second `Console.ReadLine` would lose the next line typed to the abandoned one. The line of a read
  begun for a question that was then withdrawn is never passed on: the user is told and asked again.
- Relay never answers for the user. A step an agent may not take is put to them only through
  `ApprovalDesk`, and is allowed only by a `yes` typed after the question was shown: never by an
  empty line, a line typed earlier, a default, a timeout or a setting. See
  `docs/adr/0005-approvals.md`.
- An answer covers the one step shown. Never remember it, never write a permission rule, and never
  pass on a CLI's suggestion to do so.
- Before the user is asked, show them what would run, not the agent's description of it. A command
  is shown whole or the step is refused without asking. Anything else that is cut short says how
  much is missing. Every line of it goes through `ApprovalText`, behind Relay's own indent.
- A request is answered only in the turn it arrived in, and only while whoever asked is still
  waiting. `ApprovalDesk.Decide` refuses it otherwise, without asking.
- Offer an agent the approval tool only when somebody can be asked: input and output are not
  redirected and the console can interact. Otherwise pass nothing, and its CLI refuses the step.
- `relay approval-tool` writes nothing to its standard output but the protocol, and decides
  nothing. If it cannot reach the chatroom, the answer is no.
- The file that tells an agent's CLI where to find the tool is written in a folder Relay creates
  outside the working directory. Its path goes on a command line, so a path holding a character
  `cmd.exe` gives meaning to inside quotes is not used.
- Everything an agent is sent goes through `SharedContext.Compose`. Any text that did not come from
  the user's current message must sit inside the transcript, escaped, with its author named. Never
  place another participant's words where they could read as the user's or as Relay's.
- What an agent has seen is tracked by its `ChatRoom`, and an agent belongs to one room. Do not
  share a session, or anything derived from one room's messages, with another room.
- A provider's session must report `RemembersConversation` truthfully, and an `AgentException`
  raised before the agent could have received anything must set `PromptMayHaveBeenRead = false`.
  The router relies on both to decide what an agent has seen.

## Expectations for coding agents modifying Relay

- Keep changes small and within the project boundaries above.
- Run `dotnet build` and `dotnet test` and report the real results. If something could not be
  verified, such as a live agent call, say so rather than claiming success.
- Do not weaken, skip, or delete tests to make a change pass.
- Do not commit secrets, push, create releases, or change repository settings without explicit
  approval from the maintainer.
- Update `README.md` and this file when behavior, commands, or architecture change.

## Working on an issue as agents in a room

When two agents take an issue from discussion to a pull request through Relay itself:

- Start from the issue. Quote its acceptance criteria into the room, so the agent that cannot
  reach GitHub has them too.
- Run one command per call. A chain such as `git add -A && git commit ...` is refused by an
  agent's permission rules even when each command alone is allowed, and Relay cannot approve it.
- Review against `docs/review-checklist.md`, and write findings in the shape it asks for. The
  reviewer forms its own view from the diff and the issue before reading the author's summary.
- The author answers every finding on the pull request: fixed, or not fixed and why. Nothing is
  set aside in the room alone, where the maintainer will not see it.
- A pull request is not ready until CI has finished and passed. Check it; do not assume it.
- Say what you verified and what you only reasoned, every time.
- Merging is the maintainer's decision. Do not merge, or use the override of the ruleset on
  `main`, unless the maintainer has told you to for that pull request. Never change repository
  settings.
