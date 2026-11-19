using System;
using System.Collections.Generic;
using System.Linq;

namespace SynthAnalyser
{
    public static partial class Analyzer
    {
        // =====================================================================
        // Single-note analysis: envelope, pitch, timbre, modulation, effects
        static void AnalyzeNote(AnalysisResult r, float[] x, float[][] st, int sr, Segment seg, bool truncated,
                                double[] ap, double clipFrac, bool hasDelay)
        {
            SynthPatch p = r.Patch;
            int s0 = Math.Max(0, (int)(seg.Start * sr)), s1 = Math.Min(x.Length, (int)(seg.End * sr));
            if (s1 - s0 < sr / 50) s1 = Math.Min(x.Length, s0 + sr / 50);
            r.NoteStart = s0 / (double)sr; r.NoteEnd = s1 / (double)sr;
            float[] y = new float[s1 - s0];
            Array.Copy(x, s0, y, 0, y.Length);

            // ---------------- rough pitch
            List<double> f0s = new List<double>();
            int pa = (int)((r.NoteStart + 0.03) * PitchRate), pb = Math.Min(r.PitchTrack.Length, (int)(r.NoteEnd * PitchRate));
            for (int j = pa; j < pb; j++) if (r.PitchTrack[j] > 0) f0s.Add(r.PitchTrack[j]);
            double f0r = f0s.Count >= 3 ? Dsp.Median(f0s) : 0;

            // ---------------- fine amplitude envelope (2 ms hop)
            const double ER = 500.0;
            int hop = Math.Max(1, (int)(sr / ER));
            double win = f0r > 0 ? Dsp.Clamp(2.0 / f0r, 0.004, 0.03) : 0.005;
            int w = Math.Max(2, (int)(win * sr));
            int ef = y.Length / hop + 1;
            double[] cs = new double[y.Length + 1];
            for (int i = 0; i < y.Length; i++) cs[i + 1] = cs[i] + y[i] * (double)y[i];
            double[] env = new double[ef];
            for (int f = 0; f < ef; f++)
            {
                int a = Math.Max(0, f * hop - w / 2), b = Math.Min(y.Length, f * hop + w / 2);
                env[f] = b > a ? Math.Sqrt((cs[b] - cs[a]) / (b - a)) : 0;
            }
            int peakI = 0; double peak = 0;
            for (int f = 0; f < ef; f++) if (env[f] > peak) { peak = env[f]; peakI = f; }
            double[] db = new double[ef];
            for (int f = 0; f < ef; f++) db[f] = Math.Max(-100, Dsp.ToDb(env[f] / peak));
            double[] sdb = Smooth(db, 10);

            int t10 = 0, t90 = peakI;
            for (int f = 0; f <= peakI; f++) if (env[f] >= 0.1 * peak) { t10 = f; break; }
            for (int f = t10; f <= peakI; f++) if (env[f] >= 0.9 * peak) { t90 = f; break; }
            double rise = (t90 - t10) / ER;
            double attack = Math.Max(0.0005, rise * 1.25 - win * 0.6);
            // decay reference point: first local max after reaching 90 %
            int atkEnd = t90;
            while (atkEnd + 1 < ef && atkEnd < t90 + 25 && env[atkEnd + 1] >= env[atkEnd]) atkEnd++;
            if (env[peakI] > env[atkEnd] * 1.12 && peakI < atkEnd + 0.4 * ER) atkEnd = peakI;
            p.AA = attack;
            r.M("Amp envelope", "Attack (10-90 %)", Dsp.Ms(rise), "suggested attack " + Dsp.Ms(attack));

            int endI = ef - 1;
            while (endI > atkEnd && sdb[endI] < -60) endI--;
            bool reachedSilence = sdb[ef - 1] < -45;

            // hold at peak
            int holdI = atkEnd;
            while (holdI + 1 < ef && sdb[holdI + 1] > sdb[atkEnd] - 1.0) holdI++;
            double hold = (holdI - atkEnd) / ER;

            // ---------------- sustain plateau search
            // smoothing adapted to the note length (up to 200 ms: ignores unison beating / wobble)
            int smw = (int)Dsp.Clamp((endI0(sdb, atkEnd) - atkEnd) / 8.0, 15, 100);
            double[] hdb = Smooth(db, smw);
            double[] slope = LocalSlope(hdb, smw, ER);
            int bestA = -1, bestB = -1;
            {
                int runA = -1, gap = 0;
                for (int f = atkEnd; f <= endI; f++)
                {
                    bool flat = Math.Abs(slope[f]) < 6 && hdb[f] > -35;
                    if (flat) { if (runA < 0) runA = f; gap = 0; }
                    else if (runA >= 0)
                    {
                        gap++;
                        if (gap > 10)
                        {
                            int runB = f - gap;
                            if (bestA < 0 || runB - runA > bestB - bestA) { bestA = runA; bestB = runB; }
                            runA = -1; gap = 0;
                        }
                    }
                }
                if (runA >= 0)
                {
                    int runB = endI - Math.Min(gap, 10);
                    if (bestA < 0 || runB - runA > bestB - bestA) { bestA = runA; bestB = runB; }
                }
            }
            double noteLen = (endI - atkEnd) / ER;
            double minRun = Math.Max(0.1, 0.2 * noteLen);
            bool sustained = bestA >= 0 && (bestB - bestA) / ER >= minRun;
            if (!sustained)
            {
                // global test: level roughly constant between the attack and the final drop (wiggles allowed)
                double hmax = hdb.Skip(atkEnd).Take(endI - atkEnd + 1).DefaultIfEmpty(-100).Max();
                int lastLoud = endI; while (lastLoud > atkEnd && hdb[lastLoud] < hmax - 12) lastLoud--;
                int a0 = atkEnd + (int)(0.05 * ER);
                if ((lastLoud - a0) / ER >= Math.Max(0.3, minRun))
                {
                    List<double> xs = new List<double>(), ys = new List<double>();
                    for (int f = a0; f <= lastLoud; f++) { xs.Add(f / ER); ys.Add(sdb[f]); }
                    double icpt; double b = Dsp.LinearSlope(xs, ys, out icpt);
                    if (Math.Abs(b) < 3) { sustained = true; bestA = a0; bestB = lastLoud; }
                }
            }
            int relStartI;   // where the release (or decay to silence) begins
            double gateTime;
            bool releaseKnown = true;
            if (sustained)
            {
                List<double> lv = new List<double>();
                for (int f = bestA; f <= bestB; f++) lv.Add(sdb[f]);
                double susDb = Dsp.Median(lv);
                // compare with the smoothed peak so beating/wobble does not lower the sustain estimate
                double pkS = -100;
                for (int f = Math.Max(0, atkEnd - smw); f <= Math.Max(atkEnd, bestA); f++) pkS = Math.Max(pkS, hdb[f]);
                susDb = Math.Min(0, susDb - Math.Min(0, pkS));
                p.AS = Dsp.Clamp(Dsp.FromDb(susDb), 0, 1);
                if (susDb > -1.5) { p.AS = 1.0; p.AD = 0.1; }
                else
                {
                    int dI = atkEnd;
                    while (dI < bestB && sdb[dI] > susDb + 1.0) dI++;
                    p.AD = Math.Max(0.01, (dI - atkEnd) / ER);
                }
                // the smoothed curve bends early: walk forward to where the level really starts to drop
                while (bestB < endI && sdb[bestB + 1] > susDb - 3) bestB++;
                relStartI = bestB;
                gateTime = bestB / ER;
                if (truncated || endI - bestB < 0.03 * ER && !reachedSilence)
                {
                    releaseKnown = false;
                    p.AR = 0.15;
                }
                else
                {
                    p.AR = TimeTo60(sdb, bestB, susDb, ER);
                }
                r.M("Amp envelope", "Decay to sustain", Dsp.Ms(p.AD), null);
                r.M("Amp envelope", "Sustain level", (p.AS * 100).ToString("0") + " %  (" + susDb.ToString("0.0") + " dB)", null);
                r.M("Amp envelope", "Release (to -60 dB)", releaseKnown ? Dsp.Ms(p.AR) : "not measurable", releaseKnown ? null : "the note is cut off before it is released");
            }
            else
            {
                p.AS = 0;
                double t60 = TimeTo60(sdb, atkEnd, 0, ER);
                p.AD = Dsp.Clamp(t60, 0.02, 20);
                p.AR = Dsp.Clamp(t60 * 0.8, 0.02, 20);
                relStartI = atkEnd;
                gateTime = atkEnd / ER + t60;
                r.M("Amp envelope", "Sustain", "none", "sound decays continuously");
                r.M("Amp envelope", "Decay (to -60 dB)", Dsp.Ms(p.AD), reachedSilence ? null : "extrapolated");
            }
            if (hold > 0.04) r.M("Amp envelope", "Hold at peak", Dsp.Ms(hold), null);

            // punchy transient: a fast drop right after the peak, followed by a much slower fade
            {
                int i150 = Math.Min(ef - 1, atkEnd + (int)(0.15 * ER)), i650 = Math.Min(ef - 1, atkEnd + (int)(0.65 * ER));
                double drop = sdb[atkEnd] - sdb[i150];
                double slowRate = (sdb[i150] - sdb[i650]) / Math.Max(0.1, (i650 - i150) / ER);   // dB/s
                if (drop > 5 && i650 > i150 && slowRate < drop / 0.15 / 4)
                {
                    int iT = atkEnd; while (iT < i150 && sdb[iT] > sdb[i150] + 1) iT++;
                    r.TransientDb = drop; r.TransientTime = Math.Max(0.01, (iT - atkEnd) / ER);
                    p.PunchDb = drop; p.PunchTime = r.TransientTime;
                    r.M("Amp envelope", "Initial transient", "+" + drop.ToString("0") + " dB for ~" + Dsp.Ms(r.TransientTime), "punch/click on top of the body");
                }
            }

            // shape of the decay: how far the level falls in the first quarter of the decay time
            // (measured after any punch). ~15 dB = exponential, ~3 dB = nearly linear amplitude fall.
            if (!sustained && p.AD > 0.05)
            {
                int i0 = atkEnd + (int)(r.TransientTime * ER);
                int i25 = Math.Min(ef - 1, i0 + (int)(0.25 * p.AD * ER));
                if (i25 > i0) r.Decay25Db = Math.Max(0, sdb[i0] - sdb[i25]);
            }

            // ---------------- tremolo
            if (sustained && (bestB - bestA) / ER > 0.4)
            {
                List<double> sig = new List<double>();
                for (int f = bestA; f <= bestB; f += 5) sig.Add(db[f]);
                double str;
                double rate = Dsp.DominantModRate(sig, ER / 5, 1.0, 15, out str);
                double pp = Dsp.Percentile(Detrend(sig), 0.95) - Dsp.Percentile(Detrend(sig), 0.05);
                if (rate > 0 && str > 0.65 && pp > 1.5 && (bestB - bestA) / ER * rate >= 3)
                {
                    p.TremoloRate = rate;
                    p.TremoloDepth = Dsp.Clamp(1 - Dsp.FromDb(-pp), 0.05, 1);
                    r.M("Modulation", "Tremolo", rate.ToString("0.0") + " Hz, " + pp.ToString("0.0") + " dB p-p", "strength " + str.ToString("0.00"));
                }
            }

            // ---------------- pitch details
            double noteT0 = r.NoteStart;
            double steadyA = noteT0 + atkEnd / ER + 0.03;
            double steadyB = noteT0 + (sustained ? bestB / ER : Math.Min(gateTime, (endI) / ER));
            if (steadyB < steadyA + 0.05) steadyB = Math.Min(r.NoteEnd, steadyA + 0.1);
            List<double> steadyF = new List<double>();
            for (int j = (int)(steadyA * PitchRate); j < Math.Min(r.PitchTrack.Length, (int)(steadyB * PitchRate)); j++)
                if (j >= 0 && r.PitchTrack[j] > 0) steadyF.Add(r.PitchTrack[j]);
            int loudFrames = 0, voicedFrames = 0;
            for (int j = (int)(noteT0 * PitchRate); j < Math.Min(r.PitchTrack.Length, (int)(r.NoteEnd * PitchRate)); j++)
            {
                int ei = (int)((j / PitchRate - noteT0) * ER);
                if (ei >= 0 && ei < ef && sdb[ei] > -30)
                {
                    loudFrames++;
                    if (r.PitchTrack[j] > 0) voicedFrames++;
                }
            }
            r.VoicedRatio = loudFrames > 0 ? voicedFrames / (double)loudFrames : 0;
            double f0 = steadyF.Count >= 3 ? Dsp.Median(steadyF) : f0r;
            r.Pitched = f0 > 0 && r.VoicedRatio >= 0.35;
            r.M("Pitch", "Voiced (periodic) frames", (r.VoicedRatio * 100).ToString("0") + " %", r.Pitched ? "tonal" : "mostly noise / unpitched");

            // pitch envelope
            if (f0 > 0)
            {
                List<double> dev = new List<double>(); List<double> devT = new List<double>();
                for (int j = (int)(noteT0 * PitchRate); j < Math.Min(r.PitchTrack.Length, (int)((noteT0 + 0.4) * PitchRate)); j++)
                    if (j >= 0 && r.PitchTrack[j] > 0) { dev.Add(1200 * Math.Log(r.PitchTrack[j] / f0, 2)); devT.Add(j / PitchRate - noteT0); }
                if (dev.Count >= 4 && devT[0] < 0.05)
                {
                    double init = Dsp.Median(dev.Take(3).ToList());
                    if (Math.Abs(init) > 80)
                    {
                        double settle = devT[devT.Count - 1];
                        for (int i = 1; i + 2 < dev.Count; i++)
                            if (Math.Abs(dev[i]) < 25 && Math.Abs(dev[i + 1]) < 25 && Math.Abs(dev[i + 2]) < 25) { settle = devT[i]; break; }
                        // extrapolate the start: YIN frames are ~40 ms wide, so the true start is higher
                        p.PitchEnvSemis = Math.Round(init / 100.0 * 1.3, 1);
                        p.PitchEnvTime = Math.Max(0.005, settle);
                        r.M("Pitch", "Pitch envelope", (init > 0 ? "+" : "") + (init / 100).ToString("0.0") + " st at start", "settles after " + Dsp.Ms(settle));
                    }
                }
            }

            // fast pitch drops (kicks, 808s) are too quick for the pitch tracker: use zero crossings
            if (f0 > 0 && f0 < 250)
            {
                double zsemi, ztime;
                if (ZeroCrossPitchEnv(y, sr, f0, out zsemi, out ztime) && zsemi > Math.Max(2, p.PitchEnvSemis + 1))
                {
                    p.PitchEnvSemis = Math.Round(zsemi, 1);
                    p.PitchEnvTime = ztime;
                    r.M("Pitch", "Fast pitch drop", "+" + zsemi.ToString("0.0") + " st", "falls to the note within " + Dsp.Ms(ztime));
                }
            }

            // vibrato
            double vibCents = 0;
            if (steadyF.Count >= 40)
            {
                List<double> c = steadyF.Select(v => 1200 * Math.Log(v / f0, 2)).ToList();
                double str;
                double rate = Dsp.DominantModRate(c, PitchRate, 2.5, 10, out str);
                List<double> dt = Detrend(c);
                double depth = (Dsp.Percentile(dt, 0.95) - Dsp.Percentile(dt, 0.05)) / 2;
                if (rate > 0 && str > 0.45 && depth > 6)
                {
                    p.VibratoRate = rate;
                    p.VibratoCents = depth;
                    vibCents = depth;
                    // onset delay
                    double maxStd = 0; List<double> rs = new List<double>();
                    for (int i = 0; i + 25 <= c.Count; i++) { double sd = Dsp.StdDev(c.GetRange(i, 25)); rs.Add(sd); maxStd = Math.Max(maxStd, sd); }
                    for (int i = 0; i < rs.Count; i++) if (rs[i] > maxStd * 0.5) { double dly = i / PitchRate + (steadyA - noteT0); if (dly > 0.15) p.VibratoDelay = dly; break; }
                    r.M("Modulation", "Vibrato", rate.ToString("0.0") + " Hz, +/-" + depth.ToString("0") + " cents",
                        p.VibratoDelay > 0 ? "fades in after " + Dsp.Ms(p.VibratoDelay) : null);
                }
                double icpt;
                double[] tt = Enumerable.Range(0, c.Count).Select(i => i / PitchRate).ToArray();
                double drift = Dsp.LinearSlope(tt, c, out icpt) * c.Count / PitchRate;
                if (Math.Abs(drift) > 40) r.Notes2.Add("Pitch drifts " + drift.ToString("+0;-0") + " cents over the note - use a slow pitch envelope/bend or glide.");
            }

            // ---------------- spectra
            double lateA = steadyA, lateB = steadyB;
            if (!sustained)
            {
                // decaying sound: use the part between -6 and -30 dB for the 'settled' timbre
                int a6 = atkEnd; while (a6 < endI && sdb[a6] > -6) a6++;
                int b30 = a6; while (b30 < endI && sdb[b30] > -30) b30++;
                lateA = noteT0 + a6 / ER; lateB = noteT0 + b30 / ER;
                if (lateB - lateA < 0.03) { lateA = noteT0 + atkEnd / ER; lateB = lateA + 0.1; }
            }
            double fS = f0 > 0 ? f0 : 100;
            int N = Dsp.NextPow2((int)Math.Max(sr * 0.17, 10.0 * sr / fS));
            N = Math.Min(N, 32768);
            double[] P = AverageSpectrum(x, sr, lateA, lateB, N);
            double binHz = sr / (double)N;
            double pmax = P.Max();
            r.SpecDb = P.Select(v => Dsp.PowToDb(v / pmax)).ToArray();
            r.SpecBinHz = binHz;
            int topBin = Math.Min(P.Length - 1, (int)(Math.Min(16000, 0.45 * sr) / binHz));
            List<double> allDb = new List<double>();
            for (int b = (int)(40 / binHz) + 1; b < topBin; b++) allDb.Add(r.SpecDb[b]);
            double floorDb = Dsp.Percentile(allDb, 0.35);

            // early spectrum (attack region) for filter envelope
            int Ne = Dsp.NextPow2((int)Math.Max(sr * 0.04, 4.0 * sr / fS));
            Ne = Math.Min(Ne, N);
            double eA = noteT0 + Math.Max(0, atkEnd / ER - 0.005);
            double[] Pe = AverageSpectrum(x, sr, eA, eA + Ne / (double)sr * 0.5, Ne);

            // tonal / harmonic energy
            double total = 0;
            for (int b = (int)(20 / binHz) + 1; b < topBin; b++) total += P[b];
            List<int> peaks = FindPeaks(P, binHz, floorDb, pmax, topBin);

            List<Harmonic> harm = null;
            double harmFrac = 0, tonalFrac = 0;
            if (f0 > 0)
            {
                harm = ExtractHarmonics(P, binHz, f0, sr, floorDb, pmax);
                f0 = RefineF0(harm, f0);
                harm = ExtractHarmonics(P, binHz, f0, sr, floorDb, pmax);
                harmFrac = BandEnergy(P, binHz, harm.Where(h => h.Present).Select(h => h.Freq), f0, true) / Math.Max(1e-30, total);
            }
            tonalFrac = PeakEnergy(P, peaks, binHz, f0) / Math.Max(1e-30, total);
            tonalFrac = Math.Max(tonalFrac, harmFrac);
            // overall brightness figures (used by the comparison feature)
            {
                double num = 0, den = 0;
                for (int b = Math.Max(1, (int)(20 / binHz)); b < topBin; b++) { double m = Math.Sqrt(P[b]); num += m * b * binHz; den += m; }
                r.Centroid = den > 0 ? num / den : 0;
                double best = -300; List<double> fc = new List<double>(), lv = new List<double>();
                for (double f = 31.5; f < Math.Min(16000, 0.45 * sr); f *= Math.Pow(2, 1 / 3.0))
                {
                    int a = Math.Max(1, (int)(f / 1.12 / binHz)), b = Math.Min(P.Length - 1, (int)(f * 1.12 / binHz));
                    double e = 0; for (int i = a; i <= b; i++) e += P[i];
                    double d = Dsp.PowToDb(e + 1e-30); fc.Add(f); lv.Add(d); best = Math.Max(best, d);
                }
                r.Edge40 = 0;
                for (int i = 0; i < fc.Count; i++) if (lv[i] > best - 40) r.Edge40 = fc[i];
            }
            r.AttackMeasured = rise;
            r.M("Timbre", "Tonal energy", (tonalFrac * 100).ToString("0") + " %", "energy in sharp spectral peaks");
            if (f0 > 0) r.M("Timbre", "Harmonic energy", (harmFrac * 100).ToString("0") + " %", "energy on the " + Dsp.Hz(f0) + " harmonic series");

            // chord / inharmonic?
            bool sparse = harm != null && harm.Count(h => h.Present) >= 3 &&
                          (harm.Take(8).Count(h => h.Present) < 3 ||
                           (harm.Count >= 2 && !(harm[0].Present && harm[0].Db > -25) && !(harm[1].Present && harm[1].Db > -25)));
            bool dense = harm != null && harm.Take(16).Count(h => h.Present) >= 0.6 * Math.Min(16, harm.Count);
            // a single harmonic series that explains (nearly) everything is one note - even when its lowest
            // harmonics are missing (high-passed) - not a chord
            bool oneSeries = f0 >= 40 && harmFrac >= 0.85 * tonalFrac && harm != null && harm.Count(h => h.Present) >= 6;
            if (!oneSeries && tonalFrac > 0.4 && (f0 <= 0 || harmFrac < 0.6 * tonalFrac || sparse || (f0 < 35 && !dense)))
            {
                double explained;
                List<double> notes = DetectChord(P, binHz, peaks, out explained);
                if (notes.Count >= 2 && explained > 0.7)
                {
                    r.IsChord = true;
                    r.ChordFreqs = notes;
                    r.ChordName = ChordNamer.Name(notes);
                    p.PitchEnvSemis = 0;   // was measured against a wrong single-note pitch
                    r.Measurements.RemoveAll(m => m.Name == "Pitch envelope" || m.Name == "Fast pitch drop");
                    f0 = notes.Min();
                    r.Pitched = true;
                    r.M("Pitch", "Chord", r.ChordName, "explains " + (explained * 100).ToString("0") + " % of the peak energy");
                    harm = ExtractHarmonics(P, binHz, f0, sr, floorDb, pmax);
                }
                else if (peaks.Count >= 3 && (f0 <= 0 || sparse || harmFrac < 0.6 * tonalFrac))
                {
                    r.Inharmonic = true;
                    r.FmHint = FmGuess(P, peaks, binHz);
                    r.InharmonicRatios = PartialRatios(P, peaks, binHz);
                    r.M("Timbre", "Inharmonic partials", r.InharmonicRatios, "ratios to the lowest strong partial");
                    double car = FmCarrier(P, peaks, binHz);
                    if (car > 0) f0 = car;
                    else if (f0 <= 0) f0 = PartialBase(P, peaks, binHz);
                    r.Pitched = f0 > 0 && (r.VoicedRatio >= 0.35 || tonalFrac > 0.6);
                    p.PitchEnvSemis = 0;
                }
            }

            if (f0 > 0 && harm == null) harm = ExtractHarmonics(P, binHz, f0, sr, floorDb, pmax);
            if (harm == null || harm.Count == 0 || !harm.Any(h => h.Present)) r.Pitched = false;
            r.F0 = f0;
            if (r.Pitched)
            {
                r.M("Pitch", "Fundamental", f0.ToString("0.0") + " Hz", Dsp.NoteName(f0) + " " + Dsp.CentsOff(f0).ToString("+0;-0;0") + " cents");
            }

            // ---------------- noise content
            double noiseFrac = Dsp.Clamp(1 - tonalFrac, 0, 1);
            double flatLate = Flatness(P, binHz, sr);
            double[] Pa = Dsp.PowerSpectrum(x, (int)(noteT0 * sr), Dsp.NextPow2(sr / 50));
            double flatAtk = Flatness(Pa, sr / (double)Dsp.NextPow2(sr / 50), sr);
            r.M("Timbre", "Noise content", (noiseFrac * 100).ToString("0") + " %", "spectral flatness " + flatLate.ToString("0.00"));

            if (!r.Pitched)
            {
                AnalyzeNoiseSound(r, P, binHz, sr);
            }
            else
            {
                // ---------------- waveform + filter fit
                bool subMode = false;
                double fitF0 = f0;
                List<Harmonic> hLate = harm;
                if (!r.Inharmonic && DetectSubOsc(harm))
                {
                    subMode = true;
                    double h1 = harm[0].Db, h2 = harm[1].Db;
                    p.SubLevel = Dsp.Clamp(Dsp.FromDb(h1 - h2), 0.1, 1.5);
                    fitF0 = f0 * 2;
                    hLate = ExtractHarmonics(P, binHz, fitF0, sr, floorDb, pmax);
                    p.SubOctave = -1;
                    r.F0 = fitF0;
                    r.M("Timbre", "Sub oscillator", "yes, -1 oct", "odd harmonics missing, strong fundamental - main osc plays " + Dsp.NoteName(fitF0));
                }
                double eFloor;
                List<Harmonic> hEarly = ExtractHarmonicsWithFloor(Pe, sr / (double)Ne, fitF0, sr, out eFloor);
                FitResult fe = FitFilter(hEarly, fitF0, eFloor + 10, null);
                FitResult fl = FitFilter(hLate, fitF0, NormBound(hLate, floorDb, pmax), null);
                int presE = hEarly.Count(h => h.Present), presL = hLate.Count(h => h.Present);
                FitResult waveSrc = presE > presL + 2 ? fe : fl;
                // refit late with the chosen waveform
                fl = FitFilter(hLate, fitF0, NormBound(hLate, floorDb, pmax), waveSrc);
                fe = FitFilter(hEarly, fitF0, eFloor + 10, waveSrc);
                r.Harmonics = hLate;
                r.ModelGainDb = fl.Gain;
                r.FitErrorDb = Math.Sqrt(Math.Max(0, fl.Err));

                p.Osc1Wave = waveSrc.Wave; p.PulseWidth = waveSrc.Pw;
                if (r.Inharmonic) { p.Osc1Wave = Wave.Sine; }
                r.M("Timbre", "Best waveform match", SynthPatch.WaveName(p.Osc1Wave, p.PulseWidth), "fit error " + r.FitErrorDb.ToString("0.0") + " dB rms");

                double highestHarm = fitF0 * hLate.Count;
                bool lateOpen = fl.Open || fl.Cutoff > Math.Min(18000, highestHarm * 1.2);
                bool earlyOpen = fe.Open || fe.Cutoff > Math.Min(18000, fitF0 * hEarly.Count * 1.2);
                double cutLate = lateOpen ? 20000 : fl.Cutoff;
                double cutEarly = earlyOpen ? 20000 : fe.Cutoff;
                if (!lateOpen || !earlyOpen)
                {
                    p.Filter = (fl.Open ? fe.Mode : fl.Mode);
                    p.FilterSlope = fl.Open ? fe.Slope : fl.Slope;
                    p.Resonance = fl.Open ? fe.Res : fl.Res;
                    p.Cutoff = Math.Min(cutLate, 20000);
                    double envOct = Math.Log(Math.Min(cutEarly, 20000) / Math.Min(cutLate, 20000), 2);
                    if (Math.Abs(envOct) > 0.7 && fe.Mode == fl.Mode) p.FilterEnvOct = Math.Round(envOct * 2) / 2.0;
                }
                if (p.Filter == FilterMode.LowPass && p.Cutoff >= 12000 && Math.Abs(p.FilterEnvOct) < 0.4)
                {
                    p.Filter = FilterMode.Off; p.Cutoff = 20000;   // nothing audible to filter
                }
                r.M("Filter", "Cutoff (settled part)", lateOpen ? "open" : Dsp.Hz(cutLate), lateOpen ? null : fl.Slope + " dB/oct, resonance " + (fl.Res * 100).ToString("0") + " %");
                r.M("Filter", "Cutoff (attack)", earlyOpen ? "open" : Dsp.Hz(cutEarly), null);

                // residuals -> high-pass or extra fundamental
                if (fl.Residual != null && fl.Residual.Length >= 3 && !subMode && hLate[0].Present)
                {
                    if (fl.Residual[0] > 6 && !r.IsChord)
                    {
                        p.SubLevel = Dsp.Clamp(Dsp.FromDb(fl.Residual[0]) - 1, 0.1, 1.5);
                        p.SubOctave = 0;
                        r.M("Timbre", "Fundamental boost", "+" + fl.Residual[0].ToString("0") + " dB", "add a sine layer at the same pitch");
                    }
                    int hpK = 0;
                    for (int k = 0; k < Math.Min(4, fl.Residual.Length); k++) { if (fl.Residual[k] < -6) hpK = k + 1; else break; }
                    if (hpK > 0)
                    {
                        p.HighPassHz = fitF0 * (hpK + 0.5);
                        r.M("Filter", "Low-end cut", "high-pass ~" + Dsp.Hz(p.HighPassHz), "lowest " + hpK + " harmonic(s) weaker than expected");
                    }
                }

                // filter envelope timing from brightness track (meaningless for a pure sine)
                if (p.Osc1Wave != Wave.Sine) FilterEnvelopeTiming(r, x, sr, noteT0, atkEnd / ER, endI / ER, fitF0);
                else { p.Filter = FilterMode.Off; p.FilterEnvOct = 0; p.SubLevel = p.SubOctave < 0 ? p.SubLevel : 0; }
                if (p.CutoffLfoRate > 0)
                {
                    // the LFO explains the brightness differences; the level wobble is a side effect of the filter
                    p.FilterEnvOct = 0;
                    if (p.TremoloRate > 0) { p.TremoloRate = 0; p.TremoloDepth = 0; }
                }

                // ---------------- noise layer
                if (noiseFrac > 0.12 && r.VoicedRatio > 0.35)
                {
                    p.NoiseLevel = Dsp.Clamp(Math.Sqrt(noiseFrac / Math.Max(0.05, 1 - noiseFrac)) * 0.5, 0.05, 1);
                }
                if (flatAtk > 0.2 && flatAtk > 2.5 * flatLate)
                {
                    p.NoiseLevel = Math.Max(p.NoiseLevel, 0.3);
                    p.NoiseDecay = NoiseBurstLength(x, sr, noteT0, flatLate);
                    r.M("Timbre", "Noisy attack transient", Dsp.Ms(p.NoiseDecay), "flatness " + flatAtk.ToString("0.00") + " vs " + flatLate.ToString("0.00"));
                }

                // ---------------- unison / detune
                double sigma = UnisonSpread(P, binHz, hLate, fitF0, floorDb, pmax);
                double vibSig = vibCents / Math.Sqrt(2);
                double uni = Math.Sqrt(Math.Max(0, sigma * sigma - vibSig * vibSig));
                r.UnisonCents = uni;
                double uniThresh = sustained ? 4.0 : 7.0;
                r.M("Timbre", "Harmonic peak spread", uni.ToString("0.0") + " cents", uni > uniThresh ? "detuned voices / unison" : "single, stable oscillator");
                if (p.CutoffLfoRate > 0 && p.CutoffLfoOct < 0.6 && uni > 8)
                {
                    // a shallow, 'LFO-like' brightness movement plus detuned harmonics = unison beating
                    p.CutoffLfoRate = 0; p.CutoffLfoOct = 0;
                    r.Measurements.RemoveAll(m => m.Name.StartsWith("Filter wobble"));
                }
                if (p.CutoffLfoRate > 0 || r.IsChord) uniThresh = Math.Max(uniThresh, 15);   // AM sidebands / chord partials look like detune
                if (p.VibratoRate > 0) uniThresh = Math.Max(uniThresh, 8 + vibCents * 0.3);
                if (uni > uniThresh && !r.Inharmonic)
                {
                    if (uni < 12) { p.UnisonVoices = 2; p.UnisonDetuneCents = Math.Round(uni); }
                    else if (uni < 25) { p.UnisonVoices = 4; p.UnisonDetuneCents = Math.Round(uni * 1.4); }
                    else { p.UnisonVoices = 7; p.UnisonDetuneCents = Math.Round(Math.Min(100, uni * 1.6)); }
                }
            }

            // ---------------- stereo
            if (st != null)
            {
                int a = (int)(steadyA * sr), b = Math.Min(st[0].Length, (int)(Math.Max(steadyB, steadyA + 0.1) * sr));
                double corr = Correlation(st[0], st[1], a, b);
                r.StereoCorr = corr;
                r.M("Stereo", "L/R correlation", corr.ToString("0.00"), corr > 0.95 ? "essentially mono" : (corr > 0.7 ? "slightly wide" : "wide"));
                if (corr < 0.9)
                {
                    p.StereoWidth = Dsp.Clamp((1 - corr) * 1.5, 0.1, 1);
                    if (p.UnisonVoices <= 1) p.ChorusMix = Dsp.Clamp((1 - corr) * 0.8, 0.15, 0.6);
                }
            }

            // ---------------- clipping / drive
            if (clipFrac > 0.01 && p.Osc1Wave != Wave.Square && p.Osc1Wave != Wave.Pulse)
            {
                p.Drive = Dsp.Clamp(clipFrac * 10, 0.2, 0.8);
                r.M("Effects", "Clipped / saturated peaks", (clipFrac * 100).ToString("0.0") + " % of samples", "suggests distortion or a limiter");
            }

            // ---------------- reverb tail
            if (!hasDelay && !truncated && reachedSilence)
                DetectReverb(r, sdb, relStartI, ER, st, sr, noteT0);
            if (hasDelay)
                r.M("Effects", "Echo repeats", Dsp.Ms(p.DelayTime) + ", feedback " + (p.DelayFeedback * 100).ToString("0") + " %", "or repeated notes getting quieter");

            r.ReleaseKnown = releaseKnown;
            r.Notes2.Add(releaseKnown ? "" : "Release could not be measured because the note is cut off; set it by ear.");
            r.Notes2.RemoveAll(s => string.IsNullOrEmpty(s));
            _gate = gateTime;
            _sustained = sustained;
        }

