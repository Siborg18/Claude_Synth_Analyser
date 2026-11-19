using System;
using System.Collections.Generic;

namespace SynthAnalyser
{
    /// <summary>Small DSP toolbox: FFT, windows, statistics, note naming.</summary>
    public static class Dsp
    {
        // ---------------------------------------------------------------- FFT
        /// <summary>In-place iterative radix-2 complex FFT. n must be a power of two.</summary>
        public static void Fft(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    double t = re[i]; re[i] = re[j]; re[j] = t;
                    t = im[i]; im[i] = im[j]; im[j] = t;
                }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
                double wr = Math.Cos(ang), wi = Math.Sin(ang);
                int half = len >> 1;
                for (int i = 0; i < n; i += len)
                {
                    double cr = 1, ci = 0;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = a + half;
                        double xr = re[b] * cr - im[b] * ci;
                        double xi = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - xr; im[b] = im[a] - xi;
                        re[a] += xr; im[a] += xi;
                        double ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr; cr = ncr;
                    }
                }
            }
            if (inverse)
            {
                for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
            }
        }

        public static int NextPow2(int v)
        {
            int n = 1;
            while (n < v) n <<= 1;
            return n;
        }

        private static readonly Dictionary<int, double[]> hannCache = new Dictionary<int, double[]>();
        public static double[] Hann(int n)
        {
            lock (hannCache)
            {
                double[] w;
                if (hannCache.TryGetValue(n, out w)) return w;
                w = new double[n];
                for (int i = 0; i < n; i++) w[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / n);
                hannCache[n] = w;
                return w;
            }
        }

        /// <summary>Power spectrum (n/2+1 bins) of a Hann-windowed frame starting at 'start'.</summary>
        public static double[] PowerSpectrum(float[] x, int start, int n)
        {
            double[] re = new double[n], im = new double[n];
            double[] w = Hann(n);
            for (int i = 0; i < n; i++)
            {
                int idx = start + i;
                re[i] = (idx >= 0 && idx < x.Length) ? x[idx] * w[i] : 0.0;
            }
            Fft(re, im, false);
            double[] p = new double[n / 2 + 1];
            for (int i = 0; i <= n / 2; i++) p[i] = re[i] * re[i] + im[i] * im[i];
            return p;
        }

        // ---------------------------------------------------------- helpers
        public static double ToDb(double lin) { return 20.0 * Math.Log10(Math.Max(lin, 1e-12)); }
        public static double PowToDb(double p) { return 10.0 * Math.Log10(Math.Max(p, 1e-24)); }
        public static double FromDb(double db) { return Math.Pow(10.0, db / 20.0); }
        public static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

        public static double Median(IList<double> v)
        {
            if (v == null || v.Count == 0) return double.NaN;
            List<double> s = new List<double>(v);
            s.Sort();
            int m = s.Count / 2;
            return (s.Count % 2 == 1) ? s[m] : 0.5 * (s[m - 1] + s[m]);
        }

        public static double Percentile(IList<double> v, double p)
        {
            if (v == null || v.Count == 0) return double.NaN;
            List<double> s = new List<double>(v);
            s.Sort();
            double pos = Clamp(p, 0, 1) * (s.Count - 1);
            int lo = (int)Math.Floor(pos);
            int hi = Math.Min(lo + 1, s.Count - 1);
            double f = pos - lo;
            return s[lo] * (1 - f) + s[hi] * f;
        }

        public static double Mean(IList<double> v)
        {
            if (v == null || v.Count == 0) return double.NaN;
            double s = 0; foreach (double d in v) s += d;
            return s / v.Count;
        }

        public static double StdDev(IList<double> v)
        {
            if (v == null || v.Count < 2) return 0;
            double m = Mean(v), s = 0;
            foreach (double d in v) s += (d - m) * (d - m);
            return Math.Sqrt(s / (v.Count - 1));
        }

        /// <summary>Least squares line fit y = a + b*x. Returns slope b.</summary>
        public static double LinearSlope(IList<double> x, IList<double> y, out double intercept)
        {
            int n = Math.Min(x.Count, y.Count);
            intercept = 0;
            if (n < 2) return 0;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i]; }
            double den = n * sxx - sx * sx;
            if (Math.Abs(den) < 1e-18) { intercept = sy / n; return 0; }
            double b = (n * sxy - sx * sy) / den;
            intercept = (sy - b * sx) / n;
            return b;
        }

        /// <summary>Finds the dominant periodicity (Hz) of a control signal sampled at 'rate' Hz,
        /// searching minHz..maxHz via normalised autocorrelation. Returns 0 if nothing clear.</summary>
        public static double DominantModRate(IList<double> sig, double rate, double minHz, double maxHz, out double strength)
        {
            strength = 0;
            int n = sig.Count;
            if (n < 8) return 0;
            double m = Mean(sig);
            double[] x = new double[n];
            // remove mean and linear trend
            double[] idx = new double[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            double icpt;
            double slope = LinearSlope(idx, sig, out icpt);
            for (int i = 0; i < n; i++) x[i] = sig[i] - (icpt + slope * i);
            double e0 = 0; for (int i = 0; i < n; i++) e0 += x[i] * x[i];
            if (e0 < 1e-12) return 0;
            int minLag = Math.Max(1, (int)Math.Floor(rate / maxHz));
            int maxLag = Math.Min(n / 2, (int)Math.Ceiling(rate / minHz));
            if (maxLag <= minLag + 1) return 0;
            double[] r = new double[maxLag + 2];
            for (int lag = 0; lag <= maxLag + 1 && lag < n; lag++)
            {
                double s = 0;
                for (int i = 0; i + lag < n; i++) s += x[i] * x[i + lag];
                r[lag] = s / e0 * n / (double)(n - lag); // unbiased
            }
            // strongest local maximum, but prefer the first one that is nearly as strong (avoids picking multiples)
            double best = 0; int bestLag = -1;
            for (int lag = Math.Max(minLag, 1); lag <= maxLag; lag++)
                if (r[lag] > r[lag - 1] && r[lag] >= r[lag + 1] && r[lag] > best) { best = r[lag]; bestLag = lag; }
            if (bestLag > 0)
                for (int lag = Math.Max(minLag, 1); lag < bestLag; lag++)
                    if (r[lag] > r[lag - 1] && r[lag] >= r[lag + 1] && r[lag] >= 0.85 * best) { best = r[lag]; bestLag = lag; break; }
            if (bestLag < 0) return 0;
            double a = r[bestLag - 1], b = r[bestLag], c = r[bestLag + 1];
            double den = a - 2 * b + c;
            double off = Math.Abs(den) > 1e-12 ? 0.5 * (a - c) / den : 0;
            strength = best;
            return rate / (bestLag + off);
        }

        // ----------------------------------------------------- music helpers
        private static readonly string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        public static string PitchClassName(int pc) { return names[((pc % 12) + 12) % 12]; }
        public static double FreqToMidi(double f) { return 69.0 + 12.0 * Math.Log(f / 440.0, 2); }
        public static double MidiToFreq(double m) { return 440.0 * Math.Pow(2.0, (m - 69.0) / 12.0); }
        public static string NoteName(double freq)
        {
            if (!(freq > 0)) return "-";
            int m = (int)Math.Round(FreqToMidi(freq));
            return names[((m % 12) + 12) % 12] + ((m / 12) - 1).ToString();
        }
        public static double CentsOff(double freq)
        {
            double m = FreqToMidi(freq);
            return (m - Math.Round(m)) * 100.0;
        }

        public static string Ms(double seconds)
        {
            double ms = seconds * 1000.0;
            if (ms < 10) return ms.ToString("0.0") + " ms";
            if (ms < 1000) return ms.ToString("0") + " ms";
            return seconds.ToString("0.00") + " s";
        }

        public static string Hz(double hz)
        {
            if (hz >= 1000) return (hz / 1000.0).ToString("0.0#") + " kHz";
            return hz.ToString("0") + " Hz";
        }
    }
}
