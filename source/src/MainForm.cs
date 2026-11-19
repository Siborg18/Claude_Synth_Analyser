using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Media;
using System.Threading;
using System.Windows.Forms;

namespace SynthAnalyser
{
    public class MainForm : Form
    {
        AudioClip clip;
        AnalysisResult result;
        AudioClip analysedSlice;
        double analysedOffset;
        float[][] preview;
        SoundPlayer player;
        Stopwatch playClock = new Stopwatch();
        double playFrom, playLen;
        System.Windows.Forms.Timer playTimer;
        string pendingFile;
        int busy;

        WaveformView wave;
        SpectrumView spectrum;
        EnvelopeView envView;
        RichTextBox recipeBox, measBox;
        Label info;
        ToolStripStatusLabel status;
        Button bOpen, bPlayOrig, bPlayPrev, bStop, bAnalyse, bCopy, bSave, bExport, bCompare, bVital;
        TabControl rightTabs;
        TabPage tpCompare;
        RichTextBox compareBox;

        const double MaxAnalyse = 30.0;

        public MainForm()
        {
            Text = "Synth Analyser";
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            float s = DpiScale();
            Size = new Size((int)(1280 * s), (int)(860 * s));
            MinimumSize = new Size((int)(900 * s), (int)(600 * s));
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            KeyPreview = true;

            // ---------- toolbar
            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top;
            bar.AutoSize = true;
            bar.Padding = new Padding((int)(6 * s));
            bar.BackColor = Theme.Bg;
            bOpen = Btn("Open sample...", OnOpen);
            bPlayOrig = Btn("▶ Play original", (o, e) => PlayOriginal());
            bPlayPrev = Btn("▶ Play synth preview", (o, e) => PlayPreview());
            bStop = Btn("■ Stop", (o, e) => StopPlayback());
            bAnalyse = Btn("Analyse selection", (o, e) => StartAnalysis());
            bCopy = Btn("Copy recipe", (o, e) => { if (result != null) Clipboard.SetText(result.Recipe.Replace("\n", Environment.NewLine)); SetStatus("Recipe copied to clipboard."); });
            bSave = Btn("Save recipe...", OnSaveRecipe);
            bExport = Btn("Export preview WAV...", OnExportWav);
            bCompare = Btn("Compare my version...", OnCompare);
            bVital = Btn("Save Vital preset...", OnSaveVital);
            bar.Controls.AddRange(new Control[] { bOpen, bPlayOrig, bPlayPrev, bStop, bAnalyse, bCopy, bSave, bVital, bExport, bCompare });

            info = new Label();
            info.Dock = DockStyle.Top;
            info.Height = (int)(24 * s);
            info.Padding = new Padding((int)(10 * s), (int)(4 * s), 0, 0);
            info.ForeColor = Theme.Dim;
            info.Text = "Open or drop a sample (WAV/AIFF). Drag on the waveform to select one note or a short phrase, then 'Analyse selection'.";

            wave = new WaveformView();
            wave.Dock = DockStyle.Top;
            wave.Height = (int)(170 * s);
            wave.SelectionChanged += (o, e) => StartAnalysis();

            // ---------- lower area
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.BackColor = Theme.Bg;
            split.SplitterWidth = (int)(6 * s);

            TabControl left = new TabControl();
            left.Dock = DockStyle.Fill;
            TabPage tpSpec = new TabPage("Spectrum");
            TabPage tpEnv = new TabPage("Envelope && pitch");
            spectrum = new SpectrumView { Dock = DockStyle.Fill };
            envView = new EnvelopeView { Dock = DockStyle.Fill };
            tpSpec.Controls.Add(spectrum); tpEnv.Controls.Add(envView);
            tpSpec.BackColor = tpEnv.BackColor = Theme.Panel;
            left.TabPages.Add(tpSpec); left.TabPages.Add(tpEnv);
            split.Panel1.Controls.Add(left);

            TabControl right = new TabControl();
            right.Dock = DockStyle.Fill;
            TabPage tpRec = new TabPage("Synth recipe");
            TabPage tpMeas = new TabPage("Measurements");
            recipeBox = MakeText(); measBox = MakeText();
            recipeBox.WordWrap = true;
            tpRec.Controls.Add(recipeBox); tpMeas.Controls.Add(measBox);
            right.TabPages.Add(tpRec); right.TabPages.Add(tpMeas);
            tpCompare = new TabPage("Comparison");
            compareBox = MakeText(); compareBox.WordWrap = true;
            compareBox.Text = "Recreated the sound in your own synth? Record/bounce the same note to a WAV file, then click 'Compare my version...'.\n\n" +
                              "The app analyses your recording the same way and lists what differs most (pitch/octave, brightness, envelope, LFO, detune...).";
            tpCompare.Controls.Add(compareBox);
            right.TabPages.Add(tpCompare);
            rightTabs = right;
            split.Panel2.Controls.Add(right);

            StatusStrip ss = new StatusStrip();
            ss.BackColor = Theme.Bg;
            status = new ToolStripStatusLabel("Ready");
            status.ForeColor = Theme.Dim;
            ss.Items.Add(status);

            Controls.Add(split);
            Controls.Add(wave);
            Controls.Add(info);
            Controls.Add(bar);
            Controls.Add(ss);

            Load += (o, e) =>
            {
                split.SplitterDistance = (int)(split.Width * 0.5);
                if (pendingFile != null) LoadFile(pendingFile);
            };
            DragEnter += (o, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += (o, e) =>
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0) LoadFile(files[0]);
            };
            KeyDown += (o, e) =>
            {
                if (e.KeyCode == Keys.Space && !(ActiveControl is RichTextBox)) { PlayOriginal(); e.Handled = true; }
                else if (e.KeyCode == Keys.P && !(ActiveControl is RichTextBox)) { PlayPreview(); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) StopPlayback();
                else if (e.Control && e.KeyCode == Keys.O) OnOpen(null, null);
            };
            playTimer = new System.Windows.Forms.Timer { Interval = 30 };
            playTimer.Tick += (o, e) =>
            {
                double t = playClock.Elapsed.TotalSeconds;
                if (t > playLen) { wave.Playhead = -1; playTimer.Stop(); }
                else wave.Playhead = playFrom + t;
                wave.Invalidate();
            };
            UpdateButtons();
        }

