using System;
using System.Collections.Generic;

namespace SynthAnalyser
{
    /// <summary>A small subtractive synth that renders a SynthPatch so the recipe can be auditioned.</summary>
    public static class PreviewSynth
    {
        class Svf
        {
            double ic1, ic2, a1, a2, a3, k;
            public void Set(double fc, double q, int sr)
            {
                fc = Math.Min(Math.Max(fc, 10), sr * 0.45);
                double g = Math.Tan(Math.PI * fc / sr);
                k = 1.0 / q;
                a1 = 1.0 / (1 + g * (g + k)); a2 = g * a1; a3 = g * a2;
            }
            public double Low, Band, High;
            public void Tick(double v0)
            {
                double v3 = v0 - ic2;
                double v1 = a1 * ic1 + a2 * v3;
                double v2 = ic2 + a2 * ic1 + a3 * v3;
                ic1 = 2 * v1 - ic1; ic2 = 2 * v2 - ic2;
                Low = v2; Band = v1; High = v0 - k * v1 - v2;
            }
        }

        static double PolyBlep(double t, double dt)
        {
            if (t < dt) { t /= dt; return t + t - t * t - 1.0; }
            if (t > 1.0 - dt) { t = (t - 1.0) / dt; return t * t + t + t + 1.0; }
            return 0;
        }

        static double Osc(Wave w, double ph, double dt, double pw)
        {
            switch (w)
            {
                case Wave.Sine: return Math.Sin(2 * Math.PI * ph);
                case Wave.Triangle: return 1 - 4 * Math.Abs(ph - 0.5);
                case Wave.Saw: return 2 * ph - 1 - PolyBlep(ph, dt);
                case Wave.Square:
                case Wave.Pulse:
                    {
                        double width = w == Wave.Square ? 0.5 : pw;
                        double v = ph < width ? 1 : -1;
                        v += PolyBlep(ph, dt);
                        double p2 = ph - width; if (p2 < 0) p2 += 1;
                        v -= PolyBlep(p2, dt);
                        return v - (2 * width - 1);   // remove DC
                    }
                default: return 0;
            }
        }

        static double EnvAt(double t, double gate, double a, double d, double s, double r, out bool done)
        {
            done = false;
            double tauD = s < 0.01 ? Math.Max(1e-4, d / 6.9) : Math.Max(1e-4, d / 4.0);
            Func<double, double> held = tt =>
            {
                if (tt < a) return a > 0 ? tt / a : 1;
                return s + (1 - s) * Math.Exp(-(tt - a) / tauD);
            };
            if (t < gate) return held(t);
            double lv = held(gate);
            double v = lv * Math.Exp(-(t - gate) / Math.Max(1e-4, r / 6.9));
            if (v < 1e-4 && t > gate) done = true;
            return v;
        }

        public static float[][] Render(SynthPatch p, List<NoteEvent> notes, int sr, double totalSec, double refFreq)
        {
            int n = (int)(totalSec * sr);
            float[] L = new float[n], R = new float[n];
            Random rnd = new Random(1234);
            double prevFreq = 0, prevEnd = -1;
            List<NoteEvent> sorted = new List<NoteEvent>(notes);
            sorted.Sort((x, y) => x.Start.CompareTo(y.Start));
            foreach (NoteEvent ne in sorted)
            {
                double startFreq = ne.Freq;
                if (p.Mono && p.Glide > 0 && prevFreq > 0 && ne.Start <= prevEnd + 0.02) startFreq = prevFreq;
                RenderNote(p, ne, startFreq, sr, L, R, rnd, refFreq);
                prevFreq = ne.Freq; prevEnd = ne.Start + ne.Duration;
            }
            ApplyEffects(p, L, R, sr);
            return new float[][] { L, R };
        }

