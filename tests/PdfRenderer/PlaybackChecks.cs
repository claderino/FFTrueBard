using FFTrueBard.Lessons;
using NAudio.Wave;

internal static class PlaybackChecks
{
    public static void Run(TabPdfGuide score)
    {
        Check(typeof(IWavePosition).IsAssignableFrom(typeof(WaveOutEvent)), "The live audio output must expose its played-byte clock");
        var output = new ControlledOutput();
        using var player = new LessonPlayer(() => output);
        player.LoadTabGuide("generated test score", score);
        WaitForAudio(player);
        Check(score.End < player.Duration && player.SetPlaybackClip(0, score.End), "Could not set the original playback bounds");

        var row = score.Rows[0].Cursor;
        var a = row.NoteAtX(row.XAt(.25), 0) ?? throw new InvalidOperationException("Missing loop start note");
        Check(a.Start == .25 && a.End == .5, "Loop points are the printed note's onset and end");
        player.Play();
        output.Buffer(.4);
        output.PlayBuffered(.1);
        Check(player.Position > .35 && Near(player.AudiblePosition, .1) &&
            Near(row.TimeAt(row.XAt(player.AudiblePosition)), .1), "Read-ahead moved the cursor past the audible position");

        player.SeekFromScore(row.TimeAt(row.XAt(.25)));
        Check(output.AwaitingStop && Near(player.AudiblePosition, .25), "Score seek did not clear the queued audio");
        player.Update();
        Check(output.AwaitingStop && output.PlaybackState == PlaybackState.Stopped, "Seek resumed before stop was acknowledged");
        output.AcknowledgeStop();
        player.Update();
        Check(player.Playing && Near(player.Position, .25) && Near(row.XAt(player.AudiblePosition), a.X + a.Width / 2),
            "Score seek did not resume from the chosen note");

        player.SetSpeed(75);
        output.AcknowledgeStop();
        player.Update();
        output.Buffer(1.1);
        output.PlayBuffered(.5);
        Check(Near(player.AudiblePosition, .625) && score.RowAt(player.AudiblePosition) is null,
            "The slowed clock did not hide the cursor over a rest");
        output.PlayBuffered(.55);
        Check(Near(player.AudiblePosition, 1.0375) && score.RowAt(player.AudiblePosition)?.Page == 2,
            "Slowed audio and score did not move together to page two");

        Check(player.SetScoreLoop(a.Start, a.End), "Could not set an A–B loop");
        output.AcknowledgeStop();
        player.Update();
        Check(Near(player.LoopA, .25) && Near(player.LoopB, .5), "Loop bounds don't match the selected note");
        output.Buffer(.8);
        output.PlayBuffered(.5);
        Check(Near(player.AudiblePosition, .375) && score.RowAt(player.AudiblePosition)?.Page == 1,
            "The slowed playback didn't wrap to the looped row");
        Check(player.ClearScoreLoop(), "Could not clear the A–B loop");
        output.AcknowledgeStop();
        player.Update();
        Check(!player.LoopEnabled && player.SpeedPercent == 75 && player.Playing && Near(player.AudiblePosition, .375),
            "Clearing the loop changed speed, paused playback, or jumped away from the audible note");
        output.Buffer(.95);
        output.PlayBuffered(.9);
        Check(score.RowAt(player.AudiblePosition)?.Page == 2, "Clearing the loop did not restore playback past B");
        player.SeekFromScore(player.Duration);
        output.AcknowledgeStop();
        player.Update();
        Check(!player.Playing && Near(player.Position, score.End), "Clearing the loop did not restore the original clip end");
        player.Stop();
        player.Update();
        Check(!player.Playing && Near(player.Position, 0), "Stop did not return to the original clip start");
    }