        float DpiScale()
        {
            using (Graphics g = CreateGraphics()) return Math.Max(1f, g.DpiX / 96f);
        }

        Button Btn(string text, EventHandler h)
        {
            Button b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Theme.Grid;
            b.BackColor = Theme.Panel;
            b.ForeColor = Theme.Text;
            b.Padding = new Padding(6, 3, 6, 3);
            b.Margin = new Padding(3);
            b.Click += h;
            return b;
        }

        RichTextBox MakeText()
        {
            RichTextBox t = new RichTextBox();
            t.Dock = DockStyle.Fill;
            t.ReadOnly = true;
            t.BorderStyle = BorderStyle.None;
            t.BackColor = Theme.Panel;
            t.ForeColor = Theme.Text;
            t.Font = new Font("Consolas", 10f);
            t.WordWrap = false;
            return t;
        }

        void SetStatus(string s) { status.Text = s; }

        void UpdateButtons()
        {
            bool has = clip != null, idle = busy == 0;
            bPlayOrig.Enabled = has;
            bAnalyse.Enabled = has && idle;
            bPlayPrev.Enabled = bExport.Enabled = preview != null;
            bCopy.Enabled = bSave.Enabled = bVital.Enabled = result != null;
            bCompare.Enabled = result != null && idle;
            bStop.Enabled = has;
        }

        public void LoadFileDeferred(string path) { pendingFile = path; }

