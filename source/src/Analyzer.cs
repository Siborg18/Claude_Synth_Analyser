using System;
using System.Collections.Generic;
using System.Linq;

namespace SynthAnalyser
{
    public class Measurement
    {
        public string Group, Name, Value, Note;
        public Measurement(string g, string n, string v, string note) { Group = g; Name = n; Value = v; Note = note; }
    }

    public class AnalysisResult
    {
        public SynthPatch Patch = new SynthPatch();
        public int SampleRate;
        public double Duration;

        public string Category = "Unknown";
        public int Confidence = 50;

        public bool Pitched;
        public double F0;               // played note frequency (main oscillator)
        public double VoicedRatio;
        public bool Inharmonic;
        public string InharmonicRatios;
        public string FmHint;
        public double TransientDb, TransientTime, Decay25Db = -1;
        public double Centroid, Edge40, AttackMeasured, UnisonCents, StereoCorr = 1;
        public string FileName;
        public bool ReleaseKnown = true;
        public bool IsChord;
        public string ChordName;
        public List<double> ChordFreqs = new List<double>();

        public bool IsPhrase;
        public double Bpm;
        public string PhraseDescription;
        public List<NoteEvent> Notes = new List<NoteEvent>();   // notes for the preview synth
        public List<double> Onsets = new List<double>();

        // display data
        public double[] Env;            // linear 0..1 at EnvRate, whole clip
        public double EnvRate = 200;
        public double[] PitchTrack;     // Hz at 100 frames/s (0 = unvoiced)
        public double[] SpecDb;         // late spectrum in dB, relative to max
        public double SpecBinHz;
        public List<Harmonic> Harmonics = new List<Harmonic>();
        public double ModelGainDb;
        public double NoteStart, NoteEnd;   // analysed note region (seconds within clip)
        public double FitErrorDb;

        public List<Measurement> Measurements = new List<Measurement>();
        public List<string> Notes2 = new List<string>();        // extra observations / tips
        public string Recipe;

        public void M(string g, string n, string v, string note) { Measurements.Add(new Measurement(g, n, v, note ?? "")); }
    }

    public static partial class Analyzer
    {
        public const double PitchRate = 100.0;

