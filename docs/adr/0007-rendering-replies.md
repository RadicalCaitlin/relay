# ADR 0007: Rendering agent replies, and highlighting what Relay acted on

- **Status:** Accepted
- **Date:** 2026-10-17
- **Issue:** #40

## Context

Agent replies are printed as the raw Markdown the agent wrote: backticks around code, `-` for
bullets, `**` for bold, ```` ``` ```` around blocks. A reply of any length is hard to read. Nothing in
what Relay prints shows which `@mention` it acted on, and an activity line for a skill says only
`Skill`, because the skill's name is in a field Relay does not read (`{"skill":"code-review"}`).

What constrains the answer:

- A reply is untrusted text. Today it reaches the terminal only through `RelayConsole.Safe`, which
  removes control and bidirectional characters and escapes Spectre markup, so it cannot move the
  cursor, choose a colour or make a link. Rendering must keep all of that.
- `@mentions` are routing. Highlighting one Relay did not act on would tell the user something
  happened that did not.
- No package dependency without discussing it in the issue (`AGENTS.md`).
- The user's own input is read with `Console.ReadLine`, so Relay cannot style it as it is typed.
  #67 replaces that reader.

## Decision

**Relay renders the common parts of Markdown in agent replies with styles of its own, and
highlights a mention only where it read it as an address. The agent's text can pick which of
Relay's few styles a span gets, and nothing else.**

### What is rendered

| Written | Shown |
| --- | --- |
| `# Heading` to `###### Heading` | Bold; levels 1 and 2 also underlined. The `#` marks are not shown |
| `- item`, `* item`, `+ item` | `•` and the item, keeping its indent |
| `1. item` | As written, number kept |
| `**bold**` | Bold |
| `***both***` | Bold and italic |
| `*italic*` | Italic. `_x_` is left alone: it is too common in names such as `snake_case_name` |
| `` `code` `` | In Relay's code colour, contents shown exactly, nothing inside rendered |
| ```` ``` ```` fenced block | Each line shown exactly, in the code colour, behind a dim `│`; the language name, if any, dim above it |
| `> quote` | Dim, behind a dim `│` |
| Anything else (tables, links, images, HTML, rules such as `---` or `* * *`) | As written |

- A marker that is not closed on the same line is shown as written. Emphasis opens only at the
  start of a word and closes only at the end of one, so a glob such as `**/*.cs` or a product
  `a*b*c` is shown as written. A code span is passed over whole, so a marker inside it never
  closes emphasis around it. As in CommonMark, a span closes only at a run of exactly as many
  backticks as opened it.
- A fenced block ends only at a fence at least as long as the one that opened it, so a block can
  show another block.
- A line longer than 4,000 characters is shown as written: finding closing markers on a line
  made mostly of unclosed ones costs time in proportion to the square of its length.
- Without a terminal that can show Unicode, `-` stands for `•` and `|` for `│`.
- Links are never made. `[text](url)` stays as written, so an agent cannot make text that opens
  somewhere other than it says.
- Styles are bold, italic, underline, dim and one colour for code, chosen to read on both light
  and dark terminals.
- `/history` shows messages the same way.

### How it stays safe

Rendering is one pure function in `Relay.Cli.Ui`, `ReplyRenderer.Render(string) -> string`, from
reply text to Spectre markup:

1. Control and bidirectional characters are removed first, as `RelayConsole.Safe` does.
2. The text is split into lines and spans by the rules above.
3. Every span of the agent's text is escaped with `Markup.Escape` on its own, and only then
   wrapped in one of Relay's fixed style tags.

So the only markup in the result is Relay's. This replaces "renders text that has already been
through `Safe`" in #40 with an order that is easier to get right: clean, split, then escape each
piece. The tests feed it hostile text: markup, escape sequences, unclosed and nested markers,
very long lines.

### Highlighting

- **Mentions.** The router records on each message how many characters at its start are
  mentions it acted on (`ChatMessage.AddressLength`), and only those are shown in the style Relay
  uses for agent names. The renderer never reads the text for mentions itself, so it cannot
  disagree with the router. For a human message that is the leading mentions it was sent to,
  including after a comma or colon; a message Relay refused to send (no such agent, an
  unavailable one, nothing to send) has none. For an agent's reply it is the `@id` of a handover
  to an agent in the room inside an exchange, when it is on the first line. A mention anywhere
  else is plain text. Whether a handover was then delivered is said, as now, by the notice that
  follows it.
- **Skills and tools.** In an activity line for a step, the tool's name is highlighted. Claude's
  `Skill` tool is shown with the skill it ran (`Skill: code-review`), and `skill` is read like the
  other fields that say what a tool was used on.
- **`/commands` in the user's own input** move to #67. Relay does not print the user's line back,
  so there is nothing to highlight until it reads keys itself.

### Not done here

- Wrapping, reflowing or paging long replies. The terminal wraps as now.
- Syntax highlighting inside code blocks.
- Rendering anything an agent is sent. Rendering is display only: the room, `/history`'s source
  and what other agents read keep the text exactly as written.

## Alternatives considered

| Option | Why not |
| --- | --- |
| Markdig, or another Markdown library | A new dependency, and its output still has to be mapped to Spectre markup and escaped piece by piece, which is most of the work and all of the risk. |
| Render every Markdown feature | Tables and links are where most of the complexity and all of the link risk are, for little gain in a terminal. |
| Strip the markers instead of styling | Loses what the agent meant to stress, and code becomes hard to tell from prose. |
| Highlight every `@word` that names an agent | Says Relay acted on mentions it ignored, which is what ADR 0002 keeps from being routing. |
| Highlight `/commands` by printing the user's line again | Doubles every line typed. #67 does it properly. |

## Consequences

- Replies are easier to read, and code stands out.
- What an agent writes can now change how a reply looks, within Relay's own few styles. It still
  cannot add colour of its choosing, links, or anything that reaches the terminal as a sequence.
  `docs/threat-model.md` records this.
- A reply that uses `*` or a backtick for something other than Markdown may be shown styled.
  Anything unclosed is shown as written.
- `ReplyRenderer` is a new place where escaping matters. `ReplyRendererTests` renders thousands of
  hostile and random texts, with and without a highlighted address, and asserts that each result
  parses as markup and holds only `ReplyRenderer.Styles`.
