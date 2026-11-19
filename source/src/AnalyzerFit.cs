using System;
using System.Collections.Generic;
using System.Linq;

namespace SynthAnalyser
{
    public class FitResult
    {
        public Wave Wave = Wave.Saw;
        public double Pw = 0.5;
        public bool Open = true;
        public int Slope = 24;
        public double Cutoff = 20000, Res = 0, Gain = 0, Err = 1e9;
        public double[] Residual;
        public FilterMode Mode = FilterMode.LowPass;
    }

    public static class FilterModel
    {
        public static double QFromRes(double res, int slope)
        {
            return slope == 12 ? 0.707 * Math.Pow(2, res * 3.5) : 1.307 * Math.Pow(2, res * 3.0);
        }

        static double Lp2Pow(double r, double q) { double a = 1 - r * r; return 1.0 / (a * a + (r / q) * (r / q)); }

        /// <summary>Magnitude response in dB of the model filter used by both the fitter and the preview synth.</summary>
        public static double ResponseDb(FilterMode mode, int slope, double fc, double res, double f)
        {
            if (mode == FilterMode.Off) return 0;
            double r = f / fc;
            double q = QFromRes(res, slope);
            double p;
            switch (mode)
            {
                case FilterMode.LowPass:
                    p = slope == 12 ? Lp2Pow(r, q) : Lp2Pow(r, 0.541) * Lp2Pow(r, q);
                    break;
                case FilterMode.HighPass:
                    p = slope == 12 ? Lp2Pow(r, q) * r * r * r * r : Lp2Pow(r, 0.541) * Lp2Pow(r, q) * Math.Pow(r, 8);
                    break;
                default: // band-pass (12 dB)
                    p = Lp2Pow(r, q) * (r / q) * (r / q);
                    break;
            }
            return 10 * Math.Log10(Math.Max(p, 1e-20));
        }
    }

    public static partial class Analyzer
    {
        struct Tpl { public Wave W; public double Pw; public double Pen; public Tpl(Wave w, double pw, double pen) { W = w; Pw = pw; Pen = pen; } }

        static readonly Tpl[] Templates =
        {
            new Tpl(Wave.Sine, 0.5, 0), new Tpl(Wave.Saw, 0.5, 0), new Tpl(Wave.Square, 0.5, 0.5),
            new Tpl(Wave.Triangle, 0.5, 0.8), new Tpl(Wave.Pulse, 0.25, 1.5), new Tpl(Wave.Pulse, 0.125, 2.0),
            new Tpl(Wave.Pulse, 0.33, 2.0), new Tpl(Wave.Pulse, 0.1, 2.5)
        };

        public static double TemplateDb(Wave w, double pw, int k)
        {
            double a;
            switch (w)
            {
                case Wave.Sine: a = k == 1 ? 1 : 0; break;
                case Wave.Saw: a = 1.0 / k; break;
                case Wave.Square: a = (k % 2 == 1) ? 1.0 / k : 0; break;
                case Wave.Triangle: a = (k % 2 == 1) ? 1.0 / (k * (double)k) : 0; break;
                case Wave.Pulse: a = Math.Abs(Math.Sin(Math.PI * k * pw)) / k / Math.Max(1e-6, Math.Sin(Math.PI * pw)); break;
                default: a = 1; break;
            }
            return a <= 1e-9 ? -200 : 20 * Math.Log10(a);
        }

