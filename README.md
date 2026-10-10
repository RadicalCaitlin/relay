# Relay

Your agents. One conversation.

Relay is a local, cross-platform terminal chatroom for AI coding agents. It discovers the agent
CLIs you already have installed (Claude Code and OpenAI Codex), puts them in a shared room with
you, and routes your messages to the agent you address with an `@mention`.

Relay drives each agent through its own official CLI. It needs no API keys of its own, runs no
backend server, and never changes an agent's permission or sandbox settings.

```
╭──────────────────────────────────────╮
│                RELAY                 │
│    Your agents. One conversation.    │
╰──────────────────────────────────────╯

● Claude       connected
● Codex        connected

# general  /help for commands

You: @claude Say hello to Relay.
Claude: Hello, Relay!

You: @codex What is 41 + 1? Reply with only the number.
Codex: 42
```

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows, macOS, or Linux
- At least one agent CLI, installed on your `PATH` and already signed in:
  - Claude Code (`claude`)
  - OpenAI Codex (`codex`)

Relay still runs without them; missing agents are shown as unavailable.

## Build and test

```
dotnet build
dotnet test
```

## Run

```
dotnet run --project src/Relay.Cli -- <command>
```

| Command           | Description                                              |
| ----------------- | -------------------------------------------------------- |
| `relay`           | Home screen: banner, agent status, and available commands |
| `relay agents`    | List agent providers, install status, version, and path  |
| `relay start`     | Start an interactive chatroom                            |
| `relay --version` | Print the Relay version                                  |
| `relay --help`    | Show help                                                |

Inside the chatroom:

| Input               | Effect                                              |
| ------------------- | --------------------------------------------------- |
| `@claude <message>` | Send the message to Claude and show its reply       |
| `@codex <message>`  | Send the message to Codex and show its reply        |
| any other text      | Record the message in the room without sending it   |
| `/agents`           | Show discovered agents                              |
| `/history`          | Replay the room's message history                   |
| `/clear`            | Clear the display (history is kept)                 |
| `/reset`            | Start a new conversation with every agent           |
| `/reset <agent>`    | Start a new conversation with one agent             |
| `/help`             | Show the command list                               |
| `/exit`             | Leave the chatroom                                  |
| `Ctrl+C`            | Cancel the request in flight; exit when idle        |

Agents run in the directory you start Relay from, with whatever access their own configuration
grants them there. Start Relay only in directories you trust.

## Agent support

| Agent        | Discovery | Chat | How Relay talks to it                                                  |
| ------------ | --------- | ---- | ---------------------------------------------------------------------- |
| Claude Code  | yes       | yes  | `claude -p --output-format json`, continued with `--resume <id>`       |
| OpenAI Codex | yes       | yes  | `codex exec --json -`, continued with `codex exec resume <id> --json -` |

Both integrations were verified against Claude Code 2.1.273 and codex-cli 0.162.0 on Windows.

## MVP limitations

- One room (`general`), held in memory. Nothing is persisted between runs.
- An agent sees only the messages addressed to it, not the rest of the room. Each agent keeps
  its own conversation for the lifetime of the room, or until you `/reset` it.
- Replies arrive whole when the agent finishes; there is no streaming.
- Agents cannot answer permission prompts through Relay. Tool use that would need approval is
  declined by the agent CLI, so agents are effectively limited to what their own configuration
  already allows. Codex also refuses to run outside a Git repository.
- One agent per message, and agents never trigger each other.
- Not yet packaged; run it with `dotnet run`.

## Near-term roadmap

- Shared room context, so agents can see what others said
- Controlled agent-to-agent collaboration with turn limits and loop prevention
- Streaming replies and tool-activity display
- Surfacing agent permission prompts to the human
- Multiple rooms and persisted history
- Packaging as a .NET global tool and native executables

## Contributing

See [AGENTS.md](AGENTS.md) for architecture, conventions, and the rules for process execution.