        public static AnalysisResult Analyze(AudioClip clip)
        {
            AnalysisResult r = new AnalysisResult();
            int sr = clip.SampleRate;
            r.SampleRate = sr;
            r.Duration = clip.Duration;
            r.FileName = clip.SourcePath != null ? System.IO.Path.GetFileName(clip.SourcePath) : "";
            float[] x = clip.Mono();
            int n = x.Length;
            if (n < sr / 50) throw new InvalidOperationException("The selection is too short to analyse (min 20 ms).");

            double pk = 0;
            for (int i = 0; i < n; i++) pk = Math.Max(pk, Math.Abs(x[i]));
            if (pk < 1e-6) throw new InvalidOperationException("The selection is silent.");
            // clipping: runs of >= 5 (nearly) identical samples close to the peak level
            int clipCount = 0;
            for (int i = 0; i < n; )
            {
                if (Math.Abs(x[i]) < 0.9 * pk) { i++; continue; }
                int j = i + 1;
                while (j < n && Math.Abs(x[j] - x[i]) < 1e-5) j++;
                if (j - i >= 5) clipCount += j - i;
                i = j;
            }
            float g = (float)(1.0 / pk);
            for (int i = 0; i < n; i++) x[i] *= g;
            float[][] st = null;
            if (clip.ChannelCount >= 2)
            {
                st = new float[2][];
                for (int c = 0; c < 2; c++)
                {
                    st[c] = new float[n];
                    for (int i = 0; i < n; i++) st[c][i] = clip.Channels[c][i] * g;
                }
            }
            r.M("File", "Sample rate", sr + " Hz", null);
            r.M("File", "Channels", clip.ChannelCount.ToString(), clip.ChannelCount >= 2 ? "stereo" : "mono");
            r.M("File", "Analysed length", r.Duration.ToString("0.000") + " s", null);
            r.M("File", "Peak level", Dsp.ToDb(pk).ToString("0.0") + " dBFS", null);

            // ---- global tracks
            r.Env = CoarseEnvelope(x, sr, r.EnvRate);
            double[] ap;
            r.PitchTrack = TrackPitch(x, sr, out ap, 0);
            {
                // low notes need a longer analysis window (>= 3 periods), otherwise the tracker wobbles
                List<double> v = r.PitchTrack.Where(q => q > 0).ToList();
                double med = v.Count > 5 ? Dsp.Median(v) : 0;
                if (med > 0 && med < 90) r.PitchTrack = TrackPitch(x, sr, out ap, med * 0.8);
            }
            List<Onset> onsets = DetectOnsets(x, sr, r.Env, r.EnvRate, r.PitchTrack);
            foreach (Onset o in onsets) r.Onsets.Add(o.Time);

            // ---- segment into notes
            List<Segment> segs = BuildSegments(onsets, r, n / (double)sr);
            r.Onsets = segs.Select(sg => sg.Start).ToList();
            Segment echoOf = null;
            if (segs.Count >= 2 && LooksLikeEcho(segs))
            {
                // one note followed by delay repeats
                double ioi = 0;
                for (int i = 1; i < segs.Count; i++) ioi += segs[i].Start - segs[i - 1].Start;
                ioi /= (segs.Count - 1);
                List<double> ratios = new List<double>();
                for (int i = 1; i < segs.Count; i++) ratios.Add(segs[i].Peak / Math.Max(1e-9, segs[i - 1].Peak));
                r.Patch.DelayTime = ioi;
                r.Patch.DelayFeedback = Dsp.Clamp(Dsp.Median(ratios), 0.05, 0.9);
                r.Patch.DelayMix = Dsp.Clamp(segs[1].Peak / Math.Max(1e-9, segs[0].Peak), 0.1, 0.8);
                echoOf = segs[0];
                segs = new List<Segment> { segs[0] };
            }

            r.IsPhrase = segs.Count >= 3 || (segs.Count == 2 && r.Duration > 0.8);
            Segment rep;
            bool truncated;
            if (r.IsPhrase)
            {
                rep = PickRepresentative(segs);
                truncated = rep != segs[segs.Count - 1];
            }
            else
            {
                rep = segs[0];
                truncated = false;
                if (echoOf == null) rep.End = r.Duration;  // include everything after onset (tail, reverb)
            }

            AnalyzeNote(r, x, st, sr, rep, truncated, ap, clipCount / (double)n, echoOf != null);

            if (r.IsPhrase) AnalyzePhrase(r, segs, onsets);
            else BuildSingleNoteEvents(r, rep);

            Categorize(r);
            r.Recipe = RecipeWriter.Write(r);
            return r;
        }

        // =====================================================================
        // Envelope
        public static double[] CoarseEnvelope(float[] x, int sr, double rate)
        {
            int hop = Math.Max(1, (int)(sr / rate));
            int win = hop * 2;
            int frames = x.Length / hop + 1;
            double[] cs = new double[x.Length + 1];
            for (int i = 0; i < x.Length; i++) cs[i + 1] = cs[i] + x[i] * (double)x[i];
            double[] env = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                int a = Math.Max(0, f * hop - win / 2), b = Math.Min(x.Length, f * hop + win / 2);
                env[f] = b > a ? Math.Sqrt((cs[b] - cs[a]) / (b - a)) : 0;
            }
            double m = env.Max();
            if (m > 0) for (int f = 0; f < frames; f++) env[f] /= m;
            return env;
        }