        /// <summary>Joint fit of oscillator waveform + low-pass filter to measured harmonic levels.</summary>
        static FitResult FitFilter(List<Harmonic> h, double f0, double boundDb, FitResult restrict)
        {
            FitResult best = new FitResult();
            if (h == null) return best;
            boundDb = Math.Max(boundDb, -60);   // ignore detail more than 60 dB down
            double Lvl = Math.Max(boundDb, -42);
            int K = h.Count;
            if (K == 0 || !h.Any(x => x.Present)) return best;
            double[] obs = h.Select(x => x.Db).ToArray();
            bool[] pres = h.Select(x => x.Present && x.Db > boundDb - 10).ToArray();
            double[] wt = new double[K];
            for (int k = 0; k < K; k++) wt[k] = (k + 1) <= 8 ? 1.0 : Math.Sqrt(8.0 / (k + 1));

            // filter candidates
            List<double[]> F = new List<double[]>();
            List<double[]> Fp = new List<double[]>();   // {open, slope, fc, res, penalty}
            F.Add(new double[K]); Fp.Add(new double[] { 1, 24, 20000, 0, 0, 0 });
            double[] resSet = { 0, 0.25, 0.5, 0.7, 0.85 };
            for (double fc = Math.Max(30, f0 * 0.5); fc <= 20000; fc *= Math.Pow(2, 1 / 6.0))
                foreach (int sl in new int[] { 12, 24 })
                    foreach (double rs in resSet)
                    {
                        double[] a = new double[K];
                        for (int k = 0; k < K; k++) a[k] = FilterModel.ResponseDb(FilterMode.LowPass, sl, fc, rs, (k + 1) * f0);
                        F.Add(a); Fp.Add(new double[] { 0, sl, fc, rs, 0.3 + rs * 0.8, 0 });
                    }
            // band-pass candidates: harmonics rise towards a centre frequency and fall above it
            for (double fc = Math.Max(60, f0 * 1.5); fc <= 8000; fc *= Math.Pow(2, 1 / 6.0))
                foreach (double rs in new double[] { 0, 0.25, 0.5, 0.7 })
                {
                    double[] a = new double[K];
                    for (int k = 0; k < K; k++) a[k] = FilterModel.ResponseDb(FilterMode.BandPass, 12, fc, rs, (k + 1) * f0);
                    F.Add(a); Fp.Add(new double[] { 0, 12, fc, rs, 4.0 + rs * 0.8, 1 });
                }

            foreach (Tpl t in Templates)
            {
                if (restrict != null && (t.W != restrict.Wave || Math.Abs(t.Pw - restrict.Pw) > 1e-6)) continue;
                double[] T = new double[K];
                for (int k = 0; k < K; k++) T[k] = TemplateDb(t.W, t.Pw, k + 1);
                for (int c = 0; c < F.Count; c++)
                {
                    double[] Fc = F[c];
                    // gain offset from clearly audible harmonics
                    double sw = 0, sd = 0;
                    for (int k = 0; k < K; k++) if (pres[k] && obs[k] > Lvl && T[k] > -100) { sw += wt[k]; sd += wt[k] * (obs[k] - T[k] - Fc[k]); }
                    double g = sw > 0 ? sd / sw : 0;
                    double err = 0, ws = 0;
                    for (int k = 0; k < K; k++)
                    {
                        // everything below Lvl counts as 'practically absent' on both sides
                        double o = pres[k] ? Math.Max(obs[k], Lvl) : Lvl;
                        double pm = Math.Max(T[k] + Fc[k] + g, Lvl);
                        double e = o - pm;
                        err += wt[k] * Math.Min(e * e, 625);
                        ws += wt[k];
                    }
                    err = err / ws + t.Pen + Fp[c][4];
                    if (err < best.Err)
                    {
                        best.Err = err; best.Wave = t.W; best.Pw = t.Pw; best.Gain = g;
                        best.Open = Fp[c][0] > 0.5; best.Slope = (int)Fp[c][1]; best.Cutoff = Fp[c][2]; best.Res = Fp[c][3];
                        best.Mode = Fp[c][5] > 0.5 ? FilterMode.BandPass : FilterMode.LowPass;
                        best.Residual = new double[K];
                        for (int k = 0; k < K; k++) best.Residual[k] = pres[k] && obs[k] > Lvl ? obs[k] - Math.Max(T[k] + Fc[k] + g, Lvl) : 0;
                    }
                }
            }
            return best;
        }

        // =====================================================================
        static void BuildSingleNoteEvents(AnalysisResult r, Segment rep)
        {
            SynthPatch p = r.Patch;
            double dur = _sustained ? Math.Max(0.05, _gate) : Math.Max(0.05, p.AD);
            double f = r.F0 > 0 ? r.F0 : 261.63;
            if (r.IsChord && r.ChordFreqs.Count >= 2)
            {
                foreach (double cf in r.ChordFreqs) r.Notes.Add(new NoteEvent { Start = rep.Start, Duration = dur, Freq = cf, Velocity = 0.8 });
            }
            else r.Notes.Add(new NoteEvent { Start = rep.Start, Duration = dur, Freq = f, Velocity = 1.0 });
        }