        [ThreadStatic] static double _gate;
        [ThreadStatic] static bool _sustained;

        // ---------------------------------------------------------------------
        static double NormBound(List<Harmonic> h, double floorDb, double pmax)
        {
            // floorDb is relative to the spectrum max; harmonic Db values are relative to strongest harmonic
            double hmax = 0; // strongest harmonic == spectrum max in practice
            return floorDb - hmax + 10;
        }

        static int endI0(double[] sdb, int atkEnd)
        {
            int e = sdb.Length - 1;
            while (e > atkEnd && sdb[e] < -60) e--;
            return e;
        }

        static double TimeTo60(double[] sdb, int fromI, double refDb, double rate)
        {
            // time to fall 30 dB below refDb, doubled (assumes exponential decay); extrapolates if needed
            for (int f = fromI; f < sdb.Length; f++)
                if (sdb[f] <= refDb - 30) return Math.Max(0.005, 2.0 * (f - fromI) / rate);
            // extrapolate from slope over the available tail
            List<double> xs = new List<double>(), ys = new List<double>();
            for (int f = fromI; f < sdb.Length; f++) { xs.Add((f - fromI) / rate); ys.Add(sdb[f]); }
            double icpt;
            double s = Dsp.LinearSlope(xs, ys, out icpt);
            if (s < -1) return Dsp.Clamp(-60.0 / s, 0.01, 30);
            return Math.Max(1.0, xs.Count > 0 ? xs[xs.Count - 1] * 2 : 1);
        }