    public static void RunCourse(LessonLibrary library, LessonScorePlayhead.BundledMap timing, LessonScoreCursor cursor)
    {
        var exercise = timing.Map.Exercise;
        var track = library.FindWeeklyExerciseTrack(exercise)
            ?? throw new InvalidOperationException($"No weekly MP3 for Exercise {exercise}");
        var matched = LessonScorePlayhead.FindForReader(library.Paths.Root, library.Paths.Pdf, exercise,
            timing.Map.PdfPage, track.Path, timing.CueStartSeconds, timing.CueEndSeconds);
        Check(matched.Map?.AudioSha256 == timing.Map.AudioSha256 && matched.MeasuredEndSeconds == timing.MeasuredEndSeconds,
            $"Exercise {exercise} did not resolve the reviewed timings for the original MP3");
        var output = new ControlledOutput();
        using var player = new LessonPlayer(() => output);
        player.Load(track.Path);
        WaitForAudio(player);
        var clipEnd = timing.PlaybackEndSeconds ?? player.Duration;
        Check(player.SetPlaybackClip(timing.CueStartSeconds, clipEnd), $"Exercise {exercise} clip did not load");
        Check(Near(player.Position, timing.CueStartSeconds) && !cursor.ShowsNoteAt(player.AudiblePosition),
            $"Exercise {exercise} cursor must stay off the notation at the announcement");

        if (exercise == 193)
        {
            var final = timing.Map.Notes[^1];
            player.Seek(final.Start - .3);
            player.Play();
            output.Buffer(.5);
            output.PlayBuffered(.31);
            Check(player.AudiblePosition > timing.CueEndSeconds && cursor.ShowsNoteAt(player.AudiblePosition),
                "Exercise 193 did not play through the final note beyond the printed cutoff");
            return;
        }

        var firstRepeat = timing.Map.Notes.First(n => n.Pass == 1);
        player.SetSpeed(75);
        player.Seek(firstRepeat.Start - .3);
        player.Play();
        output.Buffer(.8);
        output.PlayBuffered(.24);
        var before = cursor.XAt(player.AudiblePosition);
        output.PlayBuffered(.24);
        Check(cursor.XAt(player.AudiblePosition) < before && cursor.ShowsNoteAt(player.AudiblePosition),
            "Exercise 294 must jump back to its repeated phrase at 0.75×");
        var a = cursor.NoteAtX(firstRepeat.X + firstRepeat.Width / 2, player.AudiblePosition)!;
        var next = timing.Map.Notes.First(n => n.Pass == 1 && n.Start > firstRepeat.Start);
        var b = cursor.NoteAtX(next.X + next.Width / 2, player.AudiblePosition)!;
        Check(a == firstRepeat && b == next && player.SetScoreLoop(a.Start, b.End), "Exercise 294 did not use the second pass for A–B");
        output.AcknowledgeStop();
        player.Update();
        var wrap = (b.End - a.Start + .05) / .75;
        output.Buffer(wrap + .05);
        output.PlayBuffered(wrap);
        Check(Near(player.AudiblePosition, a.Start + .05), "Exercise 294 did not wrap to the second-pass A");
        Check(player.ClearScoreLoop(), "Exercise 294 could not clear the loop");
        output.AcknowledgeStop();
        player.Update();
        Check(player.Playing && player.SpeedPercent == 75 && Near(player.AudiblePosition, a.Start + .05),
            "Exercise 294 did not resume from the second-pass cursor at the original speed setting");
    }

    private static void WaitForAudio(LessonPlayer player) =>
        Check(SpinWait.SpinUntil(() => { player.Update(); return !player.Loading; }, TimeSpan.FromSeconds(5)) &&
            player.Ready, $"Practice audio did not load: {player.Status}");

    private static bool Near(double a, double b) => Math.Abs(a - b) < .0002;
    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }

    // Real samples are pulled through the synthesizer and SoundTouch. Only the
    // Windows audio device's byte clock and stop notification are controlled.
    private sealed class ControlledOutput : IWavePlayer, IWavePosition
    {
        private IWaveProvider provider = null!;
        private long played;
        private long buffered;
        public bool AwaitingStop { get; private set; }
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => provider.WaveFormat;
        public float Volume { get; set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider waveProvider) => provider = waveProvider;
        public long GetPosition() => played;
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop()
        {
            PlaybackState = PlaybackState.Stopped;
            AwaitingStop = true;
        }
        public void AcknowledgeStop()
        {
            Check(AwaitingStop, "The player didn't request that the output stop");
            played = buffered = 0;
            AwaitingStop = false;
            PlaybackStopped?.Invoke(this, new StoppedEventArgs());
        }
        public void Buffer(double seconds)
        {
            var bytes = new byte[Bytes(seconds)];
            var read = provider.Read(bytes, 0, bytes.Length);
            Check(read == bytes.Length && bytes.Any(b => b != 0), $"Expected {bytes.Length} bytes of flowing PCM; got {read}");
            buffered += read;
        }
        public void PlayBuffered(double seconds)
        {
            played += Bytes(seconds);
            Check(PlaybackState == PlaybackState.Playing && played <= buffered, "Played frames must have been buffered first");
        }
        private int Bytes(double seconds) =>
            (int)Math.Round(seconds * OutputWaveFormat.SampleRate) * OutputWaveFormat.BlockAlign;
        public void Dispose() { }
    }
}