        static void AnalyzePhrase(AnalysisResult r, List<Segment> segs, List<Onset> onsets)
        {
            SynthPatch p = r.Patch;
            double baseF = r.F0 > 0 ? r.F0 : 261.63;
            List<double> chordRatios = new List<double> { 1.0 };
            if (r.IsChord && r.ChordFreqs.Count >= 2)
            {
                double lo = r.ChordFreqs.Min();
                chordRatios = r.ChordFreqs.Select(c => c / lo).ToList();
            }
            List<double> gates = new List<double>();
            for (int i = 0; i < segs.Count; i++)
            {
                Segment s = segs[i];
                double ioi = s.End - s.Start;
                double snd = Math.Min(s.Sounding, ioi);
                if (i < segs.Count - 1 && ioi > 0.03) gates.Add(snd / ioi);
                double f = s.Voiced && s.Freq > 0 ? s.Freq : baseF;
                bool legatoNext = i + 1 < segs.Count && segs[i + 1].Legato;
                double dur = legatoNext ? ioi + 0.02 : Math.Max(0.03, _sustained ? snd : Math.Min(ioi, Math.Max(snd, 0.05)));
                foreach (double cr in chordRatios)
                    r.Notes.Add(new NoteEvent { Start = s.Start, Duration = dur, Freq = f * cr, Velocity = Math.Max(0.2, s.Peak) * (chordRatios.Count > 1 ? 0.8 : 1) });
            }
            // tempo
            r.Bpm = EstimateBpm(segs.Select(s => s.Start).ToList());
            if (r.Bpm > 0) r.M("Phrase", "Tempo (estimate)", r.Bpm.ToString("0") + " BPM", "from note spacing - may be half/double");
            r.M("Phrase", "Notes detected", segs.Count.ToString(), null);

            // gate / legato
            int legato = segs.Count(s => s.Legato);
            if (gates.Count > 0)
            {
                double gm = Dsp.Median(gates);
                r.M("Phrase", "Gate (sounding / spacing)", (gm * 100).ToString("0") + " %", gm < 0.6 ? "staccato" : (gm > 0.95 ? "legato" : "normal"));
            }
            if (legato > 0 && legato >= (segs.Count - 1) * 0.3)
            {
                p.Mono = true;
                List<double> glides = new List<double>();
                for (int i = 1; i < segs.Count; i++)
                {
                    if (!segs[i].Legato || segs[i - 1].Freq <= 0 || segs[i].Freq <= 0) continue;
                    double g = GlideTime(r.PitchTrack, segs[i].Start, segs[i - 1].Freq, segs[i].Freq);
                    if (g >= 0) glides.Add(g);
                }
                p.Glide = glides.Count > 0 ? Dsp.Clamp(Dsp.Median(glides), 0.01, 1.0) : 0.05;
                r.M("Phrase", "Legato transitions", legato + " of " + (segs.Count - 1), "glide ~" + Dsp.Ms(p.Glide));
            }

            // pattern
            List<int> midi = segs.Where(s => s.Voiced && s.Freq > 0).Select(s => (int)Math.Round(Dsp.FreqToMidi(s.Freq))).ToList();
            string names = string.Join(" ", midi.Take(16).Select(m => Dsp.PitchClassName(m) + ((m / 12) - 1)).ToArray()) + (midi.Count > 16 ? " ..." : "");
            string desc;
            if (midi.Count == 0) desc = "Rhythmic, unpitched hits";
            else if (midi.Distinct().Count() == 1) desc = "Repeated note " + names.Split(' ')[0] + " (rhythmic pattern / gated sequence)";
            else
            {
                int up = 0, down = 0;
                for (int i = 1; i < midi.Count; i++) { if (midi[i] > midi[i - 1]) up++; else if (midi[i] < midi[i - 1]) down++; }
                int steps = Math.Max(1, midi.Count - 1);
                int distinctPc = midi.Select(m => ((m % 12) + 12) % 12).Distinct().Count();
                if (up >= steps * 0.7 && distinctPc <= 5) desc = "Ascending arpeggio";
                else if (down >= steps * 0.7 && distinctPc <= 5) desc = "Descending arpeggio";
                else if (distinctPc <= 4 && up > 0 && down > 0 && midi.Count >= 6) desc = "Arpeggio / riff on " + distinctPc + " pitch classes";
                else desc = "Melodic line";
                desc += ": " + names;
            }
            r.PhraseDescription = desc;
            r.M("Phrase", "Pattern", desc, null);
        }

        static double GlideTime(double[] pt, double t, double f1, double f2)
        {
            int c = (int)(t * PitchRate);
            int a = -1, b = -1;
            for (int j = c; j >= Math.Max(0, c - 20); j--) if (pt[j] > 0 && Math.Abs(1200 * Math.Log(pt[j] / f1, 2)) < 30) { a = j; break; }
            for (int j = c; j < Math.Min(pt.Length, c + 40); j++) if (pt[j] > 0 && Math.Abs(1200 * Math.Log(pt[j] / f2, 2)) < 30) { b = j; break; }
            if (a < 0 || b < 0 || b <= a) return -1;
            return Math.Max(0, (b - a) / PitchRate - 0.02);
        }

        public static double EstimateBpm(List<double> starts)
        {
            List<double> iois = new List<double>();
            for (int i = 1; i < starts.Count; i++) { double d = starts[i] - starts[i - 1]; if (d > 0.06) iois.Add(d); }
            if (iois.Count < 3) return 0;
            double best = 0, bestS = double.MinValue;
            for (double bpm = 70; bpm <= 180; bpm += 0.5)
            {
                double six = 15.0 / bpm, s = 0;
                foreach (double d in iois)
                {
                    double q = d / six, rq = Math.Round(q);
                    if (rq < 1) { s -= 1; continue; }
                    double dev = q - rq;
                    double bonus = (rq == 1 || rq == 2 || rq == 4 || rq == 8) ? 1.0 : (rq == 3 || rq == 6 ? 0.8 : 0.6);
                    s += Math.Exp(-dev * dev / (2 * 0.08 * 0.08)) * bonus;
                }
                s -= 0.002 * Math.Abs(bpm - 120);
                if (s > bestS) { bestS = s; best = bpm; }
            }
            return bestS > iois.Count * 0.5 ? Math.Round(best) : 0;
        }