        // =====================================================================
        // Pitch (YIN, FFT-accelerated)
        public static double[] TrackPitch(float[] x, int sr, out double[] aperiodicity, double minF0)
        {
            int hop = (int)(sr / PitchRate);
            int W = Dsp.NextPow2((int)(sr * 0.035));
            if (minF0 > 0) W = Math.Min(16384, Math.Max(W, Dsp.NextPow2((int)(3.0 * sr / minF0))));
            int maxTau = W;
            int minTau = Math.Max(2, sr / 4200);
            int M = W * 4;
            int frames = x.Length / hop + 1;
            double[] f0 = new double[frames];
            aperiodicity = new double[frames];
            double[] are = new double[M], aim = new double[M], bre = new double[M], bim = new double[M];
            double[] sq = new double[2 * W + 1];
            double[] dp = new double[maxTau + 1];
            // global level gate
            double gRms = 0;
            for (int i = 0; i < x.Length; i++) gRms = Math.Max(gRms, Math.Abs(x[i]));

            for (int f = 0; f < frames; f++)
            {
                int s = f * hop - W / 2;
                double ea = 0;
                for (int i = 0; i < W; i++)
                {
                    int idx = s + i;
                    double v = (idx >= 0 && idx < x.Length) ? x[idx] : 0;
                    ea += v * v;
                }
                aperiodicity[f] = 1;
                if (Math.Sqrt(ea / W) < 3e-4) continue;   // ~ -70 dB of peak

                Array.Clear(are, 0, M); Array.Clear(aim, 0, M); Array.Clear(bre, 0, M); Array.Clear(bim, 0, M);
                sq[0] = 0;
                for (int i = 0; i < 2 * W; i++)
                {
                    int idx = s + i;
                    double v = (idx >= 0 && idx < x.Length) ? x[idx] : 0;
                    bre[i] = v;
                    if (i < W) are[i] = v;
                    sq[i + 1] = sq[i] + v * v;
                }
                Dsp.Fft(are, aim, false);
                Dsp.Fft(bre, bim, false);
                for (int i = 0; i < M; i++)
                {
                    // conj(A) * B
                    double rr = are[i] * bre[i] + aim[i] * bim[i];
                    double ii = are[i] * bim[i] - aim[i] * bre[i];
                    bre[i] = rr; bim[i] = ii;
                }
                Dsp.Fft(bre, bim, true);
                // d(tau) and cumulative mean normalised difference
                dp[0] = 1;
                double run = 0;
                for (int tau = 1; tau <= maxTau; tau++)
                {
                    double eb = sq[tau + W] - sq[tau];
                    double d = ea + eb - 2 * bre[tau];
                    if (d < 0) d = 0;
                    run += d;
                    dp[tau] = run > 0 ? d * tau / run : 1;
                }
                int best = -1;
                for (int tau = minTau; tau < maxTau - 1; tau++)
                {
                    if (dp[tau] < 0.15)
                    {
                        while (tau + 1 < maxTau - 1 && dp[tau + 1] < dp[tau]) tau++;
                        best = tau; break;
                    }
                }
                if (best < 0)
                {
                    double mv = double.MaxValue;
                    for (int tau = minTau; tau < maxTau - 1; tau++) if (dp[tau] < mv) { mv = dp[tau]; best = tau; }
                }
                double a = dp[best - 1], b = dp[best], c = dp[best + 1];
                double den = a - 2 * b + c;
                double off = Math.Abs(den) > 1e-12 ? 0.5 * (a - c) / den : 0;
                if (Math.Abs(off) > 1) off = 0;
                aperiodicity[f] = b;
                if (b < 0.3) f0[f] = sr / (best + off);
            }
            // remove isolated voiced frames & single-frame octave glitches
            for (int f = 1; f < frames - 1; f++)
            {
                if (f0[f] > 0 && f0[f - 1] <= 0 && f0[f + 1] <= 0) f0[f] = 0;
                if (f0[f] > 0 && f0[f - 1] > 0 && f0[f + 1] > 0)
                {
                    double nb = Math.Sqrt(f0[f - 1] * f0[f + 1]);
                    double rat = f0[f] / nb;
                    if (Math.Abs(Math.Log(rat, 2)) > 0.8 && Math.Abs(Math.Log(f0[f - 1] / f0[f + 1], 2)) < 0.1) f0[f] = nb;
                }
            }
            return f0;
        }

        // =====================================================================
        // Onsets
        public class Onset { public double Time; public bool Legato; public double Strength; }

