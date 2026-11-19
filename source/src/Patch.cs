using System;
using System.Collections.Generic;

namespace SynthAnalyser
{
    public enum Wave { Sine, Triangle, Saw, Square, Pulse, Noise }
    public enum FilterMode { Off, LowPass, HighPass, BandPass }

    /// <summary>A generic subtractive synth patch. Used both for the written recipe and the preview synth.</summary>
    public class SynthPatch
    {
        // oscillators
        public Wave Osc1Wave = Wave.Saw;
        public double PulseWidth = 0.5;         // 0..1 (0.5 = square)
        public int UnisonVoices = 1;
        public double UnisonDetuneCents = 0;    // +/- spread of outer voices
        public double StereoWidth = 0;          // 0..1
        public double SubLevel = 0;             // sine sub oscillator level, 0..1.5 relative to osc 1
        public int SubOctave = -1;              // -1 = one octave down, 0 = same pitch (sine layer)
        public double NoiseLevel = 0;           // 0..1 relative to osc 1
        public double NoiseDecay = 0;           // 0 = sustained, else seconds (burst on attack)

        // filter
        public FilterMode Filter = FilterMode.Off;
        public int FilterSlope = 24;            // dB/oct
        public double Cutoff = 20000;           // Hz (base / sustain cutoff)
        public double Resonance = 0;            // 0..1
        public double FilterEnvOct = 0;         // octaves added at env peak (negative = inverted)
        public double FA = 0.001, FD = 0.3, FS = 0, FR = 0.3;
        public double KeyTrack = 0.5;           // 0..1
        public double HighPassHz = 0;           // extra 12 dB high-pass, 0 = off

        // amp envelope
        public double AA = 0.002, AD = 0.3, AS = 1.0, AR = 0.2;
        public double PunchDb = 0, PunchTime = 0.1;   // extra level at the start that falls away quickly

        // pitch
        public double PitchEnvSemis = 0, PitchEnvTime = 0.05;
        public double VibratoRate = 0, VibratoCents = 0, VibratoDelay = 0;
        public double Glide = 0;
        public bool Mono = false;

        // other modulation
        public double TremoloRate = 0, TremoloDepth = 0;      // depth 0..1
        public double CutoffLfoRate = 0, CutoffLfoOct = 0;

        // effects
        public double Drive = 0;                // 0..1
        public double ChorusMix = 0;            // 0..1
        public double ReverbMix = 0, ReverbTime = 0;
        public double DelayTime = 0, DelayFeedback = 0, DelayMix = 0;

        public SynthPatch Clone() { return (SynthPatch)MemberwiseClone(); }

        public static string WaveName(Wave w, double pw)
        {
            switch (w)
            {
                case Wave.Sine: return "Sine";
                case Wave.Triangle: return "Triangle";
                case Wave.Saw: return "Saw";
                case Wave.Square: return "Square";
                case Wave.Pulse: return "Pulse (" + Math.Round(pw * 100) + "% width)";
                default: return "Noise";
            }
        }
    }

    public class NoteEvent
    {
        public double Start, Duration, Freq, Velocity = 1.0;
    }

    public class Harmonic
    {
        public int K;
        public double Freq, Db;       // Db relative to strongest harmonic (band energy)
        public double PeakDb;         // peak level relative to the spectrum maximum (for display)
        public bool Present;
    }
}
