using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SynthAnalyser
{
    /// <summary>Turns an analysis into a human-readable, generic subtractive synth recipe.</summary>
    public static class RecipeWriter
    {
        static string Row(string name, string value)
        {
            return "   " + name.PadRight(15, '.') + " " + value;
        }

        static string Pct(double v) { return Math.Round(v * 100).ToString("0") + " %"; }

        public static string NoteValue(double seconds, double bpm)
        {
            if (bpm <= 0 || seconds <= 0) return null;
            double beat = 60.0 / bpm;
            string[] names = { "1/1", "1/2", "1/4", "1/8", "1/16", "1/32" };
            double[] beats = { 4, 2, 1, 0.5, 0.25, 0.125 };
            string best = null; double bd = 0.08;
            for (int i = 0; i < names.Length; i++)
            {
                foreach (var v in new[] { Tuple.Create("", 1.0), Tuple.Create(" dotted", 1.5), Tuple.Create(" triplet", 2.0 / 3.0) })
                {
                    double t = beats[i] * v.Item2 * beat;
                    double d = Math.Abs(seconds / t - 1);
                    if (d < bd) { bd = d; best = names[i] + v.Item1; }
                }
            }
            return best;
        }

        static string RateText(double hz, double bpm)
        {
            string s = hz.ToString("0.0#") + " Hz (one cycle every " + Dsp.Ms(1.0 / hz) + ")";
            string nv = NoteValue(1.0 / hz, bpm);
            if (nv != null) s += "  (= " + nv + " at " + bpm.ToString("0") + " BPM)";
            return s;
        }

        static string T(double sec) { return sec < 0.003 ? "0 ms (instant)" : Dsp.Ms(sec); }
        static string Env(double a, double d, double s, double r)
        {
            return "A " + T(a) + "   D " + Dsp.Ms(d) + "   S " + Pct(s) + "   R " + Dsp.Ms(r);
        }

        static string NoteText(double f)
        {
            int midi = (int)Math.Round(Dsp.FreqToMidi(f));
            string pc = Dsp.PitchClassName(midi);
            int oct = midi / 12 - 1;
            return Dsp.NoteName(f) + "  (MIDI note " + midi + ", " + f.ToString("0.0") + " Hz, " + Dsp.CentsOff(f).ToString("+0;-0;0") + " cents)" +
                   "\n           Octave names differ per program: " + pc + oct + " in scientific naming (middle C = C4), " +
                   pc + (oct + 1) + " in FL Studio, " + pc + (oct - 1) + " in Ableton/Cubase/Logic. Check the MIDI number or Hz.";
        }

        public static string Write(AnalysisResult r)
        {
            SynthPatch p = r.Patch;
            StringBuilder sb = new StringBuilder();
            string line = new string('-', 64);
            sb.AppendLine("SYNTH RECIPE  -  " + r.Category.ToUpperInvariant() + "   (confidence ~" + r.Confidence + " %)");
            sb.AppendLine(line);
            if (r.IsChord)
                sb.AppendLine("Chord: " + r.ChordName);
            else if (r.Pitched && r.F0 > 0)
            {
                sb.AppendLine("Play note: " + NoteText(r.F0));
            }
            else sb.AppendLine("Unpitched sound - pitch/key is not critical.");
            if (r.IsPhrase && r.PhraseDescription != null)
                sb.AppendLine("Phrase: " + r.PhraseDescription + (r.Bpm > 0 ? "   @ ~" + r.Bpm.ToString("0") + " BPM" : ""));
            sb.AppendLine();

            // ---------------------------------------------------------------- oscillators
            sb.AppendLine("1. OSCILLATORS");
            if (p.Osc1Wave == Wave.Noise)
            {
                sb.AppendLine(Row("Osc 1", "Noise (white) - the sound is mostly noise"));
                sb.AppendLine(Row("Tonal oscs", "off (or very low)"));
            }
            else
            {
                sb.AppendLine(Row("Osc 1", SynthPatch.WaveName(p.Osc1Wave, p.PulseWidth)));
                if (p.UnisonVoices >= 2)
                {
                    if (p.UnisonVoices == 2)
                        sb.AppendLine(Row("Osc 2", "same wave, detuned " + (p.UnisonDetuneCents * 2).ToString("0") + " cents from Osc 1 (or unison 2 voices, +/-" + p.UnisonDetuneCents.ToString("0") + " ct)"));
                    else
                        sb.AppendLine(Row("Unison", p.UnisonVoices + " voices, detune +/-" + p.UnisonDetuneCents.ToString("0") + " cents"));
                }
                if (p.StereoWidth > 0.05 && p.UnisonVoices >= 2)
                    sb.AppendLine(Row("Stereo spread", Pct(p.StereoWidth)));
                if (p.SubLevel > 0.05)
                    sb.AppendLine(Row(p.SubOctave < 0 ? "Sub osc" : "Sine layer", "Sine, " + (p.SubOctave < 0 ? "-1 octave" : "same pitch as Osc 1") + ", level " + Pct(Math.Min(1, p.SubLevel))));
                if (p.NoiseLevel > 0.03)
                    sb.AppendLine(Row("Noise", Pct(p.NoiseLevel) + (p.NoiseDecay > 0 ? "  - short burst on the attack (~" + Dsp.Ms(p.NoiseDecay) + "), use its own fast envelope" : "  - sustained, through the filter")));
                double c = Dsp.CentsOff(r.F0);
                if (r.Pitched && Math.Abs(c) > 15 && !r.IsChord)
                    sb.AppendLine(Row("Fine tune", c.ToString("+0;-0") + " cents (sample is not at concert pitch)"));
                if (r.Inharmonic)
                {
                    sb.AppendLine(Row("Inharmonic", "partial ratios " + r.InharmonicRatios));
                    if (r.FmHint != null) sb.AppendLine(Row("FM guess", r.FmHint));
                    sb.AppendLine(Row("", "-> use FM or ring-mod (Osc 2 modulating Osc 1 at that ratio), with a decaying mod envelope"));
                }
            }
            sb.AppendLine();

            // ---------------------------------------------------------------- filter
            sb.AppendLine("2. FILTER");
            if (p.Filter == FilterMode.Off)
            {
                sb.AppendLine(Row("Filter", "off / fully open (all harmonics present)"));
            }
            else
            {
                string type = p.Filter == FilterMode.LowPass ? "Low-pass" : p.Filter == FilterMode.HighPass ? "High-pass" : "Band-pass";
                sb.AppendLine(Row("Type", type + " " + p.FilterSlope + " dB/oct"));
                sb.AppendLine(Row("Cutoff", Dsp.Hz(p.Cutoff) + (r.F0 > 0 && p.Filter == FilterMode.LowPass ? "  (~" + (p.Cutoff / r.F0).ToString("0.#") + "x the note frequency)" : "")));
                sb.AppendLine(Row("Resonance", Pct(p.Resonance)));
                if (Math.Abs(p.FilterEnvOct) >= 0.4)
                {
                    double peakHz = Math.Min(20000, p.Cutoff * Math.Pow(2, p.FilterEnvOct));
                    sb.AppendLine(Row("Env amount", (p.FilterEnvOct > 0 ? "+" : "") + p.FilterEnvOct.ToString("0.0") + " oct (" + Math.Round(p.FilterEnvOct * 12) + " semitones; opens to ~" + Dsp.Hz(peakHz) + ")"));
                    sb.AppendLine(Row("Filter env", Env(p.FA, p.FD, p.FS, p.FR)));
                }
                if (p.Filter == FilterMode.LowPass && r.Pitched)
                    sb.AppendLine(Row("Key tracking", "50 % (adjust so other notes keep the same brightness)"));
            }
            if (p.HighPassHz > 0)
                sb.AppendLine(Row("High-pass", Dsp.Hz(p.HighPassHz) + " (thin out the low end)"));
            sb.AppendLine();

            // ---------------------------------------------------------------- amp
            sb.AppendLine("3. AMP ENVELOPE");
            sb.AppendLine(Row("Amp env", Env(p.AA, p.AD, p.AS, p.AR)));
            if (p.AS < 0.01) sb.AppendLine(Row("", "(no sustain: the Decay sets the length of the sound)"));
            if (r.TransientDb > 0)
                sb.AppendLine(Row("Transient", "the first ~" + Dsp.Ms(r.TransientTime) + " are " + r.TransientDb.ToString("0") + " dB louder: add punch with a 2nd envelope (A 0, D ~" + Dsp.Ms(r.TransientTime) + ", S 0) on volume or cutoff, or a short noise/click layer"));
            sb.AppendLine();

            // ---------------------------------------------------------------- modulation
            List<string> mod = new List<string>();
            if (Math.Abs(p.PitchEnvSemis) >= 0.8)
                mod.Add(Row("Pitch env", (p.PitchEnvSemis > 0 ? "+" : "") + p.PitchEnvSemis.ToString("0.#") + " semitones -> 0 over " + Dsp.Ms(p.PitchEnvTime) + " (A 0, D " + Dsp.Ms(p.PitchEnvTime) + ", S 0)"));
            if (p.VibratoRate > 0)
                mod.Add(Row("Vibrato", "LFO sine " + RateText(p.VibratoRate, r.Bpm) + " -> pitch, +/-" + p.VibratoCents.ToString("0") + " cents" + (p.VibratoDelay > 0 ? ", fade-in/delay " + Dsp.Ms(p.VibratoDelay) : "")));
            if (p.TremoloRate > 0)
                mod.Add(Row("Tremolo", "LFO " + RateText(p.TremoloRate, r.Bpm) + " -> amp, depth " + Pct(p.TremoloDepth)));
            if (p.CutoffLfoRate > 0)
                mod.Add(Row("Filter LFO", "LFO " + RateText(p.CutoffLfoRate, r.Bpm) + " -> cutoff, +/-" + p.CutoffLfoOct.ToString("0.0") + " oct (bipolar: sweeps above AND below the cutoff)"));
            if (p.Mono)
                mod.Add(Row("Voice mode", "Mono / legato, glide " + Dsp.Ms(p.Glide)));
            else if (r.IsChord)
                mod.Add(Row("Voice mode", "Poly (" + r.ChordFreqs.Count + "+ voices)"));
            if (mod.Count > 0)
            {
                sb.AppendLine("4. PITCH & MODULATION");
                foreach (string m in mod) sb.AppendLine(m);
                sb.AppendLine();
            }

            // ---------------------------------------------------------------- effects
            List<string> fx = new List<string>();
            if (p.Drive > 0) fx.Add(Row("Distortion", "soft clip / saturation, drive ~" + Pct(p.Drive)));
            if (p.ChorusMix > 0) fx.Add(Row("Chorus", "mix " + Pct(p.ChorusMix) + " (the sample is wide in stereo)"));
            if (p.DelayTime > 0)
            {
                string nv = NoteValue(p.DelayTime, r.Bpm);
                fx.Add(Row("Delay", Dsp.Ms(p.DelayTime) + (nv != null ? " (= " + nv + ")" : "") + ", feedback " + Pct(p.DelayFeedback) + ", mix " + Pct(p.DelayMix)));
            }
            if (p.ReverbMix > 0) fx.Add(Row("Reverb", "decay ~" + p.ReverbTime.ToString("0.0") + " s, mix " + Pct(p.ReverbMix)));
            sb.AppendLine((mod.Count > 0 ? "5" : "4") + ". EFFECTS");
            if (fx.Count == 0) sb.AppendLine(Row("Effects", "none detected (dry sound)"));
            foreach (string f in fx) sb.AppendLine(f);
            sb.AppendLine();

            // ---------------------------------------------------------------- tips
            sb.AppendLine("TIPS");
            List<string> tips = new List<string>(r.Notes2);
            if (p.Filter == FilterMode.LowPass && Math.Abs(p.FilterEnvOct) >= 0.4)
                tips.Add("Most of the 'movement' is the filter envelope: set the cutoff low, then raise Env amount until the attack sounds as bright as the sample.");
            if (p.UnisonVoices >= 4) tips.Add("Big unison: if your synth has no unison, stack 2-3 detuned oscillators and add chorus.");
            if (r.IsPhrase) tips.Add("This is a phrase: the recipe describes the most representative note. Program the notes/rhythm shown above in your DAW or arpeggiator.");
            if (r.FitErrorDb > 8) tips.Add("The harmonic spectrum does not match a basic waveform very well - try wavetable positions or mixing two waveforms (e.g. saw + square).");
            if (!r.Pitched) tips.Add("For noise sounds, the filter shape and the envelope carry the character. Sweep the cutoff while listening.");
            tips.Add("All values are estimates. Use 'Play synth preview' vs 'Play original' to A/B, then fine-tune cutoff and envelope times by ear.");
            foreach (string t in tips.Distinct()) sb.AppendLine(" - " + t);
            return sb.ToString();
        }

        static string SecondRatio(string ratios)
        {
            if (string.IsNullOrEmpty(ratios)) return "1.41";
            string[] parts = ratios.Split(':');
            return parts.Length >= 2 ? parts[1].Trim() : parts[0].Trim();
        }

        public static string MeasurementsText(AnalysisResult r)
        {
            StringBuilder sb = new StringBuilder();
            string last = null;
            List<string> order = r.Measurements.Select(m => m.Group).Distinct().ToList();
            foreach (Measurement m in r.Measurements.OrderBy(m => order.IndexOf(m.Group)))
            {
                if (m.Group != last) { sb.AppendLine(); sb.AppendLine("[" + m.Group + "]"); last = m.Group; }
                sb.AppendLine("  " + m.Name.PadRight(28) + m.Value + (string.IsNullOrEmpty(m.Note) ? "" : "    (" + m.Note + ")"));
            }
            if (r.Harmonics.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("[Harmonics (settled part), dB relative to strongest]");
                foreach (Harmonic h in r.Harmonics.Take(24))
                    sb.AppendLine("  H" + h.K.ToString().PadRight(3) + (h.Freq.ToString("0") + " Hz").PadRight(12) + (h.Present ? h.Db.ToString("0.0") + " dB" : "  -   (below noise)"));
            }
            return sb.ToString().TrimStart();
        }
    }
}
