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
| `tests/Relay.Tests`| xUnit tests                                                              | Core, Agents       |

Boundaries that must hold:

- `Relay.Core` has no package dependencies and knows nothing about processes, CLIs, or the console.
- `Relay.Agents` never references `Relay.Cli` and never writes to the console.
- `System.Diagnostics.Process` is used only in `Relay.Agents/Processes/ProcessRunner.cs`.

Key types:

- `Relay.Core.Agents`: `IAgentProvider` (discovery plus session creation), `IAgentSession`
  (send a message, lifecycle state), `AgentDiscoveryService`, `AgentException` (a failure whose
  message is safe to show the user).
- `Relay.Core.Rooms`: `ChatRoom`, `ChatMessage`, `SenderType`, `AgentParticipant`, `IRoomManager`.
- `Relay.Core.Routing`: `Mention` (parses a leading `@target`), `MessageRouter`.
- `Relay.Core.Workspaces`: `WorkspaceTrustStore` (which folders the user trusts).
- `Relay.Agents.Processes`: `IProcessRunner`, `IExecutableLocator`.
- `Relay.Agents.Cli`: `CliAgentProvider` and `CliAgentSession`, shared by all CLI-backed providers.

Message flow: `StartCommand` reads a line, `MessageRouter` records it in the room, and if it
starts with `@id` the router calls that participant's `IAgentSession`. The reply, or a system
notice explaining the failure, is recorded in the room and returned for display.

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
- `ChatSecurityTests` and `ChatSignalTests` run the built CLI through `RelayHarness`, with a fake
  `claude` first on `PATH`. Use it for anything that must hold end to end.
- `ChatSignalTests` sends the CLI real signals.
  It is marked `[UnixFact]` and is skipped on Windows, where signals cannot be sent to a process.

## Adding an agent provider

1. Read the CLI's own `--help` and documentation. Identify its documented non-interactive mode
   and confirm by running it that it works with redirected stdin and stdout.
2. Add a folder under `src/Relay.Agents/` with a provider deriving from `CliAgentProvider` and a
   separate static output parser.
3. In the provider, supply the id (the `@mention` handle), display name, command name,
   `BuildArguments`, and `ParseReply`. Discovery and session handling are inherited.
4. Register it in `ServiceCollectionExtensions.AddRelayAgents`.
5. Add parser and argument tests, and update the support table in `README.md`.

An agent that is not CLI-backed implements `IAgentProvider` and `IAgentSession` directly.

## Security and process execution rules

`docs/threat-model.md` is the security model. Read it before changing anything in this section,
and update it in the same change.

- The working directory is untrusted unless `AgentSessionOptions.TrustWorkspace` is set. A provider
  must not let folder content configure the agent or run commands when it is false. For Claude that
  means `--setting-sources user`. A new provider must document how it meets this, with evidence.
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
  from a relative `PATH` entry or the working directory.
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
- A security fix needs a regression test that fails without it. Check that by removing the fix.
- Agents never trigger other agents. Only human messages are routed.

## Expectations for coding agents modifying Relay

- Keep changes small and within the project boundaries above.
- Run `dotnet build` and `dotnet test` and report the real results. If something could not be
  verified, such as a live agent call, say so rather than claiming success.
- Do not weaken, skip, or delete tests to make a change pass.
- Do not commit secrets, push, create releases, or change repository settings without explicit
  approval from the maintainer.
- Update `README.md` and this file when behavior, commands, or architecture change.
