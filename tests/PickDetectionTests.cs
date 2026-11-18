using FFTrueBard.Detection;

internal static class PickDetectionTests
{
    public static void Run(Action<bool, string> check)
    {
        var settings = new DetectionSettings(0.15f, 0.004f, 2, 150, 100);
        foreach (var rate in new[] { 44100, 48000, 96000 })
        {
            var size = Math.Max(1024, (int)(2048L * rate / 48000));
            List<DetectionFrame> Replay(double duration, Func<double, double> signal)
            {
                var processor = new PitchProcessor(rate);
                var rows = new List<DetectionFrame>();
                var frame = new float[size];
                for (var offset = 0; offset + size <= (int)(duration * rate); offset += size)
                {
                    for (var n = 0; n < size; n++) frame[n] = (float)signal((double)(offset + n) / rate);
                    rows.Add(processor.Process(frame, (long)Math.Round((offset + size) * 1000.0 / rate), settings));
                }
                return rows;
            }
            foreach (var midi in new[] { 40, 45, 50, 55, 59, 64, 69, 76, 83, 87 })
            {
                var hz = 440 * Math.Pow(2, (midi - 69) / 12.0);
                var held = Replay(2, t => 0.18 * Math.Sin(2 * Math.PI * hz * t));
                check(held.Count(f => f.Decision == "accepted") == 1, $"Held MIDI {midi} has no false repeats at {rate}");
                var ringingPicks = Replay(3, t =>
                {
                    var age = t % 0.25;
                    var amplitude = 0.025 + 0.2 * (1 - Math.Exp(-age / 0.002)) * Math.Exp(-age / 0.10);
                    return amplitude * (0.75 * Math.Sin(2 * Math.PI * hz * t) + 0.2 * Math.Sin(4 * Math.PI * hz * t));
                });
                var accepted = ringingPicks.Where(f => f.Decision == "accepted").ToArray();
                check(accepted.Length == 12 && accepted.All(f => f.Midi == midi),
                    $"Twelve same-pitch picks above the gate are recognized without octave errors at MIDI {midi}, {rate}");
                check(accepted.All(f => f.FreshAttack), "Repeated-pick events carry independent envelope-attack evidence");
                check(accepted.Zip(accepted.Skip(1), (a, b) => b.TimestampMs - a.TimestampMs).All(ms => ms >= settings.RetriggerMs),
                    "Repeated-pick recognition retains the configured minimum interval");
            }
            var vibrato = Replay(3, t =>
            {
                // Continuous FM (~20 cents) and moderate tremolo, without a new pick.
                var phase = 2 * Math.PI * 329.6276 * t + 0.75 * Math.Sin(2 * Math.PI * 5 * t);
                return (0.16 + 0.025 * Math.Sin(2 * Math.PI * 3 * t)) * Math.Sin(phase);
            });
            check(vibrato.Count(f => f.Decision == "accepted") == 1, "Moderate vibrato/tremolo is not mistaken for repeated picks");
            var gainRamp = Replay(3, t => (0.03 + 0.05 * t) * Math.Sin(2 * Math.PI * 440 * t));
            check(gainRamp.Count(f => f.Decision == "accepted") == 1, "Gradual level increase does not create extra picks");
            var decay = Replay(3, t => 0.2 * Math.Exp(-t / 0.8) * Math.Sin(2 * Math.PI * 82.4069 * t));
            check(decay.Count(f => f.Decision == "accepted") == 1, "A decaying low E does not rearm itself into false picks");
            foreach (var period in new[] { 0.20, 0.36 })
            foreach (var level in new[] { 0.06, 0.22 })
            {
                var picks = Replay(period * 10 + 0.10, t =>
                {
                    if (t < 0.10) return 0;
                    var age = (t - 0.10) % period;
                    var amplitude = level * (0.08 + (1 - Math.Exp(-age / 0.002)) * Math.Exp(-age / (period * 0.4)));
                    return amplitude * Math.Sin(2 * Math.PI * 246.9417 * t);
                });
                check(picks.Count(f => f.Decision == "accepted" && f.Midi == 59) == 10,
                    $"Ten picks survive changed tempo, gain, and frame alignment at {rate}, {period}s, {level}");
            }
            var gap = Replay(2.4, t => t < 0.8 || t >= 1.6 ? 0.15 * Math.Sin(2 * Math.PI * 440 * t) : 0);
            check(gap.Count(f => f.Decision == "accepted") == 2, "Quiet-release rearming still works with pick recognition");
            var attackOnly = Replay(0.8, t => t < 0.012 ? 0.5 * Math.Sin(2 * Math.PI * 440 * t) : 0);
            check(attackOnly.All(f => f.Decision != "accepted"), "Brief attack without stable pitch is not accepted");
            var random = new Random(401 + rate);
            var noise = Replay(2, _ => (random.NextDouble() - 0.5) * 0.1);
            check(noise.All(f => f.Decision != "accepted"), "Filtered broadband noise cannot become accepted notes");
            var silence = Replay(1, _ => 0);
            check(silence.All(f => f.Decision == "gated" && !f.FreshAttack), "Silence never creates fresh-pick evidence");
        }
        var source = Enumerable.Range(0, 2048).Select(i => (float)(0.2 * Math.Sin(2 * Math.PI * 440 * i / 48000))).ToArray();
        var copy = source.ToArray();
        YinPitch.Analyze(source, 48000, 70, 1300, 0.15f);
        check(source.SequenceEqual(copy), "Pitch filtering does not modify captured samples or raw level measurements");
        source[400] = float.NaN;
        check(YinPitch.Detect(source, 48000, 70, 1300, 0.15f) == 0, "Invalid input remains unvoiced after analysis filtering");
    }
}
