using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SynthAnalyser
{
    /// <summary>Compares the analysis of the original sample with the analysis of the user's own synth recording.</summary>
    public static class CompareWriter
    {
        class Row
        {
            public string Name, Orig, Mine, Advice;
            public double Severity;   // 0 = fine, 1 = small, 2 = clear, 3 = big
        }

        static double LowestF0(AnalysisResult r)
        {
            if (r.F0 <= 0) return 0;
            return (r.Patch.SubLevel > 0.05 && r.Patch.SubOctave < 0) ? r.F0 / 2 : r.F0;
        }

        static string NoteHz(double f)
        {
            if (f <= 0) return "unpitched";
            return Dsp.NoteName(f) + " / " + f.ToString("0.0") + " Hz";
        }

        static string EnvText(SynthPatch p)
        {
            return "A " + Dsp.Ms(p.AA) + ", D " + Dsp.Ms(p.AD) + ", S " + Math.Round(p.AS * 100) + "%, R " + Dsp.Ms(p.AR);
        }

        static string Osc(AnalysisResult r)
        {
            SynthPatch p = r.Patch;
            string s = SynthPatch.WaveName(p.Osc1Wave, p.PulseWidth);
            if (p.SubLevel > 0.05) s += p.SubOctave < 0 ? " + sub -1 oct" : " + sine layer";
            return s;
        }

        static string Lfo(SynthPatch p)
        {
            List<string> l = new List<string>();
            if (p.CutoffLfoRate > 0) l.Add("cutoff " + p.CutoffLfoRate.ToString("0.0#") + " Hz");
            if (p.TremoloRate > 0) l.Add("volume " + p.TremoloRate.ToString("0.0#") + " Hz");
            if (p.VibratoRate > 0) l.Add("pitch " + p.VibratoRate.ToString("0.0#") + " Hz");
            return l.Count == 0 ? "none" : string.Join(", ", l.ToArray());
        }

        static string Fx(SynthPatch p)
        {
            List<string> l = new List<string>();
            if (p.ReverbMix > 0) l.Add("reverb " + p.ReverbTime.ToString("0.0") + " s");
            if (p.DelayTime > 0) l.Add("delay " + Dsp.Ms(p.DelayTime));
            if (p.ChorusMix > 0) l.Add("chorus");
            if (p.Drive > 0) l.Add("drive");
            return l.Count == 0 ? "none" : string.Join(", ", l.ToArray());
        }

        public static string Write(AnalysisResult o, AnalysisResult m)
        {
            List<Row> rows = new List<Row>();
            SynthPatch po = o.Patch, pm = m.Patch;

            // ---- pitch
            {
                double fo = LowestF0(o), fm = LowestF0(m);
                Row r = new Row { Name = "Pitch (lowest note)", Orig = NoteHz(fo), Mine = NoteHz(fm) };
                if (fo > 0 && fm > 0)
                {
                    double st = 12 * Math.Log(fm / fo, 2);
                    double oct = Math.Round(st / 12);
                    if (Math.Abs(st) < 0.5) { r.Advice = "OK"; }
                    else if (oct != 0 && Math.Abs(st - oct * 12) < 0.6)
                    {
                        r.Severity = 3;
                        r.Advice = (oct < 0 ? "You are " + (-oct) + " octave(s) too LOW" : "You are " + oct + " octave(s) too HIGH") +
                                   " - play MIDI note " + Math.Round(Dsp.FreqToMidi(fo)) + " (" + fo.ToString("0.0") + " Hz) or transpose the oscillators by " + (-oct * 12).ToString("+0;-0") + " semitones. " +
                                   "Octave names differ between programs, so go by the MIDI number / Hz.";
                    }
                    else { r.Severity = 2; r.Advice = "Off by " + st.ToString("+0.0;-0.0") + " semitones - check the note you play and the oscillator transpose/tune."; }
                }
                rows.Add(r);
            }

            // ---- oscillators
            {
                Row r = new Row { Name = "Oscillators", Orig = Osc(o), Mine = Osc(m) };
                if (po.Osc1Wave != pm.Osc1Wave) { r.Severity = 1; r.Advice = "Different main waveform - try " + SynthPatch.WaveName(po.Osc1Wave, po.PulseWidth) + "."; }
                else r.Advice = "OK";
                if (po.SubLevel > 0.05 && po.SubOctave < 0 && !(pm.SubLevel > 0.05 && pm.SubOctave < 0))
                {
                    r.Severity = Math.Max(r.Severity, 2);
                    r.Advice = (r.Advice == "OK" ? "" : r.Advice + " ") + "The original is an octave layer: main osc one octave ABOVE the lowest note, plus a sine/sub one octave below it.";
                }
                rows.Add(r);
            }
            {
                Row r = new Row { Name = "Unison / detune", Orig = o.UnisonCents.ToString("0") + " cents spread", Mine = m.UnisonCents.ToString("0") + " cents spread" };
                double d = m.UnisonCents - o.UnisonCents;
                if (Math.Abs(d) < Math.Max(5, o.UnisonCents * 0.4)) r.Advice = "OK";
                else { r.Severity = 1; r.Advice = d > 0 ? "Too much detune - lower the unison detune amount." : "Not enough detune - raise unison detune / add voices."; }
                rows.Add(r);
            }
            {
                Row r = new Row { Name = "Stereo width (L/R corr.)", Orig = o.StereoCorr.ToString("0.00"), Mine = m.StereoCorr.ToString("0.00") };
                double d = m.StereoCorr - o.StereoCorr;
                if (Math.Abs(d) < 0.15) r.Advice = "OK";
                else { r.Severity = 1; r.Advice = d > 0 ? "Yours is narrower - increase unison stereo spread / chorus." : "Yours is wider - reduce stereo spread."; }
                rows.Add(r);
            }

            // ---- brightness
            {
                Row r = new Row { Name = "Brightness (centroid)", Orig = Dsp.Hz(o.Centroid), Mine = Dsp.Hz(m.Centroid) };
                double oct = Math.Log(Math.Max(1, m.Centroid) / Math.Max(1, o.Centroid), 2);
                if (Math.Abs(oct) < 0.4) r.Advice = "OK";
                else
                {
                    r.Severity = Math.Abs(oct) > 1.5 ? 3 : 2;
                    r.Advice = "Yours is " + Math.Abs(oct).ToString("0.0") + " octaves " + (oct < 0 ? "DARKER - open the filter (raise cutoff) or reduce filter-envelope/LFO pulling it down." : "BRIGHTER - lower the cutoff.");
                }
                rows.Add(r);
            }
            {
                Row r = new Row { Name = "Top end (-40 dB edge)", Orig = Dsp.Hz(o.Edge40), Mine = Dsp.Hz(m.Edge40) };
                double oct = Math.Log(Math.Max(1, m.Edge40) / Math.Max(1, o.Edge40), 2);
                r.Advice = Math.Abs(oct) < 0.7 ? "OK" : (oct < 0 ? "Missing high frequencies - cutoff too low or filter slope too steep." : "Too much top end.");
                if (Math.Abs(oct) >= 0.7) r.Severity = 2;
                rows.Add(r);
            }
            {
                string co = po.Filter == FilterMode.Off ? "open" : Dsp.Hz(po.Cutoff) + " LP" + po.FilterSlope;
                string cm = pm.Filter == FilterMode.Off ? "open" : Dsp.Hz(pm.Cutoff) + " LP" + pm.FilterSlope;
                rows.Add(new Row { Name = "Filter cutoff (est.)", Orig = co, Mine = cm, Advice = "see brightness" });
            }

            // ---- envelope
            {
                Row r = new Row { Name = "Attack (10-90% rise)", Orig = Dsp.Ms(o.AttackMeasured), Mine = Dsp.Ms(m.AttackMeasured) };
                double ratio = (m.AttackMeasured + 0.005) / (o.AttackMeasured + 0.005);
                if (ratio > 0.5 && ratio < 2) r.Advice = "OK";
                else
                {
                    r.Severity = ratio > 5 || ratio < 0.2 ? 3 : 2;
                    r.Advice = ratio > 1 ? "Your attack is " + ratio.ToString("0") + "x too SLOW - set amp attack to about " + Dsp.Ms(po.AA) + ". (Watch ms vs s!)"
                                         : "Your attack is too fast - set amp attack to about " + Dsp.Ms(po.AA) + ".";
                }
                rows.Add(r);
            }
            {
                Row r = new Row { Name = "Amp envelope (est.)", Orig = EnvText(po), Mine = EnvText(pm) };
                List<string> adv = new List<string>();
                if (po.AS < 0.05 && pm.AS > 0.3) { adv.Add("the original fades out continuously (no sustain plateau) - use sustain 0 with a long decay (~" + Dsp.Ms(po.AD) + ")"); r.Severity = 2; }
                else if (po.AS > 0.3 && pm.AS < 0.05) { adv.Add("the original holds a sustain level of ~" + Math.Round(po.AS * 100) + "%"); r.Severity = 2; }
                double dl = (pm.AS < 0.05 ? pm.AD : pm.AR) / Math.Max(0.01, po.AS < 0.05 ? po.AD : po.AR);
                bool known = (po.AS < 0.05 || o.ReleaseKnown) && (pm.AS < 0.05 || m.ReleaseKnown);
                if (known && (dl > 2 || dl < 0.5)) { adv.Add("the fade-out is " + (dl > 1 ? dl.ToString("0.0") + "x longer" : (1 / dl).ToString("0.0") + "x shorter")); r.Severity = Math.Max(r.Severity, 1); }
                r.Advice = adv.Count == 0 ? "OK" : string.Join("; ", adv.ToArray());
                rows.Add(r);
            }
            if (o.TransientDb > 0 || m.TransientDb > 0)
            {
                Row r = new Row
                {
                    Name = "Initial punch",
                    Orig = o.TransientDb > 0 ? "+" + o.TransientDb.ToString("0") + " dB / " + Dsp.Ms(o.TransientTime) : "none",
                    Mine = m.TransientDb > 0 ? "+" + m.TransientDb.ToString("0") + " dB / " + Dsp.Ms(m.TransientTime) : "none"
                };
                if (o.TransientDb > 0 && m.TransientDb <= 0) { r.Severity = 2; r.Advice = "Add a punchy start: a 2nd envelope (A 0, D ~" + Dsp.Ms(o.TransientTime) + ", S 0) to volume or cutoff."; }
                else r.Advice = "OK";
                rows.Add(r);
            }

            // ---- modulation / fx
            {
                Row r = new Row { Name = "LFO movement", Orig = Lfo(po), Mine = Lfo(pm), Advice = "OK" };
                if (po.CutoffLfoRate > 0)
                {
                    if (pm.CutoffLfoRate <= 0) { r.Severity = 2; r.Advice = "Add an LFO on the cutoff at " + po.CutoffLfoRate.ToString("0.0#") + " Hz (one cycle every " + Dsp.Ms(1 / po.CutoffLfoRate) + "), bipolar."; }
                    else if (Math.Abs(Math.Log(pm.CutoffLfoRate / po.CutoffLfoRate, 2)) > 0.3) { r.Severity = 2; r.Advice = "LFO rate differs - set it to " + po.CutoffLfoRate.ToString("0.0#") + " Hz."; }
                }
                else if (pm.CutoffLfoRate > 0 || pm.TremoloRate > 0) { r.Severity = 1; r.Advice = "Your version has movement the original does not show."; }
                rows.Add(r);
            }
            {
                Row r = new Row { Name = "Effects", Orig = Fx(po), Mine = Fx(pm), Advice = Fx(po) == Fx(pm) ? "OK" : "compare by ear" };
                rows.Add(r);
            }

            // ---- output
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("COMPARISON: ORIGINAL vs YOUR VERSION");
            sb.AppendLine("Original: " + o.FileName + "     Yours: " + m.FileName);
            sb.AppendLine(new string('-', 64));
            List<Row> issues = rows.Where(x => x.Severity >= 2).OrderByDescending(x => x.Severity).ToList();
            sb.AppendLine(issues.Count == 0 ? "No big differences found - fine-tune by ear." : "FIX FIRST:");
            int n = 1;
            foreach (Row x in issues) sb.AppendLine(" " + (n++) + ". " + x.Name + ": " + x.Advice);
            sb.AppendLine();
            sb.AppendLine("DETAILS");
            foreach (Row x in rows)
            {
                string mark = x.Severity >= 3 ? "!!" : x.Severity >= 2 ? "! " : x.Severity >= 1 ? "~ " : "  ";
                sb.AppendLine(mark + x.Name);
                sb.AppendLine("     original: " + x.Orig);
                sb.AppendLine("     yours:    " + x.Mine);
                if (x.Advice != "OK" && x.Severity < 2) sb.AppendLine("     -> " + x.Advice);
            }
            sb.AppendLine();
            sb.AppendLine("Tip: record the same note, the same length, without extra effects, for the fairest comparison.");
            return sb.ToString();
        }
    }
}
