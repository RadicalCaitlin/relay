using Relay.Cli.Ui;

namespace Relay.Tests;

/// <summary>
/// Reading one message from a keyboard: which lines arrived together, and so belong to the same
/// message. The clock is the test's own, so what counts as "together" is decided by the reader's
/// own window and never by how fast the machine running the test happens to be.
/// </summary>
public class PastedInputTests
{
    private static readonly TimeSpan InsideTheWindow = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan AfterTheWindow = TimeSpan.FromMilliseconds(60);

    /// <summary>A clock that moves only when the test or the keyboard below moves it.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan time) => _ticks += time.Ticks;
    }

    /// <param name="After">How long after the line before it this one arrives.</param>
    private sealed record Arrival(string Line, TimeSpan After);

    /// <summary>
    /// A keyboard whose lines arrive at times the test chooses. Looking to see whether a key is
    /// waiting moves the clock on, as looking at a real keyboard takes time: that is the only
    /// thing here that does, so nothing waits for real time.
    /// </summary>
    private sealed class Typist(TestClock clock, bool redirected, params Arrival[] arrivals) : IConsoleStreams
    {
        private static readonly TimeSpan PerLook = TimeSpan.FromMilliseconds(5);

        private int _next;
        private TimeSpan _since;

        public bool IsInputRedirected => redirected;

        public bool IsOutputRedirected => false;

        public bool InputWaiting
        {
            get
            {
                clock.Advance(PerLook);
                _since += PerLook;
                return _next < arrivals.Length && _since >= arrivals[_next].After;
            }
        }

        public void DiscardPendingInput()
        {
        }

        public string? ReadLine()
        {
            if (_next >= arrivals.Length)
                return null;

            _since = TimeSpan.Zero;
            return arrivals[_next++].Line;
        }
    }

    private static PastedInput Reader(params Arrival[] arrivals)
    {
        var clock = new TestClock();
        return new PastedInput(new Typist(clock, redirected: false, arrivals), clock);
    }

    private static Arrival Together(string line) => new(line, TimeSpan.Zero);

    [Fact]
    public void Lines_that_are_already_waiting_are_one_message()
    {
        var reader = Reader(Together("@claude look at this"), Together("line two"), Together("line three"));

        Assert.Equal(new TypedInput("@claude look at this\nline two\nline three", 3), reader.Read());
    }

    [Fact]
    public void A_line_that_arrives_inside_the_window_joins_the_message()
    {
        var reader = Reader(Together("first"), new("second", InsideTheWindow));

        Assert.Equal(new TypedInput("first\nsecond", 2), reader.Read());
    }

    [Fact]
    public void A_line_that_arrives_after_the_window_is_a_message_of_its_own()
    {
        var reader = Reader(Together("first"), new("second", AfterTheWindow));

        Assert.Equal(new TypedInput("first", 1), reader.Read());
        Assert.Equal(new TypedInput("second", 1), reader.Read());
    }

    [Fact]
    public void A_line_ending_in_a_backslash_waits_however_long_the_next_line_takes()
    {
        var reader = Reader(Together(@"first\"), new("second", TimeSpan.FromSeconds(30)));

        Assert.Equal(new TypedInput("first\nsecond", 2), reader.Read());
    }

    [Fact]
    public void A_backslash_at_the_end_of_the_last_line_is_still_not_sent()
    {
        var reader = Reader(Together(@"a path ending in \"));

        Assert.Equal(new TypedInput("a path ending in", 1), reader.Read());
    }

    [Fact]
    public void The_blank_lines_and_indentation_of_a_paste_are_kept()
    {
        var reader = Reader(Together("first"), Together(""), Together("    indented"), Together(""));

        Assert.Equal(new TypedInput("first\n\n    indented\n", 4), reader.Read());
    }

    // A block is never trimmed as a whole: trimming one that begins with a blank line would promote
    // its second line into the first, where a /command runs and a mention addresses someone.
    [Fact]
    public void A_block_that_begins_with_a_blank_line_keeps_it()
    {
        var reader = Reader(Together(""), Together("/approvals off"));

        Assert.Equal(new TypedInput("\n/approvals off", 2), reader.Read());
    }

    [Fact]
    public void A_single_line_is_trimmed_as_a_typed_line_always_was()
    {
        var reader = Reader(Together("   yes   "));

        Assert.Equal(new TypedInput("yes", 1), reader.Read());
    }

    [Fact]
    public void The_end_of_input_ends_the_block_and_then_the_reading()
    {
        var reader = Reader(Together("first"), Together("second"));

        Assert.Equal(new TypedInput("first\nsecond", 2), reader.Read());
        Assert.Null(reader.Read());
    }

    [Fact]
    public void With_redirected_input_every_line_is_still_its_own_message()
    {
        var clock = new TestClock();
        var reader = new PastedInput(
            new Typist(clock, redirected: true, Together("first"), Together(@"second\"), Together("third")),
            clock);

        Assert.Equal(new TypedInput("first", 1), reader.Read());
        Assert.Equal(new TypedInput(@"second\", 1), reader.Read());
        Assert.Equal(new TypedInput("third", 1), reader.Read());
    }
}
