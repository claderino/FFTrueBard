namespace FFTrueBard.Detection;

/// <summary>
/// YIN fundamental frequency estimator (de Cheveigné & Kawahara 2002),
/// ported from the original Python detector. Returns 0 when unvoiced.
/// </summary>
public static class YinPitch
{
    public static float Detect(float[] y, int sampleRate, float fMin, float fMax, float threshold)
        => Analyze(y, sampleRate, fMin, fMax, threshold).Frequency;

    public static (float Frequency, float Confidence) Analyze(float[] y, int sampleRate,
        float fMin, float fMax, float threshold)
    {
        if (sampleRate <= 0 || fMin <= 0 || fMax <= fMin || threshold <= 0 || threshold >= 1)
            return (0, 0);
        var w = Math.Min(y.Length / 2, 1024);
        var maxTau = (int)(sampleRate / fMin);
        var minTau = Math.Max(2, (int)(sampleRate / fMax));
        if (y.Length < maxTau + w || w <= minTau + 2)
            return (0, 0);

        // Difference function d(tau)
        var d = new float[maxTau + 1];
        for (var tau = 1; tau <= maxTau; tau++)
        {
            float sum = 0f;
            var off = tau;
            for (var i = 0; i < w; i++)
            {
                var delta = y[i] - y[off + i];
                sum += delta * delta;
            }

            d[tau] = sum;
        }

        // Cumulative mean normalized difference function
        var cmnd = new float[maxTau + 1];
        cmnd[0] = 1f;
        var runningSum = 0f;
        for (var tau = 1; tau <= maxTau; tau++)
        {
            runningSum += d[tau];
            cmnd[tau] = runningSum > 0f ? d[tau] * tau / runningSum : 1f;
        }

        // Follow the first threshold crossing to its local minimum. Interpolating
        // the crossing itself biases the pitch sharp, especially on low strings.
        var found = -1;
        for (var tau = minTau; tau < maxTau; tau++)
        {
            if (cmnd[tau] < threshold)
            {
                while (tau + 1 <= maxTau && cmnd[tau + 1] < cmnd[tau])
                    tau++;
                found = tau;
                break;
            }
        }

        if (found < 0)
            return (0, 0);

        // Parabolic interpolation around the minimum
        var tauF = (float)found;
        if (found > 1 && found < maxTau - 1)
        {
            var s0 = cmnd[found - 1];
            var s1 = cmnd[found];
            var s2 = cmnd[found + 1];
            var denom = 2f * (2f * s1 - s2 - s0);
            if (denom != 0f)
                tauF += (s2 - s0) / denom;
        }

        var frequency = sampleRate / tauF;
        return float.IsFinite(frequency) && frequency >= fMin && frequency <= fMax
            ? (frequency, Math.Clamp(1 - cmnd[found], 0, 1))
            : (0, 0);
    }
}
