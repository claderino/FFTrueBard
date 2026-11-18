namespace FFTrueBard.Detection;

/// <summary>Envelope dip/rise evidence for a fresh pick, independent of the expected note.</summary>
public sealed class PickAttackDetector(int sampleRate)
{
    // Follow attacks quickly, while smoothing individual string oscillations.
    private readonly double smoothing = 1 - Math.Exp(-1.0 / (sampleRate * 0.004));
    private readonly double peakDecay = Math.Exp(-1.0 / (sampleRate * 0.15));
    private double energy;
    private double peakEnergy;
    private double trough;
    private bool armed = true;
    private long lastAttack = long.MinValue / 2;

    public long? Process(float[] samples, long frameEndMs, DetectionSettings settings)
    {
        long? found = null;
        var floor = (double)settings.RmsGate * settings.RmsGate;
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            if (!float.IsFinite(value))
            {
                energy = peakEnergy = trough = 0;
                armed = true;
                continue;
            }
            energy += smoothing * (value * value - energy);
            peakEnergy = Math.Max(energy, peakEnergy * peakDecay);
            // Require a dip before another rise; a steady or fading tone cannot auto-repeat.
            if (!armed && (energy < peakEnergy * 0.4 || energy < floor))
            {
                armed = true;
                trough = energy;
            }
            if (!armed) continue;
            trough = Math.Min(trough, energy);
            if (energy < Math.Max(floor * 4, trough * 3)) continue;
            var at = frameEndMs - (long)Math.Round((samples.Length - 1 - i) * 1000.0 / sampleRate);
            if (at - lastAttack < Math.Max(50, settings.RetriggerMs)) continue;
            found = lastAttack = at;
            armed = false;
            peakEnergy = energy;
        }
        return found;
    }
}
