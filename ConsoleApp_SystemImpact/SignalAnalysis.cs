using System.Numerics;

namespace ConsoleApp_SystemImpact;

internal sealed record Association(
    string Source, double? Pearson, int? LagBins, double? LagPearson,
    double? Coherence, double? FrequencyHz, int WelchSegments,
    double? PhaseDegrees = null, double? Threshold = null)
{
    /// <summary>Coherence exceeds the Bonferroni-corrected null threshold (alpha 0.05 over all tested bins).</summary>
    public bool Significant => Coherence is { } c && Threshold is { } t && c >= t;
}

internal readonly record struct CoherencePeak(
    double? Coherence, double? Frequency, int Segments, double? PhaseDegrees, double? Threshold);

internal readonly record struct SpectralPeak(int Bin, double Prominence);

internal readonly record struct PeriodSupportResult(double Match, double Expected, double Excess, int Eligible,
    long MedianMatchedTicks = 0);

internal static class SignalAnalysis
{
    public static double? Pearson(double[] x, double[] y, int lag = 0)
    {
        if (x.Length != y.Length)
            throw new ArgumentException("Signals must have equal lengths.");
        int begin = Math.Max(0, -lag), end = Math.Min(x.Length, y.Length - lag);
        int count = end - begin;
        if (count < 3)
            return null;
        double sx = 0, sy = 0;
        for (int i = begin; i < end; i++) { sx += x[i]; sy += y[i + lag]; }
        double mx = sx / count, my = sy / count;
        double xx = 0, yy = 0, xy = 0;
        for (int i = begin; i < end; i++)
        {
            double a = x[i] - mx, b = y[i + lag] - my;
            xx += a * a; yy += b * b; xy += a * b;
        }
        return xx <= 1e-20 || yy <= 1e-20 ? null : Math.Clamp(xy / Math.Sqrt(xx * yy), -1, 1);
    }

