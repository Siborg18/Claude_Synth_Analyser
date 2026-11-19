using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace SynthAnalyser
{
    /// <summary>
    /// Writes the analysed patch as a Vital (.vital) preset. A neutral Vital 1.0.7 preset is embedded as a
    /// template; only the parameters the analysis knows about are changed.
    ///
    /// Vital stores most values in internal units:
    ///   envelope times: seconds = value^4            filter cutoff: MIDI note number (8..136)
    ///   LFO rate / portamento / delay: log2(Hz or s) oscillator level: gain = value^2
    ///   modulation amount: fraction of the destination's full range
    /// </summary>
    public static class VitalExport
    {
        const double CutoffMin = 8, CutoffMax = 136;          // semitones
        const double TransposeRange = 96;                      // -48..+48 semitones
        const double EnvMax = 2.3784142300054421;              // 32 s ^ (1/4)

        static Dictionary<string, object> settings;
        static List<string> modSrc = new List<string>(), modDst = new List<string>();

        static string LoadTemplate()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string name in asm.GetManifestResourceNames())
                if (name.EndsWith("VitalTemplate.json", StringComparison.OrdinalIgnoreCase))
                    using (StreamReader r = new StreamReader(asm.GetManifestResourceStream(name), Encoding.UTF8))
                        return r.ReadToEnd();
            throw new InvalidOperationException("Vital template resource missing.");
        }

        static void Set(string key, double v) { settings[key] = v; }
        static double EnvTime(double sec) { return Math.Min(EnvMax, Math.Pow(Math.Max(0, sec), 0.25)); }
        static double CutoffNote(double hz) { return Dsp.Clamp(Dsp.FreqToMidi(Math.Max(1, hz)), CutoffMin, CutoffMax); }
        static double Log2(double v) { return Math.Log(v, 2); }

        static void Env(int n, double a, double d, double s, double r)
        {
            Set("env_" + n + "_attack", EnvTime(a));
            Set("env_" + n + "_decay", EnvTime(d));
            Set("env_" + n + "_sustain", Dsp.Clamp(s, 0, 1));
            Set("env_" + n + "_release", EnvTime(r));
            Set("env_" + n + "_hold", 0); Set("env_" + n + "_delay", 0);
        }

        static void Mod(string source, string dest, double amount, bool bipolar)
        {
            int idx = modSrc.Count;           // 0-based slot
            if (idx >= 64) return;
            modSrc.Add(source); modDst.Add(dest);
            string k = "modulation_" + (idx + 1) + "_";
            Set(k + "amount", Dsp.Clamp(amount, -1, 1));
            Set(k + "bipolar", bipolar ? 1 : 0);
            Set(k + "bypass", 0); Set(k + "power", 0); Set(k + "stereo", 0);
        }

        // ------------------------------------------------------------ wavetables
        static string WaveData(Wave w, double pw)
        {
            const int N = 2048;
            float[] d = new float[N];
            double peak = 1e-9;
            double[] tmp = new double[N];
            for (int i = 0; i < N; i++)
            {
                double ph = i / (double)N, v = 0;
                switch (w)
                {
                    case Wave.Sine: v = Math.Sin(2 * Math.PI * ph); break;
                    case Wave.Triangle:
                        for (int k = 1; k < 256; k += 2) v += ((k / 2) % 2 == 0 ? 1 : -1) * Math.Sin(2 * Math.PI * k * ph) / (k * (double)k);
                        break;
                    case Wave.Square:
                    case Wave.Pulse:
                        {
                            double wdt = w == Wave.Square ? 0.5 : pw;
                            for (int k = 1; k < 512; k++) v += Math.Sin(Math.PI * k * wdt) / k * Math.Cos(2 * Math.PI * k * (ph - wdt / 2));
                            break;
                        }
                    default: // saw, falling like Vital's Init table
                        for (int k = 1; k < 512; k++) v += Math.Sin(2 * Math.PI * k * ph) / k;
                        break;
                }
                tmp[i] = v; peak = Math.Max(peak, Math.Abs(v));
            }
            for (int i = 0; i < N; i++) d[i] = (float)(tmp[i] / peak);
            byte[] b = new byte[N * 4];
            Buffer.BlockCopy(d, 0, b, 0, b.Length);
            return Convert.ToBase64String(b);
        }

        static void SetWavetable(int index, Wave w, double pw)
        {
            IList tables = (IList)settings["wavetables"];
            Dictionary<string, object> wt = (Dictionary<string, object>)tables[index];
            wt["name"] = SynthPatch.WaveName(w, pw);
            wt["author"] = "Synth Analyser";
            IList groups = (IList)wt["groups"];
            Dictionary<string, object> comp = (Dictionary<string, object>)((IList)((Dictionary<string, object>)groups[0])["components"])[0];
            Dictionary<string, object> kf = new Dictionary<string, object>();
            kf["position"] = 0;
            kf["wave_data"] = WaveData(w, pw);
            comp["keyframes"] = new object[] { kf };
            comp["type"] = "Wave Source";
            // keep only the first group/component
            while (groups.Count > 1) groups.RemoveAt(groups.Count - 1);
        }

        // ------------------------------------------------------------ export
        public static string Build(AnalysisResult r, string presetName)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 256;
            Dictionary<string, object> root = js.Deserialize<Dictionary<string, object>>(LoadTemplate());
            settings = (Dictionary<string, object>)root["settings"];
            modSrc.Clear(); modDst.Clear();
            // make lists mutable
            settings["wavetables"] = new ArrayList((ICollection)settings["wavetables"]);
            foreach (object o in (IList)settings["wavetables"])
                ((Dictionary<string, object>)o)["groups"] = new ArrayList((ICollection)((Dictionary<string, object>)o)["groups"]);

            SynthPatch p = r.Patch;
            root["preset_name"] = presetName;
            root["author"] = "Synth Analyser";
            root["preset_style"] = StyleFor(r.Category);
            root["comments"] = "Generated by Synth Analyser from " + (r.FileName ?? "a sample") + ". " +
                               (r.Pitched && r.F0 > 0 ? "Play MIDI note " + Math.Round(Dsp.FreqToMidi(r.F0)) + " (" + r.F0.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " Hz). " : "") +
                               (r.FmHint != null ? "Inharmonic/bell: try FM - " + r.FmHint + ". " : "") +
                               (r.IsChord ? "Chord: " + r.ChordName + ". " : "") +
                               "Values are estimates - fine-tune by ear.";

            bool noiseOnly = p.Osc1Wave == Wave.Noise;
            double cents = r.Pitched && r.F0 > 0 && !r.IsChord ? Dsp.CentsOff(r.F0) : 0;

            // ---- oscillator 1
            double lvl1 = 0.707;
            bool punch = p.PunchDb > 3;
            if (punch && !noiseOnly) lvl1 = 0.55;   // leave headroom for the transient boost
            Set("osc_1_on", noiseOnly ? 0 : 1);
            SetWavetable(0, noiseOnly ? Wave.Saw : p.Osc1Wave, p.PulseWidth);
            Set("osc_1_wave_frame", 0);
            Set("osc_1_level", lvl1);
            Set("osc_1_transpose", 0);
            Set("osc_1_tune", Math.Abs(cents) > 10 ? cents / 100.0 : 0);
            Set("osc_1_destination", 0);
            Set("osc_1_unison_voices", Math.Max(1, p.UnisonVoices));
            if (p.UnisonVoices > 1)
            {
                // Vital's detune knob: empirically ~2 display units per cent of outer-voice detune in our model
                double disp = Dsp.Clamp(p.UnisonDetuneCents * 0.5, 1, 100);
                Set("osc_1_unison_detune", Math.Sqrt(disp));
                Set("osc_1_stereo_spread", Dsp.Clamp(Math.Max(p.StereoWidth, 0.3), 0, 1));
            }
            else Set("osc_1_stereo_spread", 0);
            Set("osc_1_random_phase", 1);
            Set("osc_1_spectral_morph_type", 0);
            Set("osc_1_distortion_type", 0);

            // ---- oscillator 2 = sub / sine layer
            bool sub = !noiseOnly && p.SubLevel > 0.05;
            Set("osc_2_on", sub ? 1 : 0);
            SetWavetable(1, Wave.Sine, 0.5);
            Set("osc_2_wave_frame", 0);
            Set("osc_2_transpose", sub ? p.SubOctave * 12 : 0);
            Set("osc_2_tune", Math.Abs(cents) > 10 ? cents / 100.0 : 0);
            Set("osc_2_level", sub ? Math.Min(1, lvl1 * Math.Sqrt(Math.Min(1.4, p.SubLevel))) : 0.707);
            Set("osc_2_unison_voices", 1);
            Set("osc_2_destination", 0);
            Set("osc_2_spectral_morph_type", 0);
            Set("osc_2_distortion_type", 0);
            Set("osc_3_on", 0);

            // ---- noise (Vital's sample oscillator holds white noise in the template)
            double noise = noiseOnly ? 1.0 : p.NoiseLevel;
            bool useNoise = noise > 0.03;
            Set("sample_on", useNoise ? 1 : 0);
            Set("sample_destination", 0);
            Set("sample_loop", 1);
            Set("sample_random_phase", 1);
            Set("sample_keytrack", 0);
            double sLvl = noiseOnly ? 0.707 : Math.Min(1, lvl1 * Math.Sqrt(noise));
            if (useNoise && p.NoiseDecay > 0 && !noiseOnly)
            {
                Set("sample_level", 0);
                Env(4, 0, p.NoiseDecay * 2, 0, p.NoiseDecay);
                Mod("env_4", "sample_level", sLvl, false);
            }
            else Set("sample_level", sLvl);
            if (noiseOnly && p.PunchDb > 3)
            {
                Set("sample_level", 0.55);
                Env(5, 0, p.PunchTime * 0.6, 0, p.PunchTime * 0.6);   // calibrated in Vital: a short, fast drop
                Mod("env_5", "sample_level", Math.Min(1, 0.55 * Math.Pow(10, Math.Min(p.PunchDb, 12) / 40.0)) - 0.55, false);
            }

            // ---- filter 1
            bool filt = p.Filter != FilterMode.Off || Math.Abs(p.FilterEnvOct) >= 0.4 || p.CutoffLfoRate > 0;
            Set("filter_1_on", 1);
            Set("filter_1_model", 0);                                          // analog
            Set("filter_1_style", filt && p.FilterSlope == 24 && p.Filter != FilterMode.BandPass ? 1 : 0);
            Set("filter_1_blend", p.Filter == FilterMode.HighPass ? 2 : p.Filter == FilterMode.BandPass ? 1 : 0);
            Set("filter_1_cutoff", filt ? CutoffNote(p.Cutoff) : CutoffMax);  // fully open when no filtering
            Set("filter_1_resonance", filt ? Dsp.Clamp(p.Resonance, 0, 1) : 0);
            Set("filter_1_keytrack", noiseOnly ? 0 : (filt ? 0.5 : 0));
            Set("filter_1_drive", 0);
            Set("filter_1_mix", 1);
            Set("filter_1_filter_input", 0);

            // ---- filter 2 = extra high-pass (in series after filter 1)
            bool hp = p.HighPassHz > 0;
            Set("filter_2_on", hp ? 1 : 0);
            Set("filter_2_filter_input", hp ? 1 : 0);
            Set("filter_2_model", 0); Set("filter_2_style", 0); Set("filter_2_blend", 2);
            Set("filter_2_cutoff", hp ? CutoffNote(p.HighPassHz) : 60);
            Set("filter_2_resonance", 0); Set("filter_2_keytrack", 0);

            // ---- amp envelope (env 1)
            Env(1, p.AA, p.AD, p.AS, p.AR);
            if (r.Decay25Db >= 0)
            {
                // Vital's envelope curve: more negative power = faster early fall. Calibrated on two
                // A/B tests (4.5 dB at 25 % -> about -1, 17 dB -> about -4).
                double pw = Dsp.Clamp(-r.Decay25Db / 4.0, -8, 0);
                Set("env_1_decay_power", pw);
                Set("env_1_release_power", pw);
            }

            // ---- transient punch: env 5 lifts the oscillator levels for the first few ms
            if (punch && !noiseOnly)
            {
                Env(5, 0, p.PunchTime * 0.6, 0, p.PunchTime * 0.6);   // calibrated in Vital: a short, fast drop
                double target = Math.Min(1, lvl1 * Math.Pow(10, Math.Min(p.PunchDb, 12) / 40.0));   // level is squared -> /40
                if (!noiseOnly) Mod("env_5", "osc_1_level", target - lvl1, false);
                if (sub) Mod("env_5", "osc_2_level", target - lvl1, false);
            }

            // ---- pitch envelope (env 2)
            if (Math.Abs(p.PitchEnvSemis) >= 0.8)
            {
                Env(2, 0, Math.Max(0.01, p.PitchEnvTime), 0, Math.Max(0.01, p.PitchEnvTime));
                double amt = p.PitchEnvSemis / TransposeRange;
                Mod("env_2", "osc_1_transpose", amt, false);
                if (sub) Mod("env_2", "osc_2_transpose", amt, false);
            }

            // ---- filter envelope (env 3)
            if (Math.Abs(p.FilterEnvOct) >= 0.4)
            {
                Env(3, p.FA, p.FD * 1.5, p.FS, p.FR);
                Mod("env_3", "filter_1_cutoff", p.FilterEnvOct * 12 / (CutoffMax - CutoffMin), false);
            }

            // ---- LFO 1 -> cutoff (wobble)
            if (p.CutoffLfoRate > 0)
            {
                Set("lfo_1_sync", 0); Set("lfo_1_frequency", Log2(p.CutoffLfoRate)); Set("lfo_1_sync_type", 0);
                Mod("lfo_1", "filter_1_cutoff", 2 * p.CutoffLfoOct * 12 / (CutoffMax - CutoffMin), true);
            }
            // ---- LFO 2 -> pitch (vibrato)
            if (p.VibratoRate > 0)
            {
                Set("lfo_2_sync", 0); Set("lfo_2_frequency", Log2(p.VibratoRate)); Set("lfo_2_sync_type", 0);
                double amt = 2 * (p.VibratoCents / 100.0) / TransposeRange;
                Mod("lfo_2", "osc_1_transpose", amt, true);
                if (sub) Mod("lfo_2", "osc_2_transpose", amt, true);
            }
            // ---- LFO 3 -> level (tremolo)
            if (p.TremoloRate > 0)
            {
                Set("lfo_3_sync", 0); Set("lfo_3_frequency", Log2(p.TremoloRate)); Set("lfo_3_sync_type", 0);
                Mod("lfo_3", "osc_1_level", -p.TremoloDepth * lvl1, false);
            }

            // ---- voicing
            bool mono = p.Mono;
            Set("polyphony", mono ? 1 : 8);
            Set("legato", mono ? 1 : 0);
            Set("portamento_time", mono && p.Glide > 0 ? Log2(p.Glide) : -10);

            // ---- effects
            Set("chorus_on", p.ChorusMix > 0 ? 1 : 0);
            if (p.ChorusMix > 0) Set("chorus_dry_wet", Dsp.Clamp(p.ChorusMix * 0.65, 0, 1));   // Vital's chorus is wide: calibrated
            Set("delay_on", p.DelayTime > 0 ? 1 : 0);
            if (p.DelayTime > 0)
            {
                Set("delay_sync", 0);
                Set("delay_frequency", Log2(1.0 / p.DelayTime));
                Set("delay_aux_sync", 0);
                Set("delay_aux_frequency", Log2(1.0 / p.DelayTime));
                Set("delay_feedback", Dsp.Clamp(p.DelayFeedback, 0, 0.95));
                Set("delay_dry_wet", Dsp.Clamp(p.DelayMix, 0, 1));
            }
            Set("reverb_on", p.ReverbMix > 0 ? 1 : 0);
            if (p.ReverbMix > 0)
            {
                Set("reverb_decay_time", Dsp.Clamp(Log2(Math.Max(0.1, p.ReverbTime)), -6, 6));
                Set("reverb_dry_wet", Dsp.Clamp(p.ReverbMix, 0, 1));
            }
            Set("distortion_on", p.Drive > 0 ? 1 : 0);
            if (p.Drive > 0) { Set("distortion_type", 0); Set("distortion_drive", p.Drive * 20); Set("distortion_mix", 1); }

            // ---- modulation routing table
            List<object> mods = new List<object>();
            for (int i = 0; i < 64; i++)
            {
                Dictionary<string, object> m = new Dictionary<string, object>();
                m["source"] = i < modSrc.Count ? modSrc[i] : "";
                m["destination"] = i < modDst.Count ? modDst[i] : "";
                mods.Add(m);
            }
            settings["modulations"] = mods;

            return js.Serialize(root);
        }

        static string StyleFor(string category)
        {
            string c = (category ?? "").ToLowerInvariant();
            if (c.Contains("bass")) return "Bass";
            if (c.Contains("pad")) return "Pad";
            if (c.Contains("pluck")) return "Keys";
            if (c.Contains("lead")) return "Lead";
            if (c.Contains("kick") || c.Contains("percussion") || c.Contains("drum")) return "Percussion";
            if (c.Contains("keys") || c.Contains("bell") || c.Contains("chord")) return "Keys";
            return "";
        }

        /// <summary>" [E2 - MIDI 40]" style suffix naming the note to play, or "" for unpitched sounds.</summary>
        public static string NoteSuffix(AnalysisResult r)
        {
            if (!r.Pitched || r.F0 <= 0) return "";
            int midi = (int)Math.Round(Dsp.FreqToMidi(r.F0));
            return " [" + Dsp.PitchClassName(midi) + (midi / 12 - 1) + " - MIDI " + midi + "]";
        }

        public static void Save(AnalysisResult r, string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            File.WriteAllText(path, Build(r, name), new UTF8Encoding(false));
        }
    }
}
