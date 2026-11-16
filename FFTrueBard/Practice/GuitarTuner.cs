using FFTrueBard.Detection;

namespace FFTrueBard.Practice;

public static class OpenStrings
{
    public static readonly string[] Names = ["E2", "A2", "D3", "G3", "B3", "E4"];
    public static readonly int[] Midi = [40, 45, 50, 55, 59, 64];
    public static double Frequency(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);
}

public sealed record TunerReading(string Target, double Frequency, double Cents, int Samples, long TimestampMs);

public sealed class GuitarTuner
{
    private readonly Queue<double> cents = new();
    private int target = -2;
    public TunerReading? Reading { get; private set; }

    public void Reset()
    {
        cents.Clear();
        target = -2;
        Reading = null;
    }

    public void Receive(DetectionFrame frame, int selectedString)
    {
        if (frame.Frequency <= 0 || frame.Confidence < 0.8f || frame.Rms < frame.Settings.RmsGate)
        {
            Reset();
            return;
        }
        var differences = OpenStrings.Midi.Select(m => 1200 * Math.Log2(frame.Frequency / OpenStrings.Frequency(m))).ToArray();
        var index = selectedString >= 0 ? Math.Clamp(selectedString, 0, 5) :
            Enumerable.Range(0, 6).MinBy(i => Math.Abs(differences[i]));
        if (selectedString < 0 && Math.Abs(differences[index]) > 150)
        {
            Reset();
            return;
        }
        if (target != index)
            cents.Clear();
        target = index;
        cents.Enqueue(differences[index]);
        while (cents.Count > 5)
            cents.Dequeue();
        var ordered = cents.Order().ToArray();
        Reading = new TunerReading(OpenStrings.Names[index], frame.Frequency,
            ordered[ordered.Length / 2], ordered.Length, frame.TimestampMs);
    }
}