        // =====================================================================
        static void Categorize(AnalysisResult r)
        {
            SynthPatch p = r.Patch;
            double len = r.NoteEnd - r.NoteStart;
            string cat; int conf;
            if (!r.Pitched)
            {
                if (p.AS < 0.2 && p.AD < 1.0)
                {
                    cat = p.PitchEnvSemis > 3 ? "Kick / tom (pitched-down hit)" : "Percussion / noise hit";
                    conf = 65;
                }
                else { cat = "Noise / FX texture"; conf = 55; }
            }
            else if (p.PitchEnvSemis > 5 && r.F0 < 130 && p.AS < 0.3) { cat = "Kick / 808-style drum"; conf = 65; }
            else if (r.Inharmonic) { cat = "Bell / metallic"; conf = 60; }
            else if (r.IsChord) { cat = len > 1.2 && p.AA > 0.08 ? "Chord pad" : "Chord stab"; conf = 60; }
            else if (r.F0 < 150)
            {
                if (p.PitchEnvSemis > 5) cat = "808 / sub bass";
                else if (p.AS < 0.15 && p.AD < 1.5) cat = "Bass pluck";
                else if (p.CutoffLfoRate > 0) cat = "Wobble bass";
                else cat = "Bass";
                conf = 70;
            }
            else if (p.AA < 0.03 && p.AS < 0.15 && p.AD < 1.5) { cat = "Pluck"; conf = 70; }
            else if (p.AA > 0.15 || (p.UnisonVoices > 2 && p.AS > 0.5 && len > 1.5)) { cat = "Pad"; conf = 60; }
            else if (p.AS >= 0.4) { cat = "Lead"; conf = 55; }
            else { cat = "Keys / stab"; conf = 50; }
            if (r.IsPhrase && !cat.Contains("ass") && !cat.StartsWith("Kick")) cat += (r.PhraseDescription != null && r.PhraseDescription.Contains("arpeggio") ? " (arpeggio)" : " (phrase)");
            if (r.FitErrorDb > 8) conf -= 10;
            r.Category = cat;
            r.Confidence = conf;
        }
    }

    public static class ChordNamer
    {
        static readonly string[] qn = { "", "m", "7", "maj7", "m7", "sus2", "sus4", "dim", "aug", "5", "6", "m6", "add9", "7sus4", "m7b5", "dim7", "9", "m9" };
        static readonly int[][] qs =
        {
            new[]{0,4,7}, new[]{0,3,7}, new[]{0,4,7,10}, new[]{0,4,7,11}, new[]{0,3,7,10}, new[]{0,2,7}, new[]{0,5,7},
            new[]{0,3,6}, new[]{0,4,8}, new[]{0,7}, new[]{0,4,7,9}, new[]{0,3,7,9}, new[]{0,2,4,7}, new[]{0,5,7,10},
            new[]{0,3,6,10}, new[]{0,3,6,9}, new[]{0,2,4,7,10}, new[]{0,2,3,7,10}
        };

        public static string Name(List<double> freqs)
        {
            List<int> midi = freqs.Select(f => (int)Math.Round(Dsp.FreqToMidi(f))).OrderBy(m => m).ToList();
            HashSet<int> pcs = new HashSet<int>(midi.Select(m => ((m % 12) + 12) % 12));
            int bass = ((midi[0] % 12) + 12) % 12;
            string notes = string.Join("-", midi.Select(m => Dsp.PitchClassName(m) + ((m / 12) - 1)).ToArray());
            string best = null; double bestScore = -1;
            for (int root = 0; root < 12; root++)
            {
                if (!pcs.Contains(root)) continue;
                for (int q = 0; q < qs.Length; q++)
                {
                    HashSet<int> tpl = new HashSet<int>(qs[q].Select(i => (root + i) % 12));
                    int inter = tpl.Count(t => pcs.Contains(t));
                    double score = inter / (double)(tpl.Count + pcs.Count - inter);
                    if (root == bass) score += 0.05;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = Dsp.PitchClassName(root) + qn[q] + (root != bass ? "/" + Dsp.PitchClassName(bass) : "");
                    }
                }
            }
            if (best == null || bestScore < 0.5) return notes;
            return best + "  (" + notes + ")";
        }
    }
}
