using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SynthAnalyser
{
    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(30, 31, 36);
        public static readonly Color Panel = Color.FromArgb(38, 40, 46);
        public static readonly Color Grid = Color.FromArgb(55, 58, 66);
        public static readonly Color Text = Color.FromArgb(228, 228, 232);
        public static readonly Color Dim = Color.FromArgb(140, 144, 155);
        public static readonly Color Wave = Color.FromArgb(79, 195, 247);
        public static readonly Color Sel = Color.FromArgb(255, 183, 77);
        public static readonly Color Harm = Color.FromArgb(129, 199, 132);
        public static readonly Color Model = Color.FromArgb(255, 138, 101);
        public static readonly Color Pitch = Color.FromArgb(206, 147, 216);
    }

    public class ViewBase : Control
    {
        public ViewBase()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Panel;
            ForeColor = Theme.Text;
        }
        protected static void Label(Graphics g, string s, Font f, Color c, float x, float y)
        {
            using (SolidBrush b = new SolidBrush(c)) g.DrawString(s, f, b, x, y);
        }
    }

    /// <summary>Waveform display with click-drag region selection.</summary>
    public class WaveformView : ViewBase
    {
        AudioClip clip;
        float[] mins, maxs;
        public double SelStart, SelEnd;         // seconds
        public List<double> Onsets = new List<double>();
        public double NoteStart = -1, NoteEnd = -1;   // analysed region, absolute seconds
        public double Playhead = -1;
        public event EventHandler SelectionChanged;
        bool dragging; double dragAnchor;

        public AudioClip Clip
        {
            get { return clip; }
            set { clip = value; mins = maxs = null; SelStart = 0; SelEnd = clip != null ? clip.Duration : 0; Onsets.Clear(); NoteStart = NoteEnd = -1; Invalidate(); }
        }

        double XToT(int x) { return clip == null ? 0 : Math.Max(0, Math.Min(clip.Duration, (x / (double)Math.Max(1, Width)) * clip.Duration)); }
        float TToX(double t) { return clip == null ? 0 : (float)(t / clip.Duration * Width); }

        void BuildPeaks()
        {
            int w = Math.Max(1, Width);
            mins = new float[w]; maxs = new float[w];
            float[] m = clip.Mono();
            double spp = m.Length / (double)w;
            for (int x = 0; x < w; x++)
            {
                int a = (int)(x * spp), b = Math.Min(m.Length, (int)((x + 1) * spp) + 1);
                float lo = 0, hi = 0;
                for (int i = a; i < b; i++) { if (m[i] < lo) lo = m[i]; if (m[i] > hi) hi = m[i]; }
                mins[x] = lo; maxs[x] = hi;
            }
        }

        protected override void OnResize(EventArgs e) { mins = maxs = null; base.OnResize(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            int h = Height, mid = h / 2;
            if (clip == null)
            {
                using (Font f = new Font(Font.FontFamily, 12f))
                {
                    StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    using (SolidBrush b = new SolidBrush(Theme.Dim))
                        g.DrawString("Drop an audio file here, or click 'Open sample...'\nWAV / AIFF (MP3, FLAC, OGG with ffmpeg)", f, b, ClientRectangle, sf);
                }
                return;
            }
            if (mins == null || mins.Length != Width) BuildPeaks();
            // analysed region
            if (NoteEnd > NoteStart && NoteStart >= 0)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(28, Theme.Harm)))
                    g.FillRectangle(b, TToX(NoteStart), 0, Math.Max(1, TToX(NoteEnd) - TToX(NoteStart)), h);
            // selection
            float sx = TToX(SelStart), ex = TToX(SelEnd);
            bool full = SelStart <= 0.0001 && SelEnd >= clip.Duration - 0.0001;
            if (!full)
                using (SolidBrush b = new SolidBrush(Color.FromArgb(38, Theme.Sel)))
                    g.FillRectangle(b, sx, 0, Math.Max(1, ex - sx), h);
            using (Pen p = new Pen(Theme.Grid)) g.DrawLine(p, 0, mid, Width, mid);
            using (Pen p = new Pen(Theme.Wave))
            {
                for (int x = 0; x < mins.Length; x++)
                {
                    float y1 = mid - maxs[x] * (mid - 4), y2 = mid - mins[x] * (mid - 4);
                    g.DrawLine(p, x, y1, x, Math.Max(y1 + 1, y2));
                }
            }
            using (Pen p = new Pen(Theme.Sel, 1.5f)) { g.DrawLine(p, sx, 0, sx, h); g.DrawLine(p, ex, 0, ex, h); }
            using (Pen p = new Pen(Color.FromArgb(160, Theme.Harm)) { DashStyle = DashStyle.Dot })
                foreach (double o in Onsets) g.DrawLine(p, TToX(o), 0, TToX(o), h);
            if (Playhead >= 0)
                using (Pen p = new Pen(Color.White)) g.DrawLine(p, TToX(Playhead), 0, TToX(Playhead), h);
            // time ruler
            using (Font f = new Font(Font.FontFamily, 8f))
            {
                double step = NiceStep(clip.Duration / Math.Max(1, Width / 90.0));
                for (double t = 0; t <= clip.Duration; t += step)
                    Label(g, t.ToString(step < 0.1 ? "0.00" : "0.0") + "s", f, Theme.Dim, TToX(t) + 2, h - 14);
                Label(g, "Selection: " + SelStart.ToString("0.000") + " - " + SelEnd.ToString("0.000") + " s  (" + (SelEnd - SelStart).ToString("0.000") + " s)", f, Theme.Sel, 4, 2);
            }
        }

        static double NiceStep(double raw)
        {
            double[] s = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 30, 60 };
            foreach (double v in s) if (v >= raw) return v;
            return 120;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (clip == null || e.Button != MouseButtons.Left) return;
            dragging = true; dragAnchor = XToT(e.X);
            SelStart = SelEnd = dragAnchor; Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            double t = XToT(e.X);
            SelStart = Math.Min(t, dragAnchor); SelEnd = Math.Max(t, dragAnchor);
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            if (SelEnd - SelStart < 0.02) { SelStart = 0; SelEnd = clip.Duration; }
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (clip == null) return;
            SelStart = 0; SelEnd = clip.Duration; Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }
    }

    /// <summary>Log-frequency spectrum with detected harmonics and the fitted oscillator+filter model.</summary>
    public class SpectrumView : ViewBase
    {
        public AnalysisResult Result;
        const double FMin = 20, FMax = 20000, DbMin = -96;

        float FX(double f, RectangleF r) { return r.Left + (float)(Math.Log(f / FMin) / Math.Log(FMax / FMin) * r.Width); }
        float DY(double db, RectangleF r) { return r.Top + (float)(Math.Max(DbMin, Math.Min(0, db)) / DbMin * r.Height); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(40, 10, Width - 50, Height - 34);
            using (Font f = new Font(Font.FontFamily, 8f))
            using (Pen gp = new Pen(Theme.Grid))
            {
                foreach (double fr in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 })
                {
                    float x = FX(fr, r);
                    g.DrawLine(gp, x, r.Top, x, r.Bottom);
                    Label(g, fr >= 1000 ? (fr / 1000) + "k" : fr.ToString(), f, Theme.Dim, x - 8, r.Bottom + 3);
                }
                for (int db = 0; db >= DbMin; db -= 12)
                {
                    float y = DY(db, r);
                    g.DrawLine(gp, r.Left, y, r.Right, y);
                    Label(g, db.ToString(), f, Theme.Dim, 4, y - 6);
                }
                if (Result == null || Result.SpecDb == null)
                {
                    Label(g, "Spectrum of the analysed note appears here", f, Theme.Dim, r.Left + 10, r.Top + 10);
                    return;
                }
                // spectrum
                List<PointF> pts = new List<PointF>();
                double[] s = Result.SpecDb;
                int lastX = -1; float minY = float.MaxValue;
                for (int b = 1; b < s.Length; b++)
                {
                    double fr = b * Result.SpecBinHz;
                    if (fr < FMin || fr > FMax) continue;
                    int x = (int)FX(fr, r);
                    float y = DY(s[b], r);
                    if (x != lastX)
                    {
                        if (lastX >= 0) pts.Add(new PointF(lastX, minY));
                        lastX = x; minY = y;
                    }
                    else minY = Math.Min(minY, y);
                }
                if (pts.Count > 1)
                    using (Pen p = new Pen(Theme.Wave, 1.2f)) g.DrawLines(p, pts.ToArray());

                // model: waveform template + filter, drawn at the harmonic positions
                SynthPatch pt = Result.Patch;
                if (Result.Harmonics.Count > 0 && pt.Osc1Wave != Wave.Noise)
                {
                    double f0 = Result.F0;
                    List<PointF> mp = new List<PointF>();
                    // align model to data using the fit gain, then shift from band-energy to peak levels for display
                    List<double> shifts = new List<double>();
                    foreach (Harmonic h in Result.Harmonics) if (h.Present) shifts.Add(h.PeakDb - h.Db);
                    double disp = shifts.Count > 0 ? Dsp.Median(shifts) : 0;
                    double off = Result.ModelGainDb + disp;
                    // harmonic Db values are relative to the strongest harmonic; spectrum is relative to its max ~ same
                    foreach (Harmonic h in Result.Harmonics)
                    {
                        double fr = h.K * f0;
                        double v = Analyzer.TemplateDb(pt.Osc1Wave, pt.PulseWidth, h.K) + off +
                                   FilterModel.ResponseDb(pt.Filter, pt.FilterSlope, pt.Cutoff, pt.Resonance, fr);
                        if (v > DbMin) mp.Add(new PointF(FX(fr, r), DY(v, r)));
                        if (h.Present)
                            using (SolidBrush b = new SolidBrush(Theme.Harm)) g.FillEllipse(b, FX(h.Freq, r) - 3, DY(h.PeakDb, r) - 3, 6, 6);
                    }
                    if (mp.Count > 1)
                        using (Pen p = new Pen(Theme.Model, 1.5f) { DashStyle = DashStyle.Dash }) g.DrawLines(p, mp.ToArray());
                }
                if (pt.Filter != FilterMode.Off && pt.Cutoff < 20000)
                {
                    float x = FX(pt.Cutoff, r);
                    using (Pen p = new Pen(Theme.Sel) { DashStyle = DashStyle.Dot }) g.DrawLine(p, x, r.Top, x, r.Bottom);
                    Label(g, "cutoff " + Dsp.Hz(pt.Cutoff), f, Theme.Sel, x + 3, r.Top + 2);
                }
                Label(g, "— spectrum    ● harmonics    - - waveform + filter model", f, Theme.Dim, r.Left + 4, r.Bottom - 16);
            }
        }
    }

    /// <summary>Amplitude envelope (dB) and pitch track over time.</summary>
    public class EnvelopeView : ViewBase
    {
        public AnalysisResult Result;
        public double Offset;   // absolute time of clip start

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Panel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF r = new RectangleF(40, 10, Width - 90, Height - 34);
            using (Font f = new Font(Font.FontFamily, 8f))
            using (Pen gp = new Pen(Theme.Grid))
            {
                for (int db = 0; db >= -60; db -= 12)
                {
                    float y = r.Top + (-db / 60f) * r.Height;
                    g.DrawLine(gp, r.Left, y, r.Right, y);
                    Label(g, db + "dB", f, Theme.Dim, 2, y - 6);
                }
                if (Result == null || Result.Env == null) { Label(g, "Envelope and pitch appear here", f, Theme.Dim, r.Left + 10, r.Top + 10); return; }
                double dur = Result.Duration;
                Func<double, float> TX = t => r.Left + (float)(t / dur * r.Width);
                // envelope
                List<PointF> pts = new List<PointF>();
                for (int i = 0; i < Result.Env.Length; i++)
                {
                    double db = Math.Max(-60, Dsp.ToDb(Result.Env[i]));
                    pts.Add(new PointF(TX(i / Result.EnvRate), r.Top + (float)(-db / 60 * r.Height)));
                }
                if (pts.Count > 1) using (Pen p = new Pen(Theme.Wave, 1.3f)) g.DrawLines(p, pts.ToArray());
                // pitch (semitones relative to the detected note; range adapts to the phrase)
                if (Result.F0 > 0 && Result.PitchTrack != null)
                {
                    List<double> sts = new List<double>();
                    foreach (double pf in Result.PitchTrack) if (pf > 0) sts.Add(12 * Math.Log(pf / Result.F0, 2));
                    double lo = sts.Count > 0 ? Dsp.Percentile(sts, 0.02) : -12, hi = sts.Count > 0 ? Dsp.Percentile(sts, 0.98) : 12;
                    double mid = Math.Round((lo + hi) / 2), half = Math.Max(12, Math.Ceiling((hi - lo) / 2 / 6) * 6 + 3);
                    double top = mid + half, span = 2 * half;
                    using (SolidBrush b = new SolidBrush(Theme.Pitch))
                        for (int i = 0; i < Result.PitchTrack.Length; i++)
                        {
                            double pf = Result.PitchTrack[i];
                            if (pf <= 0) continue;
                            double st = 12 * Math.Log(pf / Result.F0, 2);
                            if (st > top || st < top - span) continue;
                            float y = r.Top + (float)((top - st) / span * r.Height);
                            g.FillRectangle(b, TX(i / Analyzer.PitchRate) - 1, y - 1, 2.5f, 2.5f);
                        }
                    int stepSt = span > 36 ? 12 : 6;
                    for (int st = (int)Math.Ceiling((top - span) / stepSt) * stepSt; st <= top; st += stepSt)
                        Label(g, (st > 0 ? "+" : "") + st + "st", f, Theme.Pitch, r.Right + 4, r.Top + (float)((top - st) / span * r.Height) - 6);
                }
                using (Pen p = new Pen(Color.FromArgb(160, Theme.Harm)) { DashStyle = DashStyle.Dot })
                    foreach (double o in Result.Onsets) g.DrawLine(p, TX(o), r.Top, TX(o), r.Bottom);
                Label(g, "— amplitude (dB)    ● pitch (semitones vs. detected note)", f, Theme.Dim, r.Left + 4, r.Bottom + 4);
            }
        }
    }
}
