using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SynthAnalyser
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            // Command-line mode: SynthAnalyser.exe --analyse sample.wav [--render preview.wav]
            if (args.Length >= 2 && (args[0] == "--analyse" || args[0] == "--analyze"))
                return RunCli(args);
            if (args.Length >= 3 && args[0] == "--batch")
                return RunBatch(args[1], args[2]);
            if (args.Length >= 3 && args[0] == "--compare")
            {
                try
                {
                    AnalysisResult a = Analyzer.Analyze(Limit(AudioIO.Load(args[1])));
                    AnalysisResult b = Analyzer.Analyze(Limit(AudioIO.Load(args[2])));
                    Console.WriteLine(CompareWriter.Write(a, b));
                    return 0;
                }
                catch (Exception ex) { Console.Error.WriteLine("Error: " + ex); return 1; }
            }

            try { if (Environment.OSVersion.Platform == PlatformID.Win32NT) SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm f = new MainForm();
            if (args.Length == 1 && File.Exists(args[0])) f.LoadFileDeferred(args[0]);
            Application.Run(f);
            return 0;
        }

        static int RunCli(string[] args)
        {
            try
            {
                AudioClip clip = AudioIO.Load(args[1]);
                AnalysisResult r = Analyzer.Analyze(clip);
                Console.WriteLine(r.Recipe);
                Console.WriteLine("==== MEASUREMENTS ====");
                Console.WriteLine(RecipeWriter.MeasurementsText(r));
                for (int i = 2; i + 1 < args.Length; i++)
                {
                    if (args[i] == "--vital")
                        VitalExport.Save(r, args[i + 1]);
                    if (args[i] == "--render")
                    {
                        float[][] o = PreviewSynth.Render(r.Patch, r.Notes, clip.SampleRate, clip.Duration + 1.0, r.F0);
                        Normalize(o, 0.9f);
                        File.WriteAllBytes(args[i + 1], AudioIO.ToWavBytes(o, clip.SampleRate));
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex);
                return 1;
            }
        }

        /// <summary>Analyses every audio file in a folder and writes a .vital preset (+ recipe .txt) for each.</summary>
        static int RunBatch(string inDir, string outDir)
        {
            Directory.CreateDirectory(outDir);
            string[] exts = { ".wav", ".wave", ".aif", ".aiff", ".mp3", ".flac", ".ogg", ".m4a" };
            System.Text.StringBuilder index = new System.Text.StringBuilder();
            index.AppendLine("Preset\tCategory\tPlay MIDI note\tHz\tOsc\tFilter\tAmp env\tNotes");
            int ok = 0, fail = 0;
            string[] files = Directory.GetFiles(inDir);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string f in files)
            {
                if (Array.IndexOf(exts, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                string name = Path.GetFileNameWithoutExtension(f);
                try
                {
                    AnalysisResult r = Analyzer.Analyze(Limit(AudioIO.Load(f)));
                    VitalExport.Save(r, Path.Combine(outDir, name + VitalExport.NoteSuffix(r) + ".vital"));
                    File.WriteAllText(Path.Combine(outDir, name + " - recipe.txt"),
                        (r.Recipe + "\n==== MEASUREMENTS ====\n" + RecipeWriter.MeasurementsText(r)).Replace("\r\n", "\n").Replace("\n", "\r\n"));
                    SynthPatch p = r.Patch;
                    index.AppendLine(string.Join("\t", new[] {
                        name, r.Category,
                        r.Pitched && r.F0 > 0 ? Math.Round(Dsp.FreqToMidi(r.F0)).ToString() : "-",
                        r.Pitched && r.F0 > 0 ? r.F0.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "-",
                        SynthPatch.WaveName(p.Osc1Wave, p.PulseWidth) + (p.UnisonVoices > 1 ? " x" + p.UnisonVoices : "") + (p.SubLevel > 0.05 ? " + sine" : "") + (p.NoiseLevel > 0.03 && p.Osc1Wave != Wave.Noise ? " + noise" : ""),
                        p.Filter == FilterMode.Off ? "open" : p.Filter + " " + Dsp.Hz(p.Cutoff),
                        "A " + Dsp.Ms(p.AA) + " D " + Dsp.Ms(p.AD) + " S " + Math.Round(p.AS * 100) + "% R " + Dsp.Ms(p.AR),
                        (r.IsChord ? "chord " + r.ChordName + "; " : "") + (r.Inharmonic ? "inharmonic; " : "") + (r.IsPhrase ? "phrase; " : "") + (r.Duration > 29.9 ? "first 30 s analysed" : "")
                    }));
                    Console.WriteLine("OK   " + name + "  ->  " + r.Category);
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("FAIL " + name + ": " + ex.Message);
                    index.AppendLine(name + "\tFAILED: " + ex.Message);
                    fail++;
                }
            }
            File.WriteAllText(Path.Combine(outDir, "_index.tsv"), index.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n"));
            Console.WriteLine(ok + " presets written, " + fail + " failed.");
            return fail > 0 ? 2 : 0;
        }

        public static AudioClip Limit(AudioClip c)
        {
            return c.Duration > 30 ? c.Slice(0, 30 * c.SampleRate) : c;
        }

        public static void Normalize(float[][] o, float target)
        {
            float pk = 1e-9f;
            foreach (float[] c in o) foreach (float v in c) pk = Math.Max(pk, Math.Abs(v));
            float g = target / pk;
            foreach (float[] c in o) for (int i = 0; i < c.Length; i++) c[i] *= g;
        }
    }
}
