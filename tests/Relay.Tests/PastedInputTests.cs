using Relay.Cli.Ui;

namespace Relay.Tests;

/// <summary>
/// Reading one message from a keyboard: which lines arrived together, and so belong to the same
/// message. The clock is the test's own, so what counts as "together" is decided by the reader's
/// own window and never by how fast the machine running the test happens to be.
/// </summary>
public class PastedInputTests
{
    // A reader that waits for input it will never be given does not block the suite: every test
    // here decides in moments, so one that has not finished by now is broken.
    private const int Patience = 10_000;

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
    /// waiting takes no time; the clock moves only where the reader waits between two looks, which
    /// is what decides how often it can look at all.
    /// </summary>
    private sealed class Typist(TestClock clock, bool redirected, params Arrival[] arrivals) : IConsoleStreams
    {
        private int _next;
        private TimeSpan _since;

        public bool IsInputRedirected => redirected;

        public bool IsOutputRedirected => false;

        public bool InputWaiting => _next < arrivals.Length && _since >= arrivals[_next].After;

        /// <summary>What a sleep of any length really costs: one tick of the system timer.</summary>
        public void Tick(TimeSpan _)
        {
            clock.Advance(Tock);
            _since += Tock;
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

    // Windows rounds a sleep up to the system timer tick unless something has raised the timer
    // resolution, so the reader gets far fewer looks at the keyboard than it asks for.
    private static readonly TimeSpan Tock = TimeSpan.FromMilliseconds(15.6);

    private static PastedInput Reader(params Arrival[] arrivals)
    {
        var clock = new TestClock();
        var typist = new Typist(clock, redirected: false, arrivals);
        return new PastedInput(typist, clock, typist.Tick);
    }

    private static Arrival Together(string line) => new(line, TimeSpan.Zero);

    // Off the test's own thread, so a reader that waits for a line it will never be given is caught
    // by the timeout above rather than holding the thread for as long as the run lasts.
    private static Task<TypedInput?> ReadAsync(PastedInput reader) => Task.Run(reader.Read);

    [Fact(Timeout = Patience)]
    public async Task Lines_that_are_already_waiting_are_one_message()
    {
        var reader = Reader(Together("@claude look at this"), Together("line two"), Together("line three"));

        Assert.Equal(new TypedInput("@claude look at this\nline two\nline three", 3), await ReadAsync(reader));
    }

    [Fact(Timeout = Patience)]
    public async Task A_line_that_arrives_inside_the_window_joins_the_message()
    {
        var reader = Reader(Together("first"), new("second", InsideTheWindow));

        Assert.Equal(new TypedInput("first\nsecond", 2), await ReadAsync(reader));
    }

    [Fact(Timeout = Patience)]
    public async Task A_line_that_arrives_after_the_window_is_a_message_of_its_own()
    {
        var reader = Reader(Together("first"), new("second", AfterTheWindow));

        Assert.Equal(new TypedInput("first", 1), await ReadAsync(reader));
        Assert.Equal(new TypedInput("second", 1), await ReadAsync(reader));
    }

    // A backslash is text. Relay never waits for another line because of one: typing a message over
    // several lines needs the input editor of #67.
    [Fact(Timeout = Patience)]
    public async Task A_typed_line_ending_in_a_backslash_is_sent_as_typed()
    {
        var reader = Reader(Together(@"a path ending in \"), new("next message", AfterTheWindow));

        Assert.Equal(new TypedInput(@"a path ending in \", 1), await ReadAsync(reader));
        Assert.Equal(new TypedInput("next message", 1), await ReadAsync(reader));
    }

    [Fact(Timeout = Patience)]
    public async Task A_backslash_inside_a_paste_is_kept()
    {
        var reader = Reader(Together(@"docker run \"), Together(@"  -it img \"), Together("  --rm"));

        Assert.Equal(new TypedInput("docker run \\\n  -it img \\\n  --rm", 3), await ReadAsync(reader));
    }

    // The window is a number of looks at the keyboard, and a sleep of 1ms really lasts a timer tick.
    // Giving up without looking once more splits a paste whose next line arrived during that tick.
    [Fact(Timeout = Patience)]
    public async Task A_line_arriving_between_two_timer_ticks_still_joins_the_message()
    {
        var reader = Reader(Together("first"), new("second", TimeSpan.FromMilliseconds(20)));

        Assert.Equal(new TypedInput("first\nsecond", 2), await ReadAsync(reader));
    }

    [Fact(Timeout = Patience)]
    public async Task A_backslash_at_the_end_of_a_paste_is_kept()
    {
        var reader = Reader(Together("first"), Together(@"C:\temp\"), new("next message", AfterTheWindow));

        Assert.Equal(new TypedInput("first\n" + @"C:\temp\", 2), await ReadAsync(reader));
        Assert.Equal(new TypedInput("next message", 1), await ReadAsync(reader));
    }

    [Fact(Timeout = Patience)]
    public async Task The_blank_lines_and_indentation_of_a_paste_are_kept()
    {
        var reader = Reader(Together("first"), Together(""), Together("    indented"), Together(""));

        Assert.Equal(new TypedInput("first\n\n    indented\n", 4), await ReadAsync(reader));
    }

    // A block is never trimmed as a whole: trimming one that begins with a blank line would promote
    // its second line into the first, where a /command runs and a mention addresses someone.
    [Fact(Timeout = Patience)]
    public async Task A_block_that_begins_with_a_blank_line_keeps_it()
    {
        var reader = Reader(Together(""), Together("/approvals off"));

        Assert.Equal(new TypedInput("\n/approvals off", 2), await ReadAsync(reader));
    }

    [Fact(Timeout = Patience)]
    public async Task A_single_line_is_trimmed_as_a_typed_line_always_was()
    {
        var reader = Reader(Together("   yes   "));

        Assert.Equal(new TypedInput("yes", 1), await ReadAsync(reader));
    }

    [Fact(Timeout = Patience)]
    public async Task The_end_of_input_ends_the_block_and_then_the_reading()
    {
        var reader = Reader(Together("first"), Together("second"));

        Assert.Equal(new TypedInput("first\nsecond", 2), await ReadAsync(reader));
        Assert.Null(await ReadAsync(reader));
    }

    [Fact(Timeout = Patience)]
    public async Task With_redirected_input_every_line_is_still_its_own_message()
    {
        var clock = new TestClock();
        var typist = new Typist(clock, redirected: true, Together("first"), Together(@"second\"), Together("third"));
        var reader = new PastedInput(typist, clock, typist.Tick);

        Assert.Equal(new TypedInput("first", 1), await ReadAsync(reader));
        Assert.Equal(new TypedInput(@"second\", 1), await ReadAsync(reader));
        Assert.Equal(new TypedInput("third", 1), await ReadAsync(reader));
    }
}