    public static Association Compare(string source, double[] x, double[] y, double binSeconds)
    {
        double? best = null;
        int? bestLag = null;
        int maxLag = Math.Min((int)(2 / binSeconds), x.Length / 10);
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double? r = Pearson(x, y, lag);
            if (r is not null && (best is null || Math.Abs(r.Value) > Math.Abs(best.Value)))
            {
                best = r; bestLag = lag;
            }
        }
        var spectrum = WelchDetail(x, y, binSeconds);
        return new(source, Pearson(x, y), bestLag, best,
            spectrum.Coherence, spectrum.Frequency, spectrum.Segments, spectrum.PhaseDegrees, spectrum.Threshold);
    }

    public static (double? Coherence, double? Frequency, int Segments) Welch(
        double[] x, double[] y, double binSeconds)
    {
        var d = WelchDetail(x, y, binSeconds);
        return (d.Coherence, d.Frequency, d.Segments);
    }

    /// <summary>
    /// Null threshold for magnitude-squared coherence: P(C &gt; c) = (1 - c)^(K - 1) with K equivalent
    /// independent segments (Hann, 50% overlap: dof = 36N²/(19N - 1), K = dof/2), Bonferroni over tested bins.
    /// </summary>
    internal static double CoherenceThreshold(int segments, int testedBins, double alpha = .05)
    {
        double k = 36.0 * segments * segments / (19.0 * segments - 1) / 2;
        return k <= 1 ? 1 : 1 - Math.Pow(alpha / Math.Max(1, testedBins), 1 / (k - 1));
    }

    public static CoherencePeak WelchDetail(double[] x, double[] y, double binSeconds)
    {
        if (x.Length != y.Length || binSeconds <= 0)
            throw new ArgumentException("Invalid signal dimensions or sampling interval.");
        int size = 256;
        while (size > 32 && x.Length < size * 4) size /= 2;
        int segments = x.Length < size ? 0 : 1 + (x.Length - size) / (size / 2);
        if (segments < 7)
            return new(null, null, segments, null, null);
        double[] xx = new double[size / 2 + 1], yy = new double[xx.Length];
        double[] xxSquared = new double[xx.Length], yySquared = new double[xx.Length];
        Complex[] xy = new Complex[xx.Length];
        for (int start = 0; start + size <= x.Length; start += size / 2)
        {
            Complex[] a = Window(x, start, size), b = Window(y, start, size);
            Fft(a); Fft(b);
            for (int k = 1; k < xx.Length; k++)
            {
                double powerX = a[k].Magnitude * a[k].Magnitude;
                double powerY = b[k].Magnitude * b[k].Magnitude;
                xx[k] += powerX;
                yy[k] += powerY;
                xxSquared[k] += powerX * powerX;
                yySquared[k] += powerY * powerY;
                xy[k] += a[k] * Complex.Conjugate(b[k]);
            }
        }
        double maxX = xx.Max(), maxY = yy.Max();
        if (maxX <= 1e-20 || maxY <= 1e-20) return new(null, null, segments, null, null);
        double? best = null, frequency = null, phase = null;
        // Ignore DC, near-Nyquist bins and frequencies carrying negligible power.
        for (int k = 1; k < size / 2; k++)
        {
            if (xx[k] < maxX * .05 || yy[k] < maxY * .05) continue;
            // A single shared burst must not masquerade as repeated spectral coupling.
            double supportX = xx[k] * xx[k] / xxSquared[k];
            double supportY = yy[k] * yy[k] / yySquared[k];
            if (supportX < 7 || supportY < 7) continue;
            double c = Math.Clamp(xy[k].Magnitude * xy[k].Magnitude / (xx[k] * yy[k]), 0, 1);
            if (best is null || c > best)
            {
                best = c; frequency = k / (size * binSeconds);
                // arg(X·conj(Y)) > 0 means the second signal lags the first.
                phase = xy[k].Phase * 180 / Math.PI;
            }
        }
        return new(best, frequency, segments, phase,
            best is null ? null : CoherenceThreshold(segments, size / 2 - 1));
    }

    /// <summary>Averaged Hann-windowed periodogram (Welch PSD, arbitrary units) of one signal.</summary>
    internal static (double[] Power, int Segments) Spectrum(double[] x, int size)
    {
        if (size < 8 || (size & (size - 1)) != 0)
            throw new ArgumentException("Spectrum size must be a power of two >= 8.");
        if (x.Length < size) return ([], 0);
        double[] power = new double[size / 2 + 1];
        int segments = 0;
        for (int start = 0; start + size <= x.Length; start += size / 2)
        {
            Complex[] a = Window(x, start, size);
            Fft(a);
            for (int k = 0; k < power.Length; k++) power[k] += a[k].Real * a[k].Real + a[k].Imaginary * a[k].Imaginary;
            segments++;
        }
        for (int k = 0; k < power.Length; k++) power[k] /= segments;
        return (power, segments);
    }

    /// <summary>
    /// Local maxima whose power exceeds the median of their neighbourhood (excluding ±2 leakage bins)
    /// by <paramref name="minProminence"/>, strongest first. A local median tolerates red/bursty backgrounds;
    /// pulse trains produce several harmonic peaks, so callers should test more than the strongest one.
    /// </summary>
    internal static List<SpectralPeak> Peaks(double[] power, int minBin, int maxBin, int halfWidth,
        double minProminence, int maxCount)
    {
        List<SpectralPeak> peaks = [];
        List<double> neighbours = new(2 * halfWidth);
        for (int k = Math.Max(1, minBin); k <= Math.Min(maxBin, power.Length - 2); k++)
        {
            if (power[k] < power[k - 1] || power[k] < power[k + 1]) continue;
            neighbours.Clear();
            for (int j = k - halfWidth; j <= k + halfWidth; j++)
                if (Math.Abs(j - k) > 2 && j >= 1 && j <= power.Length - 2) neighbours.Add(power[j]);
            if (neighbours.Count < 6) continue;
            neighbours.Sort();
            double median = neighbours[neighbours.Count / 2];
            if (median <= 1e-30) continue;
            double prominence = power[k] / median;
            if (prominence >= minProminence) peaks.Add(new(k, prominence));
        }
        return peaks.OrderByDescending(p => p.Prominence).Take(maxCount).ToList();
    }

    /// <summary>
    /// Time-domain check of a candidate period: fraction of events followed by another event one period
    /// later (±10%), compared with the chance expectation for a Poisson process of the same rate.
    /// Excess = (match - expected)/(1 - expected); undefined (0) when chance alone would already match.
    /// </summary>
    internal static PeriodSupportResult PeriodSupport(long[] sortedTimes, long periodTicks)
    {
        int n = sortedTimes.Length;
        if (n < 20 || periodTicks <= 0) return new(0, 1, 0, 0);
        long span = Math.Max(1, sortedTimes[^1] - sortedTimes[0]);
        long tolerance = Math.Max(1, periodTicks / 10);
        int hits = 0, eligible = 0, j = 0;
        List<long> matched = [];
        for (int i = 0; i < n; i++)
        {
            long lo = sortedTimes[i] + periodTicks - tolerance, hi = sortedTimes[i] + periodTicks + tolerance;
            if (hi > sortedTimes[^1]) break;
            eligible++;
            while (j < n && sortedTimes[j] < lo) j++;
            if (j < n && sortedTimes[j] <= hi)
            {
                hits++;
                matched.Add(sortedTimes[j] - sortedTimes[i]);
            }
        }
        if (eligible < 20) return new(0, 1, 0, eligible);
        double match = hits / (double)eligible;
        double expected = 1 - Math.Exp(-(n - 1) / (double)span * 2 * tolerance);
        double excess = expected >= .9 ? 0 : (match - expected) / (1 - expected);
        matched.Sort();
        return new(match, expected, excess, eligible, matched.Count == 0 ? 0 : matched[matched.Count / 2]);
    }

    private static Complex[] Window(double[] x, int start, int count)
    {
        double mean = 0;
        for (int i = 0; i < count; i++) mean += x[start + i];
        mean /= count;
        Complex[] result = new Complex[count];
        for (int i = 0; i < count; i++)
            result[i] = (x[start + i] - mean) * (.5 - .5 * Math.Cos(2 * Math.PI * i / (count - 1)));
        return result;
    }

    internal static void Fft(Complex[] data)
    {
        int n = data.Length;
        if (n < 2 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two.");
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            Complex step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < n; start += length)
            {
                Complex w = Complex.One;
                for (int i = 0; i < length / 2; i++)
                {
                    Complex even = data[start + i], odd = w * data[start + i + length / 2];
                    data[start + i] = even + odd;
                    data[start + i + length / 2] = even - odd;
                    w *= step;
                }
            }
        }
    }
}