        public static List<Onset> DetectOnsets(float[] x, int sr, double[] env, double envRate, double[] pitch)
        {
            List<Onset> res = new List<Onset>();
            int hop = (int)(sr / 100.0);
            int N = Dsp.NextPow2((int)(sr * 0.023));
            int frames = x.Length / hop + 1;
            int maxBin = Math.Min(N / 2, (int)(16000.0 * N / sr));
            double[] prev = null;
            double[] flux = new double[frames];
            double[] energy = new double[frames];
            for (int f = 0; f < frames; f++)
            {
                double[] p = Dsp.PowerSpectrum(x, f * hop - N / 2, N);
                double[] l = new double[maxBin];
                double e = 0;
                for (int b = 1; b < maxBin; b++) { l[b] = Math.Log(1 + 10 * Math.Sqrt(p[b])); e += p[b]; }
                energy[f] = e;
                if (prev != null)
                {
                    double s = 0;
                    for (int b = 1; b < maxBin; b++) { double d = l[b] - prev[b]; if (d > 0) s += d; }
                    flux[f] = s;
                }
                prev = l;
            }
            double fm = flux.Max(), em = energy.Max();
            if (fm <= 0) fm = 1;
            for (int f = 0; f < frames; f++) flux[f] /= fm;

            // start of sound
            double start = 0;
            for (int i = 0; i < env.Length; i++) if (env[i] > 0.01) { start = Math.Max(0, i / envRate - 0.005); break; }
            res.Add(new Onset { Time = start, Strength = 1 });

            for (int f = 2; f < frames - 1; f++)
            {
                double t = f * hop / (double)sr;
                if (t < start + 0.06) continue;
                bool localMax = true;
                for (int k = -3; k <= 3; k++)
                {
                    int j = f + k;
                    if (j >= 0 && j < frames && flux[j] > flux[f]) { localMax = false; break; }
                }
                if (!localMax) continue;
                List<double> nb = new List<double>();
                for (int k = -12; k <= 12; k++) { int j = f + k; if (j >= 0 && j < frames) nb.Add(flux[j]); }
                double thr = Dsp.Median(nb) + 0.12;
                if (flux[f] < thr) continue;
                if (energy[f] < em * 1e-4) continue;   // quieter than -40 dB
                // require an actual energy rise (not just a spectral change of a steady note)
                // a real re-attack: level jumps >= ~4 dB within ~50 ms (unison beating / LFO wobble rise slower)
                double eBefore = WindowRms(x, sr, t - 0.06, t - 0.01), eAfter = 0;
                for (double tt = t; tt < t + 0.06; tt += 0.01) eAfter = Math.Max(eAfter, WindowRms(x, sr, tt - 0.01, tt + 0.03));
                if (eAfter < eBefore * 1.6) continue;
                if (t - res[res.Count - 1].Time < 0.05) continue;
                res.Add(new Onset { Time = t - 0.01, Strength = flux[f] });
            }

            // legato (pitch-change) onsets
            if (pitch != null)
            {
                int P = pitch.Length;
                for (int f = 12; f < P - 18; f++)
                {
                    // stable pitch before (f-12..f-3) and after (f+3..f+18): a real note change, not wobble
                    List<double> wb = new List<double>(), wa = new List<double>();
                    for (int k = f - 12; k <= f - 3; k++) if (pitch[k] > 0) wb.Add(pitch[k]);
                    for (int k = f + 3; k <= f + 18; k++) if (pitch[k] > 0) wa.Add(pitch[k]);
                    if (wb.Count < 7 || wa.Count < 11) continue;
                    double before = Dsp.Median(wb), after = Dsp.Median(wa);
                    double semis = Math.Abs(12 * Math.Log(after / before, 2));
                    if (semis < 0.75 || semis > 30) continue;
                    if (Math.Abs(semis - 12) < 0.6 || Math.Abs(semis - 24) < 0.6) continue;   // octave errors of the tracker
                    int stb = wb.Count(q => Math.Abs(12 * Math.Log(q / before, 2)) < 0.5);
                    int sta = wa.Count(q => Math.Abs(12 * Math.Log(q / after, 2)) < 0.5);
                    if (stb < 0.6 * 10 || sta < 0.6 * 16) continue;
                    double t = f / PitchRate;
                    bool near = false;
                    foreach (Onset o in res) if (Math.Abs(o.Time - t) < 0.08) { near = true; break; }
                    if (near) continue;
                    res.Add(new Onset { Time = t, Legato = true, Strength = 0.5 });
                    f += 15;
                }
                res.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            return res;
        }

        static double WindowRms(float[] x, int sr, double t0, double t1)
        {
            int a = Math.Max(0, (int)(t0 * sr)), b = Math.Min(x.Length, (int)(t1 * sr));
            if (b <= a) return 0;
            double s = 0; for (int i = a; i < b; i++) s += x[i] * (double)x[i];
            return Math.Sqrt(s / (b - a));
        }

        // =====================================================================
        // Segments
        public class Segment
        {
            public double Start, End, Peak, Freq, Sounding;
            public bool Legato, Voiced;
        }

        static List<Segment> BuildSegments(List<Onset> onsets, AnalysisResult r, double dur)
        {
            List<Segment> segs = new List<Segment>();
            for (int i = 0; i < onsets.Count; i++)
            {
                Segment s = new Segment();
                s.Start = onsets[i].Time;
                s.End = i + 1 < onsets.Count ? onsets[i + 1].Time : dur;
                s.Legato = onsets[i].Legato;
                int a = (int)(s.Start * r.EnvRate), b = Math.Min(r.Env.Length, (int)(s.End * r.EnvRate));
                double pk = 0; int pkI = a;
                for (int j = a; j < b; j++) if (r.Env[j] > pk) { pk = r.Env[j]; pkI = j; }
                s.Peak = pk;
                int e = pkI;
                while (e < b && r.Env[e] > pk * 0.1) e++;
                s.Sounding = e / r.EnvRate - s.Start;
                List<double> f0 = new List<double>();
                int pa = (int)((s.Start + 0.03) * PitchRate), pb = Math.Min(r.PitchTrack.Length, (int)(s.End * PitchRate));
                for (int j = pa; j < pb; j++) if (r.PitchTrack[j] > 0) f0.Add(r.PitchTrack[j]);
                int total = Math.Max(1, pb - pa);
                s.Voiced = f0.Count >= 3 && f0.Count >= total * 0.3;
                s.Freq = f0.Count > 0 ? Dsp.Median(f0) : 0;
                segs.Add(s);
            }
            // same pitch and no real dip in level between them -> one held note with modulation, not two notes
            for (int i = segs.Count - 1; i >= 1; i--)
            {
                Segment a0 = segs[i - 1], b0 = segs[i];
                if (b0.Legato) continue;
                bool samePitch = (!a0.Voiced && !b0.Voiced) ||
                    (a0.Voiced && b0.Voiced && a0.Freq > 0 && b0.Freq > 0 && Math.Abs(1200 * Math.Log(b0.Freq / a0.Freq, 2)) < 50);
                if (!samePitch) continue;
                int ia = (int)(a0.Start * r.EnvRate), ib = Math.Min(r.Env.Length, (int)(b0.Start * r.EnvRate) + 2);
                double pkA = 0; int pkAi = ia;
                for (int j = ia; j < ib; j++) if (r.Env[j] > pkA) { pkA = r.Env[j]; pkAi = j; }
                double dip = double.MaxValue;
                for (int j = pkAi; j < ib; j++) dip = Math.Min(dip, r.Env[j]);
                if (dip > Math.Min(pkA, b0.Peak) * 0.2)   // never fell more than ~14 dB
                {
                    a0.End = b0.End; a0.Peak = Math.Max(a0.Peak, b0.Peak); a0.Sounding = b0.Start - a0.Start + b0.Sounding;
                    segs.RemoveAt(i);
                }
            }
            // drop tiny, quiet segments (merge into previous)
            for (int i = segs.Count - 1; i >= 1; i--)
            {
                if (segs[i].Peak < 0.05 && segs[i].End - segs[i].Start < 0.08)
                {
                    segs[i - 1].End = segs[i].End;
                    segs.RemoveAt(i);
                }
            }
            return segs;
        }

        static bool LooksLikeEcho(List<Segment> segs)
        {
            if (segs.Count < 2) return false;
            List<double> iois = new List<double>();
            for (int i = 1; i < segs.Count; i++)
            {
                if (segs[i].Legato) return false;
                if (segs[i].Peak > segs[i - 1].Peak * 0.95) return false;
                if (segs[0].Voiced && segs[i].Voiced && segs[i].Freq > 0 &&
                    Math.Abs(1200 * Math.Log(segs[i].Freq / segs[0].Freq, 2)) > 60) return false;
                iois.Add(segs[i].Start - segs[i - 1].Start);
            }
            if (iois.Count >= 2)
            {
                double m = Dsp.Mean(iois);
                if (Dsp.StdDev(iois) / m > 0.12) return false;
            }
            else if (segs[1].Peak > segs[0].Peak * 0.8) return false;
            // first note must decay noticeably before the echo arrives
            return segs[0].Sounding < (segs[1].Start - segs[0].Start) * 1.05;
        }

        static Segment PickRepresentative(List<Segment> segs)
        {
            double gmax = segs.Max(s => s.Peak);
            Segment best = segs[0]; double bs = -1;
            foreach (Segment s in segs)
            {
                double d = Math.Min(2.0, s.End - s.Start);
                double sc = d * (s.Voiced ? 1 : 0.3) * (s.Peak >= gmax * 0.25 ? 1 : 0.3) * (s.Legato ? 0.7 : 1);
                if (sc > bs) { bs = sc; best = s; }
            }
            return best;
        }
    }
}