        static void RenderNote(SynthPatch p, NoteEvent ne, double startFreq, int sr, float[] L, float[] R, Random rnd, double refFreq)
        {
            int s0 = (int)(ne.Start * sr);
            int voices = Math.Max(1, p.UnisonVoices);
            double[] det = new double[voices], ph = new double[voices], pan = new double[voices];
            for (int v = 0; v < voices; v++)
            {
                double pos = voices == 1 ? 0 : -1 + 2.0 * v / (voices - 1);
                det[v] = pos * p.UnisonDetuneCents;
                pan[v] = voices == 1 ? 0 : pos * p.StereoWidth * (v % 2 == 0 ? 1 : -1);
                ph[v] = voices == 1 ? 0 : rnd.NextDouble();
            }
            double subPh = 0;
            double norm = 1.0 / Math.Sqrt(voices);
            Svf[] fl1 = { new Svf(), new Svf() }, fl2 = { new Svf(), new Svf() }, hp = { new Svf(), new Svf() };
            double q2 = FilterModel.QFromRes(p.Resonance, p.FilterSlope);
            double keyScale = refFreq > 0 ? Math.Pow(ne.Freq / refFreq, p.KeyTrack) : 1;
            double maxLen = ne.Duration + Math.Max(p.AR, p.FR) * 1.5 + 0.05;
            int len = Math.Min(L.Length - s0, (int)(maxLen * sr));
            double vel = ne.Velocity;
            for (int i = 0; i < len; i++)
            {
                double t = i / (double)sr;
                bool done;
                double amp = EnvAt(t, ne.Duration, p.AA, p.AD, p.AS, p.AR, out done);
                if (done) break;
                // pitch
                double semis = 0;
                if (Math.Abs(p.PitchEnvSemis) > 0) semis += p.PitchEnvSemis * Math.Exp(-t * 5.0 / Math.Max(0.002, p.PitchEnvTime));
                if (p.VibratoRate > 0)
                {
                    double fade = p.VibratoDelay > 0 ? Math.Min(1, Math.Max(0, (t - p.VibratoDelay * 0.5) / (p.VibratoDelay * 0.5))) : 1;
                    semis += fade * p.VibratoCents / 100.0 * Math.Sin(2 * Math.PI * p.VibratoRate * t);
                }
                double f = ne.Freq;
                if (startFreq != ne.Freq) f = ne.Freq * Math.Pow(startFreq / ne.Freq, Math.Exp(-t * 3.0 / p.Glide));
                f *= Math.Pow(2, semis / 12.0);

                double l = 0, r = 0;
                if (p.Osc1Wave != Wave.Noise)
                {
                    for (int v = 0; v < voices; v++)
                    {
                        double fv = f * Math.Pow(2, det[v] / 1200.0);
                        double dt = fv / sr;
                        double o = Osc(p.Osc1Wave, ph[v], dt, p.PulseWidth) * norm;
                        ph[v] += dt; if (ph[v] >= 1) ph[v] -= 1;
                        l += o * (1 - pan[v]) * 0.5;
                        r += o * (1 + pan[v]) * 0.5;
                    }
                    if (p.SubLevel > 0)
                    {
                        double fs = f * Math.Pow(2, p.SubOctave);
                        double so = Math.Sin(2 * Math.PI * subPh) * p.SubLevel * 0.8;
                        subPh += fs / sr; if (subPh >= 1) subPh -= 1;
                        l += so * 0.5; r += so * 0.5;
                    }
                }
                double nl = p.Osc1Wave == Wave.Noise ? 1.0 : p.NoiseLevel;
                if (nl > 0)
                {
                    double ng = nl * (p.NoiseDecay > 0 ? Math.Exp(-t * 3.0 / p.NoiseDecay) : 1.0) * 0.6;
                    l += (rnd.NextDouble() * 2 - 1) * ng; r += (rnd.NextDouble() * 2 - 1) * ng;
                }
                l *= 2; r *= 2; // pan law compensation (each side got half)

                // filter
                if (p.Filter != FilterMode.Off || p.HighPassHz > 0)
                {
                    if ((i & 15) == 0)
                    {
                        bool fd;
                        double fenv = EnvAt(t, ne.Duration, p.FA, Math.Max(0.001, p.FD * 6.9 / 2.3), p.FS, p.FR, out fd);
                        double oct = p.FilterEnvOct * fenv;
                        if (p.CutoffLfoRate > 0) oct += p.CutoffLfoOct * Math.Sin(2 * Math.PI * p.CutoffLfoRate * t);
                        double fc = p.Cutoff * keyScale * Math.Pow(2, oct);
                        for (int c = 0; c < 2; c++)
                        {
                            if (p.FilterSlope == 24 && p.Filter != FilterMode.BandPass) { fl1[c].Set(fc, 0.541, sr); fl2[c].Set(fc, q2, sr); }
                            else fl1[c].Set(fc, q2, sr);
                            if (p.HighPassHz > 0) hp[c].Set(p.HighPassHz, 0.707, sr);
                        }
                    }
                    double[] io = { l, r };
                    for (int c = 0; c < 2; c++)
                    {
                        double v = io[c];
                        if (p.Filter != FilterMode.Off)
                        {
                            fl1[c].Tick(v);
                            if (p.Filter == FilterMode.BandPass) v = fl1[c].Band / q2;
                            else if (p.FilterSlope == 24)
                            {
                                double s1 = p.Filter == FilterMode.LowPass ? fl1[c].Low : fl1[c].High;
                                fl2[c].Tick(s1);
                                v = p.Filter == FilterMode.LowPass ? fl2[c].Low : fl2[c].High;
                            }
                            else v = p.Filter == FilterMode.LowPass ? fl1[c].Low : fl1[c].High;
                        }
                        if (p.HighPassHz > 0) { hp[c].Tick(v); v = hp[c].High; }
                        io[c] = v;
                    }
                    l = io[0]; r = io[1];
                }

                if (p.PunchDb > 0) amp *= 1 + (Math.Pow(10, p.PunchDb / 20) - 1) * Math.Exp(-t * 3.0 / Math.Max(0.005, p.PunchTime));
                if (p.TremoloRate > 0) amp *= 1 - p.TremoloDepth * 0.5 * (1 + Math.Sin(2 * Math.PI * p.TremoloRate * t));
                amp *= vel * 0.25;
                L[s0 + i] += (float)(l * amp);
                R[s0 + i] += (float)(r * amp);
            }
        }

