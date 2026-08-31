/*
 * This file is part of MAME4droid (NetPlay lobby server).
 *
 * Copyright (C) 2026 David Valdeita (Seleuco)
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, see <http://www.gnu.org/licenses>.
 */

using Mame4droid.Lobby.Configuration;
using Mame4droid.Lobby.Contracts;
using Mame4droid.Lobby.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Mame4droid.Lobby.Tests;

/// Telemetry only earns its keep if it actually lands somewhere readable: the
/// endpoint answering 204 proves nothing on its own.
public class LoggingTests
{
    private static TelemetryRequest Report(
        string outcome = "connected",
        string role = "host",
        string? path = "punch",
        string game = "mslug",
        int waitMs = 4200,
        string? country = "ES",
        string? peerCountry = "AR",
        int mode = 1,
        int delay = 0,
        int playMs = 0,
        int rttMs = 0,
        int jitterMs = 0,
        int rttMinMs = 0,
        int rttMaxMs = 0,
        string? room = "K7M2QP4A",
        int rollbacks = 0,
        int rollbackFrames = 0) => new(
            11, "1.39.0", game, role, outcome,
            new NatDto(false, true, true),
            new NatDto(true, false, false),
            path, waitMs, country, peerCountry, mode, delay, playMs, rttMs, jitterMs,
            rttMinMs, rttMaxMs, false, room, false, rollbacks, rollbackFrames);

    [Fact]
    public void A_valid_report_is_written_with_every_field_that_matters()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        Assert.True(sink.Record(Report()));

        var line = Assert.Single(log.Messages);
        Assert.Contains("TELEM", line);
        Assert.Contains("game=mslug", line);
        Assert.Contains("outcome=connected", line);
        Assert.Contains("role=host", line);
        Assert.Contains("path=punch", line);