        static double[] Smooth(double[] v, int w)
        {
            double[] o = new double[v.Length];
            double s = 0; int cnt = 0;
            int h = w / 2;
            for (int i = 0; i < v.Length + h; i++)
            {
                if (i < v.Length) { s += v[i]; cnt++; }
                if (i - w >= 0) { s -= v[i - w]; cnt--; }
                int c = i - h;
                if (c >= 0 && c < v.Length) o[c] = s / cnt;
            }
            return o;
        }

        static double[] LocalSlope(double[] v, int w, double rate)
        {
            double[] o = new double[v.Length];
            int h = w / 2;
            for (int i = 0; i < v.Length; i++)
            {
                int a = Math.Max(0, i - h), b = Math.Min(v.Length - 1, i + h);
                o[i] = b > a ? (v[b] - v[a]) / ((b - a) / rate) : 0;
            }
            return o;
        }

        static List<double> Detrend(IList<double> s)
        {
            double[] xs = Enumerable.Range(0, s.Count).Select(i => (double)i).ToArray();
            double icpt; double b = Dsp.LinearSlope(xs, s, out icpt);
            return s.Select((v, i) => v - (icpt + b * i)).ToList();
        }

        static double[] AverageSpectrum(float[] x, int sr, double tA, double tB, int N)
        {
            int a = (int)(tA * sr), b = (int)(tB * sr);
            int last = Math.Max(a, b - N);
            int frames = Math.Max(1, Math.Min(40, (last - a) / (N / 4) + 1));
            double[] acc = new double[N / 2 + 1];
            for (int i = 0; i < frames; i++)
            {
                int s = frames == 1 ? a : a + (int)((long)(last - a) * i / (frames - 1));
                double[] p = Dsp.PowerSpectrum(x, s, N);
                for (int k = 0; k < acc.Length; k++) acc[k] += p[k];
            }
            for (int k = 0; k < acc.Length; k++) acc[k] = acc[k] / frames + 1e-20;
            return acc;
        }