        void OnOpen(object sender, EventArgs e)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Audio files|*.wav;*.wave;*.aif;*.aiff;*.mp3;*.flac;*.ogg;*.m4a|All files|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) LoadFile(d.FileName);
            }
        }

        void LoadFile(string path)
        {
            StopPlayback();
            try
            {
                SetStatus("Loading " + Path.GetFileName(path) + " ...");
                Cursor = Cursors.WaitCursor;
                clip = AudioIO.Load(path);
                result = null; preview = null;
                wave.Clip = clip;
                if (clip.Duration > MaxAnalyse)
                {
                    wave.SelEnd = Math.Min(clip.Duration, 8.0);
                    SetStatus("Long file: analysing the first 8 s. Drag on the waveform to pick a note or phrase.");
                }
                Text = "Synth Analyser - " + Path.GetFileName(path);
                info.Text = Path.GetFileName(path) + "   |   " + clip.SampleRate + " Hz, " + (clip.ChannelCount >= 2 ? "stereo" : "mono") +
                            ", " + clip.Duration.ToString("0.00") + " s   |   drag to select a region, double-click for all   |   Space = play original, P = play preview";
                StartAnalysis();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not open file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                SetStatus("Could not open file.");
            }
            finally { Cursor = Cursors.Default; UpdateButtons(); }
        }

        void StartAnalysis()
        {
            if (clip == null || busy > 0) return;
            double a = wave.SelStart, b = wave.SelEnd;
            if (b - a > MaxAnalyse) { b = a + MaxAnalyse; wave.SelEnd = b; wave.Invalidate(); SetStatus("Selection limited to " + MaxAnalyse + " s."); }
            AudioClip slice = clip.Slice((int)(a * clip.SampleRate), (int)(b * clip.SampleRate));
            Interlocked.Increment(ref busy);
            UpdateButtons();
            SetStatus("Analysing " + (b - a).ToString("0.00") + " s ...");
            Cursor = Cursors.AppStarting;
            Thread t = new Thread(() =>
            {
                AnalysisResult r = null; float[][] pv = null; Exception err = null;
                try
                {
                    r = Analyzer.Analyze(slice);
                    pv = PreviewSynth.Render(r.Patch, r.Notes, slice.SampleRate, slice.Duration + Math.Min(4.0, Math.Max(0.5, r.Patch.AR + r.Patch.ReverbTime * 0.5)), r.F0);
                    // match the preview loudness to the original selection's peak
                    float pk = 0;
                    foreach (float[] c in slice.Channels) foreach (float v in c) pk = Math.Max(pk, Math.Abs(v));
                    Program.Normalize(pv, Math.Max(0.05f, Math.Min(0.95f, pk)));
                }
                catch (Exception ex) { err = ex; }
                BeginInvoke((Action)(() =>
                {
                    Interlocked.Decrement(ref busy);
                    Cursor = Cursors.Default;
                    if (err != null)
                    {
                        SetStatus("Analysis failed: " + err.Message);
                        recipeBox.Text = "Analysis failed:\n" + err.Message;
                    }
                    else ShowResult(r, pv, slice, a);
                    UpdateButtons();
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        void ShowResult(AnalysisResult r, float[][] pv, AudioClip slice, double offset)
        {
            result = r; preview = pv; analysedSlice = slice; analysedOffset = offset;
            recipeBox.Text = r.Recipe;
            measBox.Text = RecipeWriter.MeasurementsText(r);
            // highlight headings
            foreach (string head in new[] { "SYNTH RECIPE", "1. OSCILLATORS", "2. FILTER", "3. AMP ENVELOPE", "4. PITCH & MODULATION", "4. EFFECTS", "5. EFFECTS", "TIPS" })
            {
                int i = recipeBox.Text.IndexOf(head, StringComparison.Ordinal);
                if (i < 0) continue;
                int end = recipeBox.Text.IndexOf('\n', i);
                recipeBox.Select(i, (end < 0 ? recipeBox.Text.Length : end) - i);
                recipeBox.SelectionColor = head == "SYNTH RECIPE" ? Theme.Sel : Theme.Wave;
                recipeBox.SelectionFont = new Font(recipeBox.Font, FontStyle.Bold);
            }
            recipeBox.Select(0, 0);
            wave.Onsets.Clear();
            foreach (double o in r.Onsets) wave.Onsets.Add(o + offset);
            wave.NoteStart = r.NoteStart + offset; wave.NoteEnd = r.NoteEnd + offset;
            wave.Invalidate();
            spectrum.Result = r; spectrum.Invalidate();
            envView.Result = r; envView.Invalidate();
            SetStatus("Done: " + r.Category + (r.IsPhrase ? " - phrase with " + r.Onsets.Count + " notes" : "") + ". Green area = note used for the timbre analysis.");
        }

        // ------------------------------------------------------------ playback
        void Play(float[][] data, int rate, double from)
        {
            StopPlayback();
            byte[] wav = AudioIO.ToWavBytes(data, rate);
            player = new SoundPlayer(new MemoryStream(wav));
            player.Play();
            playFrom = from; playLen = data[0].Length / (double)rate;
            playClock.Restart();
            playTimer.Start();
        }

        void PlayOriginal()
        {
            if (clip == null) return;
            AudioClip s = clip.Slice((int)(wave.SelStart * clip.SampleRate), (int)(wave.SelEnd * clip.SampleRate));
            Play(s.Channels, s.SampleRate, wave.SelStart);
        }

        void PlayPreview()
        {
            if (preview == null) return;
            Play(preview, analysedSlice.SampleRate, analysedOffset);
        }

        void StopPlayback()
        {
            if (player != null) { try { player.Stop(); player.Dispose(); } catch { } player = null; }
            if (playTimer != null) playTimer.Stop();
            if (wave != null) { wave.Playhead = -1; wave.Invalidate(); }
        }

        // ------------------------------------------------------------ compare
        void OnCompare(object sender, EventArgs e)
        {
            if (result == null || busy > 0) return;
            string path;
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Open YOUR recording of the recreated sound";
                d.Filter = "Audio files|*.wav;*.wave;*.aif;*.aiff;*.mp3;*.flac;*.ogg;*.m4a|All files|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                path = d.FileName;
            }
            AnalysisResult orig = result;
            Interlocked.Increment(ref busy);
            UpdateButtons();
            SetStatus("Analysing your version ...");
            Cursor = Cursors.AppStarting;
            Thread t = new Thread(() =>
            {
                string text; Exception err = null;
                try
                {
                    AnalysisResult mine = Analyzer.Analyze(Program.Limit(AudioIO.Load(path)));
                    text = CompareWriter.Write(orig, mine);
                }
                catch (Exception ex) { err = ex; text = null; }
                BeginInvoke((Action)(() =>
                {
                    Interlocked.Decrement(ref busy);
                    Cursor = Cursors.Default;
                    if (err != null) { compareBox.Text = "Comparison failed:\n" + err.Message; SetStatus("Comparison failed."); }
                    else
                    {
                        compareBox.Text = text;
                        foreach (string head in new[] { "COMPARISON", "FIX FIRST:", "DETAILS" })
                        {
                            int i = compareBox.Text.IndexOf(head, StringComparison.Ordinal);
                            if (i < 0) continue;
                            int end = compareBox.Text.IndexOf('\n', i);
                            compareBox.Select(i, (end < 0 ? compareBox.Text.Length : end) - i);
                            compareBox.SelectionColor = head == "FIX FIRST:" ? Theme.Model : Theme.Sel;
                            compareBox.SelectionFont = new Font(compareBox.Font, FontStyle.Bold);
                        }
                        compareBox.Select(0, 0);
                        SetStatus("Compared with " + Path.GetFileName(path) + ".");
                    }
                    rightTabs.SelectedTab = tpCompare;
                    UpdateButtons();
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        // ------------------------------------------------------------ export
        void OnSaveRecipe(object sender, EventArgs e)
        {
            if (result == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "Text file|*.txt";
                d.FileName = (clip != null ? Path.GetFileNameWithoutExtension(clip.SourcePath) : "sound") + " - synth recipe.txt";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string text = result.Recipe + Environment.NewLine + "==== MEASUREMENTS ====" + Environment.NewLine + RecipeWriter.MeasurementsText(result);
                File.WriteAllText(d.FileName, text.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
                SetStatus("Saved " + d.FileName);
            }
        }

        void OnSaveVital(object sender, EventArgs e)
        {
            if (result == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Title = "Save as Vital preset";
                d.Filter = "Vital preset|*.vital";
                d.FileName = (clip != null ? Path.GetFileNameWithoutExtension(clip.SourcePath) : "Analysed sound") + VitalExport.NoteSuffix(result) + ".vital";
                string vitalDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Vital", "User", "Presets");
                if (Directory.Exists(vitalDir)) d.InitialDirectory = vitalDir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    VitalExport.Save(result, d.FileName);
                    SetStatus("Saved Vital preset " + d.FileName + (result.Pitched && result.F0 > 0 ? "  -  play MIDI note " + Math.Round(Dsp.FreqToMidi(result.F0)) : ""));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Could not save Vital preset", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        void OnExportWav(object sender, EventArgs e)
        {
            if (preview == null) return;
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "WAV file|*.wav";
                d.FileName = (clip != null ? Path.GetFileNameWithoutExtension(clip.SourcePath) : "sound") + " - synth preview.wav";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllBytes(d.FileName, AudioIO.ToWavBytes(preview, analysedSlice.SampleRate));
                SetStatus("Saved " + d.FileName);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopPlayback();
            base.OnFormClosing(e);
        }
    }
}