        static void ApplyEffects(SynthPatch p, float[] L, float[] R, int sr)
        {
            int n = L.Length;
            if (p.Drive > 0)
            {
                double k = 1 + p.Drive * 8, nk = Math.Tanh(k * 0.5);
                double pk = 1e-9;
                for (int i = 0; i < n; i++) pk = Math.Max(pk, Math.Max(Math.Abs(L[i]), Math.Abs(R[i])));
                for (int i = 0; i < n; i++)
                {
                    L[i] = (float)(Math.Tanh(k * L[i] / pk * 0.5) / nk * pk);
                    R[i] = (float)(Math.Tanh(k * R[i] / pk * 0.5) / nk * pk);
                }
            }
            if (p.ChorusMix > 0)
            {
                float[] dl = (float[])L.Clone(), dr = (float[])R.Clone();
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)sr;
                    double d1 = (0.015 + 0.004 * Math.Sin(2 * Math.PI * 0.7 * t)) * sr;
                    double d2 = (0.015 + 0.004 * Math.Sin(2 * Math.PI * 0.7 * t + Math.PI / 2)) * sr;
                    L[i] = (float)(dl[i] * (1 - p.ChorusMix * 0.5) + Frac(dl, i - d1) * p.ChorusMix);
                    R[i] = (float)(dr[i] * (1 - p.ChorusMix * 0.5) + Frac(dr, i - d2) * p.ChorusMix);
                }
            }
            if (p.DelayTime > 0 && p.DelayMix > 0)
            {
                int d = Math.Max(1, (int)(p.DelayTime * sr));
                float[] bl = new float[n], br = new float[n];
                for (int i = 0; i < n; i++)
                {
                    double il = i >= d ? L[i - d] + bl[i - d] * p.DelayFeedback : 0;
                    double ir = i >= d ? R[i - d] + br[i - d] * p.DelayFeedback : 0;
                    bl[i] = (float)il; br[i] = (float)ir;
                }
                for (int i = 0; i < n; i++) { L[i] += bl[i] * (float)p.DelayMix; R[i] += br[i] * (float)p.DelayMix; }
            }
            if (p.ReverbMix > 0 && p.ReverbTime > 0)
            {
                float[] wl = Reverb(L, sr, p.ReverbTime, 0), wr = Reverb(R, sr, p.ReverbTime, 23);
                for (int i = 0; i < n; i++) { L[i] += wl[i] * (float)p.ReverbMix; R[i] += wr[i] * (float)p.ReverbMix; }
            }
        }

        static double Frac(float[] x, double pos)
        {
            if (pos < 0) return 0;
            int i = (int)pos; double f = pos - i;
            if (i + 1 >= x.Length) return 0;
            return x[i] * (1 - f) + x[i + 1] * f;
        }

        static float[] Reverb(float[] x, int sr, double rt60, int spread)
        {
            int n = x.Length;
            double sc = sr / 44100.0;
            int[] combs = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
            int[] aps = { 556, 441, 341, 225 };
            float[] outp = new float[n];
            foreach (int cl0 in combs)
            {
                int cl = (int)((cl0 + spread) * sc);
                double g = Math.Pow(10, -3.0 * cl / (rt60 * sr));
                float[] buf = new float[cl];
                int idx = 0; double lp = 0;
                for (int i = 0; i < n; i++)
                {
                    double y = buf[idx];
                    lp = y * 0.8 + lp * 0.2;
                    buf[idx] = (float)(x[i] + lp * g);
                    outp[i] += (float)y;
                    if (++idx >= cl) idx = 0;
                }
            }
            foreach (int al0 in aps)
            {
                int al = (int)((al0 + spread) * sc);
                float[] buf = new float[al]; int idx = 0;
                for (int i = 0; i < n; i++)
                {
                    double bo = buf[idx];
                    double y = -outp[i] + bo;
                    buf[idx] = (float)(outp[i] + bo * 0.5);
                    outp[i] = (float)y;
                    if (++idx >= al) idx = 0;
                }
            }
            for (int i = 0; i < n; i++) outp[i] *= 0.12f;
            return outp;
        }
    }
}