        static List<Harmonic> ExtractHarmonics(double[] P, double binHz, double f0, int sr, double floorDb, double pmax)
        {
            List<Harmonic> hs = new List<Harmonic>();
            double lim = Math.Min(0.45 * sr, 18000);
            double best = -300;
            for (int k = 1; k <= 64 && k * f0 < lim; k++)
            {
                double fc = k * f0;
                double sw = Math.Max(2 * binHz, Math.Min(0.2 * f0, fc * 0.03));
                int a = Math.Max(1, (int)((fc - sw) / binHz)), b = Math.Min(P.Length - 2, (int)Math.Ceiling((fc + sw) / binHz));
                int mi = a; for (int i = a; i <= b; i++) if (P[i] > P[mi]) mi = i;
                double ya = Dsp.PowToDb(P[mi - 1]), yb = Dsp.PowToDb(P[mi]), yc = Dsp.PowToDb(P[mi + 1]);
                double den = ya - 2 * yb + yc;
                double off = Math.Abs(den) > 1e-9 ? Dsp.Clamp(0.5 * (ya - yc) / den, -0.5, 0.5) : 0;
                double pk = yb - 0.25 * (ya - yc) * off;
                Harmonic h = new Harmonic();
                h.K = k; h.Freq = (mi + off) * binHz;
                bool present = pk - Dsp.PowToDb(pmax) > floorDb + 10 && Math.Abs(h.Freq - fc) < Math.Max(1.5 * binHz, fc * 0.02);
                // level = energy in a band around the peak (robust against vibrato / detune smearing)
                double hw = Math.Max(2 * binHz, Math.Min(0.3 * f0, fc * 0.03));
                int ba = Math.Max(1, (int)Math.Round((h.Freq - hw) / binHz)), bb = Math.Min(P.Length - 1, (int)Math.Round((h.Freq + hw) / binHz));
                double e = 0; for (int i = ba; i <= bb; i++) e += P[i];
                h.Db = present ? Dsp.PowToDb(e / 1.5) - Dsp.PowToDb(pmax) : pk - Dsp.PowToDb(pmax);   // 1.5 = Hann power sum per bin
                h.Present = present;
                h.PeakDb = pk - Dsp.PowToDb(pmax);
                hs.Add(h);
                if (h.Db > best) best = h.Db;
            }
            foreach (Harmonic h in hs) h.Db -= best;   // relative to strongest harmonic
            return hs;
        }