        /* The NAT pair is the whole point: it is what turns a pile of sessions
         * into "punching works unless a side is symmetric". */
        Assert.Contains("self=sym0pp1up1v60mob0", line);
        Assert.Contains("peer=sym1pp0up0v60mob0", line);
        Assert.Contains("waitMs=4200", line);
    }

    [Fact]
    public void Both_halves_of_one_match_carry_the_same_tag()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        sink.Record(Report(role: "host", room: "K7M2QP4A"));
        sink.Record(Report(role: "client", room: "K7M2QP4A"));
        sink.Record(Report(role: "host", room: "ZXK6DT7M"));

        var first = TagOf(log.Messages[0]);
        Assert.Equal(first, TagOf(log.Messages[1]));
        Assert.NotEqual(first, TagOf(log.Messages[2]));

        /* Grouping the log must not hand anyone a way to match a game against
         * a listing they saved off the public board. */
        Assert.DoesNotContain("K7M2QP4A", log.Messages[0]);
    }

    [Fact]
    public void A_report_from_outside_the_board_is_still_recorded()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        /* Sessions arranged by hand have no room, and losing them would bias
         * every rate towards the people who use the board. */
        Assert.True(sink.Record(Report(room: null)));
        Assert.Equal("-", TagOf(log.Messages[0]));
    }

    private static string TagOf(string line)
    {
        var start = line.IndexOf("session=", StringComparison.Ordinal) + "session=".Length;
        var end = line.IndexOf(' ', start);
        return line.Substring(start, end - start);
    }

    [Fact]
    public void A_line_says_who_played_whom_and_in_which_mode()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        Assert.True(sink.Record(Report(mode: 1, delay: 2)));

        var line = Assert.Single(log.Messages);
        Assert.Contains("route=ES-AR", line);
        Assert.Contains("mode=rollback", line);
        Assert.Contains("delay=2", line);

        Assert.True(sink.Record(Report(mode: 0, country: null, peerCountry: null)));

        /* An unknown country is written, not dropped: a gap in the data still
         * needs to count towards the totals. */
        Assert.Contains("route=??-??", log.Messages[1]);
        Assert.Contains("mode=lockstep", log.Messages[1]);
    }

    [Fact]
    public void Latency_is_averaged_per_route_so_the_log_answers_the_question()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        sink.Record(Report(outcome: "played", playMs: 600000, rttMs: 40, jitterMs: 5));
        sink.Record(Report(outcome: "played", playMs: 600000, rttMs: 60, jitterMs: 9));

        Assert.Contains("rttMs=60", log.Messages[1]);
        Assert.Contains("jitterMs=9", log.Messages[1]);

        /* Whether a route is playable is a latency question, and reading it off
         * the line beats adding up a month of numbers by hand. */
        Assert.Contains("avgRttMs=50", log.Messages[1]);
        Assert.Equal(50, sink.AverageRtt("ES-AR|punch|played"));
    }

    [Fact]
    public void The_range_says_whether_an_average_describes_the_session()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        sink.Record(Report(outcome: "played", rttMs: 95, jitterMs: 12,
            rttMinMs: 70, rttMaxMs: 180));
        sink.Record(Report(outcome: "played", rttMs: 90, jitterMs: 8,
            rttMinMs: 60, rttMaxMs: 120));

        Assert.Contains("rangeMs=60-120", log.Messages[1]);

        /* Best and worst ever seen on the route, not an average of averages:
         * a link that once hit 180 ms is a link that can hit 180 ms. */
        Assert.Equal((60, 180), sink.RangeOf("ES-AR|punch|played"));
    }

    [Fact]
    public void A_session_that_reported_no_range_still_counts_as_itself()
    {
        var sink = new TelemetrySink(new RecordingLogger<TelemetrySink>(), Stats());

        /* An older build sends no floor or ceiling; its average is still
         * evidence, and folding a zero in would invent a perfect link. */
        sink.Record(Report(outcome: "played", rttMs: 75, jitterMs: 6));

        Assert.Equal((75, 75), sink.RangeOf("ES-AR|punch|played"));
    }

    [Fact]
    public void A_session_without_a_measurement_does_not_drag_the_average()
    {
        var sink = new TelemetrySink(new RecordingLogger<TelemetrySink>(), Stats());

        sink.Record(Report(outcome: "played", rttMs: 80, jitterMs: 4));

        /* A pairing that never played has no round trip to report, and a zero
         * would read as a perfect connection. */
        sink.Record(Report(outcome: "played", rttMs: 0, jitterMs: 0));

        Assert.Equal(80, sink.AverageRtt("ES-AR|punch|played"));
    }

    [Fact]
    public void A_session_that_died_at_once_is_not_a_session_that_worked()
    {
        var sink = new TelemetrySink(new RecordingLogger<TelemetrySink>(), Stats());

        Assert.True(sink.Record(Report(outcome: "connected")));
        Assert.True(sink.Record(Report(outcome: "played", playMs: 20 * 60 * 1000)));
        Assert.True(sink.Record(Report(outcome: "dropped", playMs: 8000)));

        /* "connected" alone counted a handshake; these two say what became of
         * it, and the country tally follows the same split. */
        Assert.Equal(1, sink.Counters["ES-AR|played"]);
        Assert.Equal(1, sink.Counters["ES-AR|dropped"]);
    }

    [Fact]
    public void Repeated_outcomes_accumulate_so_a_success_rate_can_be_read_off()
    {
        var sink = new TelemetrySink(new RecordingLogger<TelemetrySink>(), Stats());

        sink.Record(Report());
        sink.Record(Report());
        sink.Record(Report(outcome: "timeout"));

        Assert.Equal(2, sink.Counters["connected|punch|sym0pp1up1v60mob0|sym1pp0up0v60mob0"]);
        Assert.Equal(1, sink.Counters["timeout|punch|sym0pp1up1v60mob0|sym1pp0up0v60mob0"]);
    }


    [Fact]
    public void A_pairing_that_went_over_IPv6_is_told_apart_from_one_that_punched()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        /* Two symmetric CGNAT phones connect fine when both have IPv6, since
         * that route has no NAT to punch through. Counting them beside the v4
         * attempts would flatter the punch rate with sessions that never
         * punched, and the pair is what says which rule of thumb applies. */
        sink.Record(new TelemetryRequest(11, "1.39.0", "mslug", "host", "connected",
            new NatDto(true, false, false, true), new NatDto(true, false, false, true),
            "punch", 1200, "ES", "AR", 1, 0, 0, 0, 0, 0, 0, false, "K7M2QP4A"));

        Assert.Contains("self=sym1pp0up0v61mob0", Assert.Single(log.Messages));
        Assert.Equal(1, sink.Counters["connected|punch|sym1pp0up0v61mob0|sym1pp0up0v61mob0"]);
    }

    [Fact]
    public void A_drop_in_session_is_counted_apart_from_a_normal_start()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        sink.Record(Report(outcome: "connected"));
        sink.Record(Report(outcome: "connected") with { DropIn = true });
        sink.Record(Report(outcome: "timeout") with { DropIn = true });

        Assert.Contains("start=together", log.Messages[0]);
        Assert.Contains("start=dropin", log.Messages[1]);

        /* Its own tally rather than a split of the existing keys: the question
         * is whether drop-in pairs as well as a normal start, and halving
         * every other counter to answer it would only make them noisier. */
        Assert.Equal(1, sink.Counters["dropin|connected"]);
        Assert.Equal(1, sink.Counters["dropin|timeout"]);
        Assert.False(sink.Counters.ContainsKey("dropin|played"));
    }

    [Fact]
    public void A_LAN_game_and_an_internet_one_are_never_averaged_together()
    {
        var sink = new TelemetrySink(new RecordingLogger<TelemetrySink>(), Stats());

        sink.Record(Report(outcome: "played", path: "lan", rttMs: 8));
        sink.Record(Report(outcome: "played", path: "punch", rttMs: 90));

        /* Same two countries, two very different roads. Averaged together they
         * produced a number that described neither, and it was printed on
         * every line as if it were advice. */
        Assert.Equal(8, sink.AverageRtt("ES-AR|lan|played"));
        Assert.Equal(90, sink.AverageRtt("ES-AR|punch|played"));

        /* The country tally stays whole: that one answers whether there is
         * anyone within reach at all, which has nothing to do with the path. */
        Assert.Equal(2, sink.Counters["ES-AR|played"]);
    }
    [Theory]
    [InlineData("exploded", "host", "punch")]
    [InlineData("connected", "spectator", "punch")]
    public void A_report_that_makes_no_sense_is_dropped_instead_of_polluting_the_data(
        string outcome, string role, string path)
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        Assert.False(sink.Record(Report(outcome: outcome, role: role, path: path)));
        Assert.Empty(log.Messages);
    }

    [Fact]
    public void An_unknown_path_still_records_the_outcome()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        /* A cancelled session never took a path; losing the outcome over that
         * would bias the very rate we are trying to measure. */
        Assert.True(sink.Record(Report(outcome: "cancelled", path: null)));
        Assert.Contains("path=?", Assert.Single(log.Messages));
    }

    [Fact]
    public void Logs_go_to_persistent_storage_on_app_service_and_stay_local_anywhere_else()
    {
        var home = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        Assert.Equal(Path.Combine(home, "LogFiles", "lobby"),
            LobbyLogging.LogDirectory("mamelobby-api", home, @"C:\app"));

        /* A dev box has HOME too, and honouring it there scattered logs through
         * the user profile the first time round. */
        Assert.Equal(Path.Combine(@"C:\app", "logs"),
            LobbyLogging.LogDirectory(null, home, @"C:\app"));
        Assert.Equal(Path.Combine(@"C:\app", "logs"),
            LobbyLogging.LogDirectory("mamelobby-api", null, @"C:\app"));
    }

    [Fact]
    public void Telemetry_and_operational_noise_end_up_in_separate_files()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var configuration = new Serilog.LoggerConfiguration();
            LobbyLogging.Configure(new FakeEnvironment(root.FullName), configuration);

            using (var serilog = configuration.CreateLogger())
            {
                var factory = new Serilog.Extensions.Logging.SerilogLoggerFactory(serilog);
                new TelemetrySink(factory.CreateLogger<TelemetrySink>(), Stats()).Record(Report());
                factory.CreateLogger("Mame4droid.Lobby.Startup").LogWarning("port already in use");
            }

            var logs = Path.Combine(root.FullName, "logs");
            var telemetry = ReadAll(logs, "telemetry-*.log");
            var operational = ReadAll(logs, "lobby-*.log");

            /* One file a month of outcomes, and none of it buried under startup
             * chatter -- the point is to answer "how did last week go" without
             * opening seven files. */
            Assert.Contains("game=mslug", telemetry);
            Assert.DoesNotContain("port already in use", telemetry);

            Assert.Contains("port already in use", operational);
            Assert.DoesNotContain("game=mslug", operational);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static string ReadAll(string directory, string pattern)
        => string.Concat(Directory.GetFiles(directory, pattern).Select(File.ReadAllText));

    [Fact]
    public void What_rollback_cost_is_logged_as_a_rate_and_a_depth()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        /* Two minutes, 240 misses re-simulating 1200 frames between them. */
        Assert.True(sink.Record(Report(outcome: "played", playMs: 120000,
            rttMs: 220, rollbacks: 240, rollbackFrames: 1200)));

        var line = Assert.Single(log.Messages);

        /* Per minute, not per session: a long game would otherwise always
         * look worse than a short one on the same link. */
        Assert.Contains("rbPerMin=120", line);
        Assert.Contains("rbDepth=5", line);
    }

    [Fact]
    public void A_lockstep_session_reports_no_rollback_cost()
    {
        var log = new RecordingLogger<TelemetrySink>();
        var sink = new TelemetrySink(log, Stats());

        /* Nothing to divide by, and a stray average off zero misses would
         * read as a link that rolled back perfectly. */
        Assert.True(sink.Record(Report(outcome: "played", playMs: 60000, rttMs: 40)));

        var line = Assert.Single(log.Messages);
        Assert.Contains("rbPerMin=0", line);
        Assert.Contains("rbDepth=0", line);
    }

    /// The sink needs a stats store; these tests are about what it logs, not
    /// about what it counts, so it is pointed at a file nothing will read.
    private static StatsStore Stats()
        => new(new FixedOptions<LobbyOptions>(new LobbyOptions()),
               new RecordingLogger<StatsStore>(),
               Path.Combine(Path.GetTempPath(),
                            "m4d-stats-" + Guid.NewGuid().ToString("N") + ".txt"));

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public FakeEnvironment(string contentRoot) => ContentRootPath = contentRoot;

        public string ApplicationName { get; set; } = "Mame4droid.Lobby";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
