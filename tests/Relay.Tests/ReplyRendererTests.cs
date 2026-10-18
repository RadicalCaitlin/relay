using Relay.Cli.Ui;
using Relay.Core.Rooms;
using Relay.Core.Routing;
using Spectre.Console;
using static Relay.Tests.RelayHarness;

namespace Relay.Tests;

/// <summary>
/// How a message is shown (ADR 0007): the Markdown that is rendered, the mentions that are
/// highlighted, and that an agent's text can never add markup of its own.
/// </summary>
public sealed class ReplyRendererTests : IDisposable
{
    private readonly RelayHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static ChatRoom Room(string claudeReply = "x", bool codexAvailable = true)
    {
        var room = new InMemoryRoomManager().CreateRoom("general");
        room.AddParticipant(new AgentParticipant("claude", "Claude", new FakeSession(claudeReply)));
        room.AddParticipant(codexAvailable
            ? new AgentParticipant("codex", "Codex", new FakeSession("x"))
            : new AgentParticipant("codex", "Codex", null, "not installed"));
        return room;
    }

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("# Title", "[bold underline]Title[/]")]
    [InlineData("## Title ##", "[bold underline]Title[/]")]
    [InlineData("### Using C#", "[bold]Using C#[/]")]
    [InlineData("#nospace", "#nospace")]
    [InlineData("- item", "• item")]
    [InlineData("  * nested item", "  • nested item")]
    [InlineData("+ item", "• item")]
    [InlineData("12. twelfth", "12. twelfth")]
    [InlineData("> quoted", "[dim]│ quoted[/]")]
    [InlineData("**bold** and *italic*", "[bold]bold[/] and [italic]italic[/]")]
    [InlineData("**bold *and italic***", "[bold]bold [italic]and italic[/][/]")]
    [InlineData("**bold *and italic* too**", "[bold]bold [italic]and italic[/] too[/]")]
    [InlineData("***Important***", "[bold][italic]Important[/][/]")]
    [InlineData("*a **b** c*", "[italic]a [bold]b[/] c[/]")]
    [InlineData("(*aside*), **done**.", "([italic]aside[/]), [bold]done[/].")]
    // A marker inside a code span never closes emphasis around it.
    [InlineData("**see `a**b`**", "[bold]see [teal]a**b[/][/]")]
    [InlineData("**see `a** b`**", "[bold]see [teal]a** b[/][/]")]
    // Globs and products are not emphasis.
    [InlineData("search **/*.cs and **/*.json", "search **/*.cs and **/*.json")]
    [InlineData("a*b*c and 2 * 3", "a*b*c and 2 * 3")]
    [InlineData("use `dotnet test` here", "use [teal]dotnet test[/] here")]
    [InlineData("``a `tick` inside``", "[teal]a `tick` inside[/]")]
    // Only a run of the same length closes a span, so it can hold a longer one.
    [InlineData("`a``b`", "[teal]a``b[/]")]
    [InlineData("**`a``b**`**", "[bold][teal]a``b**[/][/]")]
    [InlineData("code `**not bold**`", "code [teal]**not bold**[/]")]
    [InlineData("```inline fence```", "[teal]inline fence[/]")]
    // What is left alone.
    [InlineData("a * b * c", "a * b * c")]
    [InlineData("**unclosed", "**unclosed")]
    [InlineData("`unclosed", "`unclosed")]
    [InlineData("snake_case_name and _x_", "snake_case_name and _x_")]
    [InlineData("---", "---")]
    [InlineData("* * *", "* * *")]
    [InlineData("- - -", "- - -")]
    [InlineData("[a link](https://example.com)", "[[a link]](https://example.com)")]
    [InlineData("| a | b |", "| a | b |")]
    public void One_line_is_rendered_in_relays_own_styles(string line, string markup)
        => Assert.Equal(markup, ReplyRenderer.Render(line));

    [Fact]
    public void A_fenced_block_is_shown_exactly_behind_a_bar_with_nothing_inside_rendered()
        => Assert.Equal(
            "before\n[dim]csharp[/]\n[dim]│[/] [teal]var x = **y**; // [[red]][/]\n[dim]│[/]\n[dim]│[/] [teal]# not a heading[/]\nafter",
            ReplyRenderer.Render("before\n```csharp\nvar x = **y**; // [red]\n\n# not a heading\n```\nafter"));

    // A longer fence holds a shorter one, as when a reply shows how to write a block.
    [Fact]
    public void A_block_ends_only_at_a_fence_as_long_as_the_one_that_opened_it()
        => Assert.Equal(
            "[dim]md[/]\n[dim]│[/] [teal]```bash[/]\n[dim]│[/] [teal]ls[/]\n[dim]│[/] [teal]```[/]\n[dim]│[/] [teal]# still code[/]\nafter",
            ReplyRenderer.Render("````md\n```bash\nls\n```\n# still code\n````\nafter"));

    [Fact]
    public void Without_unicode_bullets_and_bars_are_plain_characters()
        => Assert.Equal("- item\n[dim]| quoted[/]", ReplyRenderer.Render("- item\n> quoted", unicode: false));

    [Fact]
    public void Control_and_bidirectional_characters_never_reach_the_output()
    {
        var output = ReplyRenderer.Render("**a\u001b[2Jb** `c‮d` \u0007e");

        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('‮', output);
        Assert.DoesNotContain('\u0007', output);
        Assert.Equal("[bold]a[[2Jb[/] [teal]cd[/] e", output);
    }

    [Fact]
    public void A_very_long_line_is_shown_as_written()
    {
        var line = string.Concat(Enumerable.Repeat("**a** ", 1000));

        Assert.Equal(Markup.Escape(line), ReplyRenderer.Render(line));
    }

    // Matching a heading with a closing-hash pattern was quadratic in a run of spaces.
    [Fact]
    public void A_heading_with_a_long_run_of_spaces_is_rendered_without_stalling()
        => Assert.StartsWith("[bold underline]a   ", ReplyRenderer.Render("# a" + new string(' ', 100_000) + "b"));

    // Whatever an agent writes, the output parses as markup and every tag in it is one of Relay's.
    [Fact]
    public void No_text_can_produce_markup_relay_did_not_add()
    {
        var random = new Random(40);
        const string alphabet = "[]/*`#->_+.1 \n\taZ@:\\|(),\"'";
        string[] hostile = ["[red]x[/]", "[link=https://evil]x[/]", "[/]", "[[", "]]", "[bold", "`[red]`", "**[/]**", "```\n[/]\n```", "# [red]", "> [link]"];

        for (var n = 0; n < 3000; n++)
        {
            var text = n < hostile.Length
                ? hostile[n]
                : new string([.. Enumerable.Range(0, random.Next(1, 60)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            var output = ReplyRenderer.Render(text, highlighted: random.Next(text.Split('\n')[0].Length + 1));

            _ = new Markup(output);
            Assert.All(Tags(output), tag => Assert.Contains(tag, ReplyRenderer.Styles.Append("dim").Append("/")));
        }
    }

    // What is highlighted is what the router recorded, so these route a message rather than build one.
    [Theory]
    [InlineData("@codex\nplease review", "[bold aqua]@codex[/]\nplease review")]
    // The router matches agent ids without regard to case; the mention is shown as written.
    [InlineData("@Codex\nplease review", "[bold aqua]@Codex[/]\nplease review")]
    // Only a mention on the first line is highlighted.
    [InlineData("\n@codex\nplease review", "\n@codex\nplease review")]
    // A reply for the user is plain, whatever it begins with or names later.
    [InlineData("@you\nDone.", "@you\nDone.")]
    [InlineData("I agree with @codex.", "I agree with @codex.")]
    public async Task Only_a_handover_to_an_agent_has_its_mention_highlighted(string reply, string markup)
    {
        var result = await new MessageRouter().SendHumanMessageAsync(Room(reply), "@claude @codex review this");

        Assert.Equal(markup, ReplyRenderer.Message(result.Exchange[0], unicode: true));
    }

    // Outside an exchange a reply is never read for a handover.
    [Fact]
    public async Task A_reply_outside_an_exchange_is_never_highlighted()
    {
        var result = await new MessageRouter().SendHumanMessageAsync(Room("@codex\nplease review"), "@claude review this");

        Assert.Equal("@codex\nplease review", ReplyRenderer.Message(result.Exchange[0], unicode: true));
    }

    [Theory]
    [InlineData("@claude hello", "[bold aqua]@claude[/] hello")]
    [InlineData("@claude @codex agree on **this**", "[bold aqua]@claude @codex[/] agree on [bold]this[/]")]
    [InlineData("@claude, look", "[bold aqua]@claude[/], look")]
    [InlineData("@claude, @codex review this", "[bold aqua]@claude, @codex[/] review this")]
    [InlineData("@claude: @codex: both", "[bold aqua]@claude: @codex[/]: both")]
    [InlineData("@claude @Override is missing", "[bold aqua]@claude[/] @Override is missing")]
    [InlineData("hello @claude", "hello @claude")]
    // The router stops at a character that is not a space, though the display removes it.
    [InlineData("@claude ‮@codex hi", "[bold aqua]@claude[/] @codex hi")]
    // A message Relay did not send is plain, whatever it begins with.
    [InlineData("@gemini hello", "@gemini hello")]
    [InlineData("@claude", "@claude")]
    public async Task A_human_message_has_highlighted_only_the_mentions_it_was_sent_to(string text, string markup)
    {
        var result = await new MessageRouter().SendHumanMessageAsync(Room(), text);

        Assert.Equal(markup, ReplyRenderer.Message(result.Message, unicode: true));
    }

    [Fact]
    public async Task A_message_to_an_unavailable_agent_is_plain()
    {
        var result = await new MessageRouter().SendHumanMessageAsync(Room(codexAvailable: false), "@codex hi");

        Assert.Equal(StopReason.NotSent, result.Reason);
        Assert.Equal("@codex hi", ReplyRenderer.Message(result.Message, unicode: true));
    }

    [Fact]
    public async Task The_chatroom_shows_replies_rendered_and_markup_in_them_as_text()
    {
        _harness.InstallAgent("claude", ClaudeReply(@"# Plan\n- **first** step\n- run `dotnet test`\n```\n[red]kept[/]\n```\n[link=https://x]y[/]"));

        var output = await _harness.ChatAsync(["start"], "@claude hi", "/history", "/exit");

        AssertShown("Claude: Plan", output);
        AssertShown("first step", output);
        AssertShown("run dotnet test", output);
        AssertShown("[red]kept[/]", output);
        AssertShown("[link=https://x]y[/]", output);
        AssertNotShown("**first**", output);
        AssertNotShown("`dotnet test`", output);
        AssertNotShown("# Plan", output);
    }

    // The tags in markup, leaving out the escaped brackets "[[" and "]]".
    private static IEnumerable<string> Tags(string markup)
    {
        for (var i = 0; i < markup.Length; i++)
        {
            if (markup[i] == '[' && i + 1 < markup.Length && markup[i + 1] == '[')
            {
                i++;
                continue;
            }

            if (markup[i] != '[')
                continue;

            var close = markup.IndexOf(']', i);
            yield return markup[(i + 1)..close];
            i = close;
        }
    }
}