        static List<Harmonic> ExtractHarmonicsWithFloor(double[] P, double binHz, double f0, int sr, out double floorRel)
        {
            double pmax = P.Max();
            int top = Math.Min(P.Length - 1, (int)(Math.Min(16000, 0.45 * sr) / binHz));
            List<double> dbs = new List<double>();
            for (int b = (int)(40 / binHz) + 1; b < top; b++) dbs.Add(Dsp.PowToDb(P[b] / pmax));
            double floorDb = Dsp.Percentile(dbs, 0.35);
            List<Harmonic> h = ExtractHarmonics(P, binHz, f0, sr, floorDb, pmax);
            floorRel = floorDb;
            return h;
        }

        static double RefineF0(List<Harmonic> h, double f0)
        {
            double num = 0, den = 0;
            foreach (Harmonic x in h)
            {
                if (!x.Present || x.K > 12) continue;
                double w = Dsp.FromDb(x.Db);
                num += x.Freq * x.K * w; den += x.K * x.K * w;
            }
            if (den <= 0) return f0;
            double r = num / den;
            return Math.Abs(r / f0 - 1) < 0.03 ? r : f0;
        }

        static double BandEnergy(double[] P, double binHz, IEnumerable<double> freqs, double f0, bool wide)
        {
            double e = 0;
            HashSet<int> used = new HashSet<int>();
            foreach (double f in freqs)
            {
                double hw = wide ? Math.Max(3 * binHz, Math.Min(0.3 * f0, f * 0.025)) : 3 * binHz;
                int a = Math.Max(1, (int)((f - hw) / binHz)), b = Math.Min(P.Length - 1, (int)((f + hw) / binHz));
                for (int i = a; i <= b; i++) if (used.Add(i)) e += P[i];
            }
            return e;
        }

        static List<int> FindPeaks(double[] P, double binHz, double floorDb, double pmax, int topBin)
        {
            List<int> pk = new List<int>();
            double thr = pmax * Math.Pow(10, (floorDb + 15) / 10.0);
            double[] local = LocalFloor(P, binHz, topBin);
            for (int b = Math.Max(2, (int)(30 / binHz)); b < topBin - 2; b++)
                if (P[b] > thr && P[b] > local[b] * 30 && P[b] >= P[b - 1] && P[b] >= P[b + 1] && P[b] >= P[b - 2] && P[b] >= P[b + 2]) pk.Add(b);
            pk.Sort((a, b) => P[b].CompareTo(P[a]));
            if (pk.Count > 80) pk.RemoveRange(80, pk.Count - 80);
            return pk;
        }

