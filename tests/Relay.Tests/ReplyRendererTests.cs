using Relay.Cli.Ui;
using Relay.Core.Rooms;
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
    private readonly ChatRoom _room = new InMemoryRoomManager().CreateRoom("general");

    public ReplyRendererTests()
    {
        _room.AddParticipant(new AgentParticipant("claude", "Claude", new FakeSession("x")));
        _room.AddParticipant(new AgentParticipant("codex", "Codex", new FakeSession("x")));
    }

    public void Dispose() => _harness.Dispose();

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
    [InlineData("code `**not bold**`", "code [teal]**not bold**[/]")]
    [InlineData("```inline fence```", "[teal]inline fence[/]")]
    // What is left alone.
    [InlineData("a * b * c", "a * b * c")]
    [InlineData("**unclosed", "**unclosed")]
    [InlineData("`unclosed", "`unclosed")]
    [InlineData("snake_case_name and _x_", "snake_case_name and _x_")]
    [InlineData("---", "---")]
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

    // Whatever an agent writes, the output parses as markup and every tag in it is one of Relay's.
    [Fact]
    public void No_text_can_produce_markup_relay_did_not_add()
    {
        var random = new Random(40);
        const string alphabet = "[]/*`#->_+.1 \n\taZ@:\\|";
        string[] hostile = ["[red]x[/]", "[link=https://evil]x[/]", "[/]", "[[", "]]", "[bold", "`[red]`", "**[/]**", "```\n[/]\n```", "# [red]", "> [link]"];

        for (var n = 0; n < 3000; n++)
        {
            var text = n < hostile.Length
                ? hostile[n]
                : new string([.. Enumerable.Range(0, random.Next(1, 60)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            var output = ReplyRenderer.Render(text, highlighted: 0);

            _ = new Markup(output);
            Assert.All(Tags(output), tag => Assert.Contains(tag, ReplyRenderer.Styles.Append("dim").Append("/")));
        }
    }

    [Fact]
    public void An_agents_handover_has_its_mention_highlighted()
    {
        var handover = _room.AddMessage("claude", SenderType.Agent, "@codex\nplease review", targetId: "codex");

        Assert.Equal("[bold aqua]@codex[/]\nplease review", ReplyRenderer.Message(handover, _room, unicode: true));
    }

    // The router matches agent ids without regard to case, and so does the highlight.
    [Fact]
    public void A_handover_written_in_other_capitals_is_highlighted_as_written()
        => Assert.Equal(
            "[bold aqua]@Codex[/]\nplease review",
            ReplyRenderer.Message(_room.AddMessage("claude", SenderType.Agent, "@Codex\nplease review", targetId: "codex"), _room, unicode: true));

    [Fact]
    public void A_handover_that_begins_with_a_blank_line_is_shown_without_failing()
        => Assert.Equal(
            "\n@codex\nplease review",
            ReplyRenderer.Message(_room.AddMessage("claude", SenderType.Agent, "\n@codex\nplease review", targetId: "codex"), _room, unicode: true));

    [Theory]
    // Outside an exchange a reply is never read for a handover, so it is recorded as to the user.
    [InlineData("@codex\nplease review", ChatRoom.HumanId, "@codex\nplease review")]
    // A mention later in a reply is never an address.
    [InlineData("I agree with @codex.", "codex", "I agree with @codex.")]
    public void A_mention_relay_did_not_read_as_an_address_is_plain(string text, string target, string markup)
        => Assert.Equal(markup, ReplyRenderer.Message(_room.AddMessage("claude", SenderType.Agent, text, target), _room, unicode: true));

    // The target is what the router records: the first agent named, or nothing for a message to the room.
    [Theory]
    [InlineData("@claude hello", "claude", "[bold aqua]@claude[/] hello")]
    [InlineData("@claude @codex agree on **this**", "claude", "[bold aqua]@claude @codex[/] agree on [bold]this[/]")]
    [InlineData("@claude, look", "claude", "[bold aqua]@claude[/], look")]
    [InlineData("@claude, @codex review this", "claude", "[bold aqua]@claude, @codex[/] review this")]
    [InlineData("@claude: @codex: both", "claude", "[bold aqua]@claude: @codex[/]: both")]
    // An agent not in the room addressed nobody, and a mention after the first word is text.
    [InlineData("@gemini hello", "gemini", "@gemini hello")]
    [InlineData("hello @claude", null, "hello @claude")]
    [InlineData("@claude @Override is missing", "claude", "[bold aqua]@claude[/] @Override is missing")]
    // A message the router did not record as addressed is plain, whatever it begins with.
    [InlineData("@claude hello", null, "@claude hello")]
    public void A_human_message_has_only_the_mentions_that_addressed_it_highlighted(string text, string? target, string markup)
        => Assert.Equal(markup, ReplyRenderer.Message(_room.AddMessage(ChatRoom.HumanId, SenderType.Human, text, target), _room, unicode: true));

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