        /// <summary>Running lower-percentile of the spectrum over roughly +/- 1/6 octave (noise floor per region).</summary>
        static double[] LocalFloor(double[] P, double binHz, int topBin)
        {
            double[] fl = new double[P.Length];
            int step = 8;
            List<double> tmp = new List<double>();
            double[] anchors = new double[P.Length / step + 2];
            for (int ai = 0; ai * step < P.Length; ai++)
            {
                int c = ai * step;
                int hw = Math.Max(24, (int)(c * 0.12));
                int a = Math.Max(1, c - hw), b = Math.Min(P.Length - 1, c + hw);
                tmp.Clear();
                int stride = Math.Max(1, (b - a) / 200);
                for (int i = a; i <= b; i += stride) tmp.Add(P[i]);
                anchors[ai] = Dsp.Percentile(tmp, 0.3);
            }
            for (int i = 0; i < P.Length; i++)
            {
                int ai = i / step; double f = (i % step) / (double)step;
                double v0 = anchors[ai], v1 = ai + 1 < anchors.Length ? anchors[ai + 1] : v0;
                fl[i] = v0 * (1 - f) + v1 * f;
            }
            return fl;
        }

        static double PeakEnergy(double[] P, List<int> peaks, double binHz, double f0)
        {
            return BandEnergy(P, binHz, peaks.Select(b => b * binHz), f0 > 0 ? f0 : 200, f0 > 0);
        }

        static double Flatness(double[] P, double binHz, int sr)
        {
            int a = Math.Max(1, (int)(100 / binHz)), b = Math.Min(P.Length - 1, (int)(Math.Min(16000, 0.45 * sr) / binHz));
            double lg = 0, ar = 0; int n = 0;
            for (int i = a; i < b; i++) { lg += Math.Log(P[i] + 1e-20); ar += P[i]; n++; }
            if (n == 0 || ar <= 0) return 0;
            return Math.Exp(lg / n) / (ar / n);
        }

        static double NoiseBurstLength(float[] x, int sr, double t0, double flatLate)
        {
            int N = Dsp.NextPow2(sr / 100);
            double binHz = sr / (double)N;
            double first = -1;
            for (int i = 0; i < 60; i++)
            {
                double t = t0 + i * 0.004;
                double fl = Flatness(Dsp.PowerSpectrum(x, (int)(t * sr), N), binHz, sr);
                if (first < 0) first = fl;
                if (fl < (first + flatLate) * 0.5) return Math.Max(0.005, i * 0.004);
            }
            return 0.2;
        }

        static double Correlation(float[] a, float[] b, int s, int e)
        {
            double ab = 0, aa = 0, bb = 0;
            for (int i = Math.Max(0, s); i < Math.Min(e, a.Length); i++) { ab += a[i] * b[i]; aa += a[i] * a[i]; bb += b[i] * b[i]; }
            return (aa > 0 && bb > 0) ? ab / Math.Sqrt(aa * bb) : 1;
        }

        static bool DetectSubOsc(List<Harmonic> h)
        {
            if (h.Count < 9 || !h[0].Present || !h[1].Present) return false;
            double odd = 0, even = 0;
            for (int k = 3; k <= 9; k += 2) odd += Dsp.FromDb(h[k - 1].Db);
            for (int k = 2; k <= 8; k += 2) even += Dsp.FromDb(h[k - 1].Db);
            // even harmonics dominate strongly while the fundamental is still present
            // octave layering: even harmonics clearly stronger than odd ones, and a strong fundamental
            return Dsp.ToDb(even / Math.Max(1e-9, odd)) > 9 && h[0].Db > h[1].Db - 6 && h[3].Present && h[5].Present;
        }

        // ---------------------------------------------------------------------
        static double UnisonSpread(double[] P, double binHz, List<Harmonic> h, double f0, double floorDb, double pmax)
        {
            List<double> est = new List<double>();
            double floorLin = pmax * Math.Pow(10, floorDb / 10.0);
            foreach (Harmonic x in h)
            {
                if (!x.Present || x.K < 2 || x.Db < -35) continue;
                double hwHz = Math.Min(0.3 * f0, x.Freq * 0.035);
                double hwb = hwHz / binHz;
                if (hwb < 4) continue;
                double c = x.Freq / binHz;
                int a = (int)Math.Floor(c - hwb), b = (int)Math.Ceiling(c + hwb);
                if (a < 1 || b >= P.Length) continue;
                double sp = 0, mu = 0;
                for (int i = a; i <= b; i++) { sp += P[i]; mu += P[i] * i; }
                if (sp <= 0) continue;
                mu /= sp;
                double v = 0;
                for (int i = a; i <= b; i++) v += P[i] * (i - mu) * (i - mu);
                v /= sp;
                double noiseP = floorLin * (b - a + 1);
                if (sp < noiseP * 30) continue;   // too noisy
                double halfW = (b - a) / 2.0;
                double vpk = (v * sp - noiseP * halfW * halfW / 3.0) / (sp - noiseP);
                double v0 = HannVariance(halfW);
                double sig = Math.Sqrt(Math.Max(0, vpk - v0)) * binHz;
                est.Add(1731.0 * sig / x.Freq);
                if (est.Count >= 16) break;
            }
            if (est.Count < 3) return 0;
            // detune spreads harmonics by a constant number of cents; amplitude modulation (tremolo,
            // wobble) by a constant number of Hz - i.e. the cents value falls with harmonic number.
            int half = est.Count / 2;
            double lo = Dsp.Median(est.Take(half).ToList()), hi = Dsp.Median(est.Skip(half).ToList());
            if (lo > 3 && hi < lo * 0.45) return Math.Min(hi, 3.0);
            return Dsp.Median(est);
        }

        static double HannVariance(double halfW)
        {
            double s = 0, sw = 0;
            foreach (double off in new double[] { 0.0, 0.25, 0.5 })
            {
                for (double j = -Math.Floor(halfW); j <= Math.Floor(halfW); j += 1)
                {
                    double d = j - off;
                    double k = HannKernel(d); k *= k;
                    s += k * d * d; sw += k;
                }
            }
            return sw > 0 ? s / sw : 0;
        }

        static double Sinc(double x) { return Math.Abs(x) < 1e-9 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x); }
        static double HannKernel(double d) { return 0.5 * Sinc(d) + 0.25 * Sinc(d - 1) + 0.25 * Sinc(d + 1); }

        // ---------------------------------------------------------------------
        static void FilterEnvelopeTiming(AnalysisResult r, float[] x, int sr, double t0, double atkEnd, double endT, double f0)
        {
            SynthPatch p = r.Patch;
            int N = Dsp.NextPow2((int)(sr * 0.03));
            double binHz = sr / (double)N;
            List<double> ts = new List<double>(), lc = new List<double>();
            double loudest = 0;
            List<double> lev = new List<double>();
            int[] bandK = new[] { 2, 3, 4, 6, 8, 12, 16, 24, 32 }.Where(k => k * f0 > 4 * binHz && k * f0 < 6000).ToArray();
            List<double>[] band = bandK.Select(k => new List<double>()).ToArray();
            for (double t = t0; t < t0 + endT; t += 0.01)
            {
                double[] P = Dsp.PowerSpectrum(x, (int)(t * sr) - N / 2, N);
                for (int bi = 0; bi < bandK.Length; bi++)
                {
                    int c = (int)Math.Round(bandK[bi] * f0 / binHz);
                    band[bi].Add(Dsp.PowToDb(P[Math.Max(1, c - 1)] + P[c] + P[Math.Min(P.Length - 1, c + 1)]));
                }
                double num = 0, den = 0;
                for (int b = Math.Max(1, (int)(30 / binHz)); b < Math.Min(P.Length, (int)(16000 / binHz)); b++) { double m = Math.Sqrt(P[b]); num += m * b * binHz; den += m; }
                lev.Add(den);
                loudest = Math.Max(loudest, den);
                ts.Add(t - t0);
                lc.Add(den > 0 ? Math.Log(num / den, 2) : 0);
            }
            List<double> T = new List<double>(), C = new List<double>();
            for (int i = 0; i < lc.Count; i++) if (lev[i] > loudest * 0.01 && ts[i] >= atkEnd - 0.005) { T.Add(ts[i]); C.Add(lc[i]); }
            if (C.Count < 5) return;
            double cStart = Dsp.Mean(C.Take(3).ToList());
            int tail = Math.Max(3, C.Count / 4);
            double cEnd = Dsp.Mean(C.Skip(C.Count - tail).ToList());
            double delta = cStart - cEnd;
            r.M("Filter", "Brightness change", (delta >= 0 ? "darker by " : "brighter by ") + Math.Abs(delta).ToString("0.00") + " oct", "spectral centroid, attack vs. end of note");

            double fitEnv = p.FilterEnvOct;
            if (delta > 0.25 && fitEnv < -0.3) delta = 0;
            if (delta < -0.25 && fitEnv > 0.3) delta = 0;
            if (delta > 0.25)
            {
                if (p.Filter == FilterMode.Off) { p.Filter = FilterMode.LowPass; p.Cutoff = Math.Max(f0 * 2, 2000); }
                if (p.FilterEnvOct < 0.5) p.FilterEnvOct = Math.Round(Math.Max(1.0, delta * 2.2) * 2) / 2.0;
                double target = cEnd + 0.1 * delta;
                double td = T[T.Count - 1] - atkEnd;
                for (int i = 0; i < C.Count; i++) if (C[i] <= target) { td = T[i] - atkEnd; break; }
                p.FA = Math.Min(p.AA, 0.005);
                p.FD = Math.Max(0.02, td);
                p.FS = 0;
                p.FR = p.AR;
                r.M("Filter", "Filter envelope decay", Dsp.Ms(p.FD), "brightness settles after this time");
            }
            else if (delta < -0.35)
            {
                // swell: gets brighter over time
                double target = cStart - 0.9 * delta;   // 90 % of the rise
                double ta = T[T.Count - 1];
                for (int i = 0; i < C.Count; i++) if (C[i] >= target) { ta = T[i]; break; }
                if (p.Filter == FilterMode.Off) { p.Filter = FilterMode.LowPass; p.Cutoff = Math.Max(f0 * 2, 1500); }
                double oct = Math.Round(Math.Max(1.0, -delta * 2.2) * 2) / 2.0;
                p.Cutoff = Math.Max(f0, p.Cutoff / Math.Pow(2, oct));
                p.FilterEnvOct = oct;
                p.FA = Math.Max(0.02, ta);
                p.FD = 0.3; p.FS = 1.0; p.FR = p.AR;
                r.M("Filter", "Filter opens over", Dsp.Ms(p.FA), "sound gets brighter after the attack");
            }
            else if (Math.Abs(p.FilterEnvOct) > 0)
            {
                p.FA = 0.001; p.FD = Math.Max(0.05, p.AD * 0.5); p.FS = 0; p.FR = p.AR;
            }

            // periodic brightness modulation -> LFO on cutoff
            if (C.Count >= 60)
            {
                double str;
                List<double> body = C.Skip(10).ToList();
                double rate = Dsp.DominantModRate(body, 100, 0.3, 12, out str);
                List<double> dt = Detrend(body);
                double pp = Dsp.Percentile(dt, 0.95) - Dsp.Percentile(dt, 0.05);
                // An LFO on the cutoff moves every harmonic at the same rate. Detuned unison voices make each
                // harmonic 'beat' at a rate that grows with the harmonic number - not an LFO.
                bool beating = false;
                if (rate > 0 && str > 0.65 && pp > 0.3)
                {
                    List<double> rates = new List<double>();
                    int skip = (int)Math.Round((atkEnd) * 100) + 10;
                    for (int bi = 0; bi < bandK.Length; bi++)
                    {
                        List<double> bb = band[bi].Skip(skip).ToList();
                        if (bb.Count < 60) continue;
                        double bs; double br = Dsp.DominantModRate(bb, 100, 0.3, 12, out bs);
                        if (br > 0 && bs > 0.3) rates.Add(br);
                    }
                    if (rates.Count >= 3)
                    {
                        double lo = Dsp.Percentile(rates, 0.2), hi = Dsp.Percentile(rates, 0.8);
                        if (hi / lo > 1.7)
                        {
                            beating = true;
                            r.M("Modulation", "Brightness pulsing", rate.ToString("0.0") + " Hz",
                                "rates differ per harmonic (" + lo.ToString("0.0") + "-" + hi.ToString("0.0") + " Hz) = unison beating, not an LFO");
                        }
                    }
                }
                if (!beating && rate > 0 && str > 0.65 && pp > 0.3 && body.Count / 100.0 * rate >= 2)
                {
                    p.CutoffLfoRate = rate;
                    p.CutoffLfoOct = Math.Round(pp * 0.75 * 10) / 10.0;
                    // the averaged spectrum is dominated by the bright phase of the sweep: centre the cutoff
                    if (p.Filter == FilterMode.LowPass) p.Cutoff = Math.Max(f0, p.Cutoff / Math.Pow(2, 0.6 * p.CutoffLfoOct));
                    if (p.Filter == FilterMode.Off) { p.Filter = FilterMode.LowPass; p.Cutoff = Math.Max(f0 * 3, 1000); }
                    r.M("Modulation", "Filter wobble (LFO)", rate.ToString("0.00") + " Hz, " + pp.ToString("0.0") + " oct p-p", "periodic brightness change");
                }
            }
        }

        // ---------------------------------------------------------------------
        static void DetectReverb(AnalysisResult r, double[] sdb, int fromI, double rate, float[][] st, int sr, double t0)
        {
            List<double> xs = new List<double>(), ys = new List<double>();
            for (int f = fromI; f < sdb.Length; f++)
            {
                if (sdb[f] < -75) break;
                xs.Add((f - fromI) / rate); ys.Add(sdb[f]);
            }
            if (xs.Count < 0.3 * rate) return;
            // best two-segment fit
            double bestErr = double.MaxValue; int bestK = -1; double s1 = 0, s2 = 0, i1 = 0, i2 = 0;
            for (int k = (int)(0.03 * rate); k < xs.Count - (int)(0.15 * rate); k += 2)
            {
                double a1, a2;
                double b1 = Dsp.LinearSlope(xs.GetRange(0, k), ys.GetRange(0, k), out a1);
                double b2 = Dsp.LinearSlope(xs.GetRange(k, xs.Count - k), ys.GetRange(k, xs.Count - k), out a2);
                double e = 0;
                for (int i = 0; i < xs.Count; i++) { double pr = i < k ? a1 + b1 * xs[i] : a2 + b2 * xs[i]; e += (ys[i] - pr) * (ys[i] - pr); }
                if (e < bestErr) { bestErr = e; bestK = k; s1 = b1; s2 = b2; i1 = a1; i2 = a2; }
            }
            if (bestK < 0) return;
            double kneeT = xs[bestK], kneeDb = i1 + s1 * kneeT;
            double tailLen = xs[xs.Count - 1] - kneeT;
            if (s1 < -20 && s2 < -3 && s2 / s1 < 0.4 && kneeDb < -10 && kneeDb > -50 && tailLen > 0.25)
            {
                SynthPatch p = r.Patch;
                p.ReverbTime = Dsp.Clamp(-60.0 / s2, 0.3, 12);
                p.ReverbMix = Dsp.Clamp(Dsp.FromDb(kneeDb) * 2.5, 0.08, 0.6);
                // the amp envelope should only describe the dry part
                double dry60 = Dsp.Clamp(-60.0 / s1, 0.01, 20);
                if (p.AS <= 0.001) { p.AD = dry60; p.AR = dry60 * 0.8; }
                else p.AR = dry60;
                string extra = "";
                if (st != null)
                {
                    int a = (int)((t0 + (fromI / rate) + kneeT) * sr);
                    double corr = Correlation(st[0], st[1], a, a + (int)(Math.Min(tailLen, 1.0) * sr));
                    extra = ", tail L/R correlation " + corr.ToString("0.00");
                }
                r.M("Effects", "Reverb tail", "RT60 ~" + p.ReverbTime.ToString("0.0") + " s", "knee at " + kneeDb.ToString("0") + " dB" + extra);
                // stereo width / detune measured over the tail are probably the reverb's doing
                if (st != null)
                {
                    int a = (int)(t0 * sr), b = a + (int)(Math.Min(0.08, kneeT + fromI / rate) * sr) + sr / 100;
                    double early = Correlation(st[0], st[1], a, b);
                    if (early > 0.9) { p.StereoWidth = 0; p.ChorusMix = 0; }
                }
                if (p.UnisonVoices > 1 && p.UnisonDetuneCents < 20)
                {
                    p.UnisonVoices = 1; p.UnisonDetuneCents = 0;
                    r.Notes2.Add("Harmonics look slightly smeared, but that is probably the reverb tail - try without detune first.");
                }
            }
        }

        // ---------------------------------------------------------------------
        static void AnalyzeNoiseSound(AnalysisResult r, double[] P, double binHz, int sr)
        {
            SynthPatch p = r.Patch;
            p.Osc1Wave = Wave.Noise;
            p.NoiseLevel = 0;
            // third-octave bands
            List<double> fc = new List<double>(), e = new List<double>();
            for (double f = 31.5; f < Math.Min(16000, sr * 0.45); f *= Math.Pow(2, 1 / 3.0))
            {
                double lo = f / Math.Pow(2, 1 / 6.0), hi = f * Math.Pow(2, 1 / 6.0);
                double s = 0; int n = 0;
                for (int b = Math.Max(1, (int)(lo / binHz)); b <= Math.Min(P.Length - 1, (int)(hi / binHz)); b++) { s += P[b]; n++; }
                fc.Add(f); e.Add(n > 0 ? Dsp.PowToDb(s / n) : -200);
            }
            double mx = e.Max();
            int lo6 = e.FindIndex(v => v > mx - 6), hi6 = e.FindLastIndex(v => v > mx - 6);
            double fLo = fc[lo6], fHi = fc[hi6];
            double width = Math.Log(fHi / fLo, 2);
            bool hasLow = lo6 > 1, hasHigh = hi6 < fc.Count - 2;
            if (hasLow && hasHigh && width < 2.0)
            {
                p.Filter = FilterMode.BandPass; p.Cutoff = Math.Sqrt(fLo * fHi); p.Resonance = Dsp.Clamp(1.0 - width / 2.0, 0.1, 0.8); p.FilterSlope = 12;
            }
            else if (hasHigh)
            {
                p.Filter = FilterMode.LowPass; p.Cutoff = fHi; p.FilterSlope = 12;
                if (hasLow && fLo > 150) p.HighPassHz = fLo;
            }
            else if (hasLow)
            {
                p.Filter = FilterMode.HighPass; p.Cutoff = fLo; p.FilterSlope = 12;
            }
            r.M("Filter", "Noise band (-6 dB)", Dsp.Hz(fLo) + " - " + Dsp.Hz(fHi), width.ToString("0.0") + " octaves wide");
        }

        // ---------------------------------------------------------------------
        static List<double> DetectChord(double[] P, double binHz, List<int> peaks, out double explained)
        {
            explained = 0;
            List<double> pf = new List<double>(), pp = new List<double>();
            foreach (int b in peaks) { double f = b * binHz; if (f >= 50 && f <= 5000) { pf.Add(f); pp.Add(P[b]); } }
            double totalE = pp.Sum();
            if (totalE <= 0 || pf.Count < 3) return new List<double>();
            bool[] used = new bool[pf.Count];
            List<double> notes = new List<double>();
            double first = 0;
            for (int iter = 0; iter < 5; iter++)
            {
                double bestS = 0; int bestM = -1;
                for (int m = 28; m <= 96; m++)
                {
                    double f = Dsp.MidiToFreq(m), s = 0;
                    bool hasFund = false; int nh = 0;
                    for (int h = 1; h <= 8; h++)
                    {
                        int j = NearPeak(pf, used, f * h, 35);
                        if (j >= 0) { s += Math.Sqrt(pp[j]) * Math.Pow(0.85, h - 1); nh++; if (h == 1) hasFund = true; }
                    }
                    if (!hasFund || nh < 2) continue;
                    if (s > bestS) { bestS = s; bestM = m; }
                }
                if (bestM < 0) break;
                if (iter == 0) first = bestS;
                else if (bestS < first * 0.2) break;
                double fr = Dsp.MidiToFreq(bestM);
                int fj = NearPeak(pf, used, fr, 35);
                double exact = fj >= 0 ? pf[fj] : fr;
                notes.Add(exact);
                for (int h = 1; h <= 12; h++)
                {
                    for (int j = 0; j < pf.Count; j++)
                        if (!used[j] && Math.Abs(1200 * Math.Log(pf[j] / (exact * h), 2)) < 35) { used[j] = true; explained += pp[j]; }
                }
            }
            explained /= totalE;
            return notes;
        }

        static int NearPeak(List<double> pf, bool[] used, double f, double cents)
        {
            int best = -1; double bd = cents;
            for (int j = 0; j < pf.Count; j++)
            {
                if (used[j]) continue;
                double d = Math.Abs(1200 * Math.Log(pf[j] / f, 2));
                if (d < bd) { bd = d; best = j; }
            }
            return best;
        }

        static bool ZeroCrossPitchEnv(float[] y, int sr, double f0, out double semis, out double time)
        {
            semis = 0; time = 0;
            // rising zero crossings (with a little hysteresis) during the first 250 ms
            int lim = Math.Min(y.Length, (int)(0.25 * sr));
            double pk = 0; for (int i = 0; i < lim; i++) pk = Math.Max(pk, Math.Abs(y[i]));
            if (pk <= 0) return false;
            // all zero crossings (both directions, with hysteresis) -> half-period resolution
            List<double> zc = new List<double>();
            int sign = 0; bool armed = false;
            for (int i = 1; i < lim; i++)
            {
                if (Math.Abs(y[i]) > 0.05 * pk) armed = true;
                int sg = y[i] >= 0 ? 1 : -1;
                if (sign != 0 && sg != sign && armed)
                {
                    double fr = y[i - 1] / (double)(y[i - 1] - y[i]);
                    zc.Add((i - 1 + fr) / sr);
                    armed = false;
                }
                sign = sg;
            }
            if (zc.Count < 4) return false;
            List<double> fq = new List<double>(), tm = new List<double>();
            for (int i = 1; i < zc.Count; i++) { fq.Add(0.5 / (zc[i] - zc[i - 1])); tm.Add(zc[i]); }
            // the waveform must settle at f0 (otherwise zero crossings are not one per period)
            double endF = Dsp.Median(fq.Skip(fq.Count / 2).ToList());
            if (Math.Abs(12 * Math.Log(endF / f0, 2)) > 1.0) return false;
            double first = fq[0];
            double s = 12 * Math.Log(first / f0, 2);
            if (s < 2) return false;
            // falls monotonically?
            int drops = 0; for (int i = 1; i < Math.Min(fq.Count, 8); i++) if (fq[i] <= fq[i - 1] * 1.08) drops++;
            if (drops < Math.Min(fq.Count, 8) - 3) return false;
            semis = s * 1.25;   // first period is an average over the fastest part of the drop
            time = tm[tm.Count - 1];
            for (int i = 0; i < fq.Count; i++) if (12 * Math.Log(fq[i] / f0, 2) < 1.0) { time = tm[i]; break; }
            return true;
        }

        static double FmCarrier(double[] P, List<int> peaks, double binHz)
        {
            return peaks.Count > 0 ? peaks[0] * binHz : 0;
        }

        static string FmGuess(double[] P, List<int> peaks, double binHz)
        {
            List<int> top = peaks.Take(10).ToList();
            if (top.Count < 3) return null;
            double c = top[0] * binHz;   // strongest partial = carrier
            List<double> pf = top.Select(b => b * binHz).ToList();
            double bestM = 0; int bestN = 0;
            foreach (double q in pf)
            {
                foreach (double m in new[] { Math.Abs(q - c), q + c })
                {
                    if (m < 20) continue;
                    int cnt = 0;
                    foreach (double f in pf)
                        for (int k = -5; k <= 5; k++)
                            if (Math.Abs(Math.Abs(c + k * m) - f) < f * 0.015) { cnt++; break; }
                    if (cnt > bestN || (cnt == bestN && m < bestM)) { bestN = cnt; bestM = m; }
                }
            }
            if (bestN < 3) return null;
            return "carrier " + Dsp.Hz(c) + " (" + Dsp.NoteName(c) + "), modulator ratio ~" + (bestM / c).ToString("0.00") + " : 1";
        }

        static string PartialRatios(double[] P, List<int> peaks, double binHz)
        {
            List<int> top = peaks.Take(8).Where(b => b * binHz > 30).OrderBy(b => b).ToList();
            if (top.Count == 0) return "-";
            double strongest = top.Max(b => P[b]);
            // base = lowest partial that is within 20 dB of the strongest
            int bb = top.First(b => P[b] > strongest * 0.01);
            return string.Join(" : ", top.Where(b => b >= bb).Take(6).Select(b => (b / (double)bb).ToString("0.00")).ToArray());
        }

        static double PartialBase(double[] P, List<int> peaks, double binHz)
        {
            List<int> top = peaks.Take(8).OrderBy(b => b).ToList();
            if (top.Count == 0) return 0;
            double strongest = top.Max(b => P[b]);
            return top.First(b => P[b] > strongest * 0.01) * binHz;
        }
    }
}
