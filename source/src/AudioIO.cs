using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SynthAnalyser
{
    public class AudioClip
    {
        public float[][] Channels;   // [channel][sample]
        public int SampleRate;
        public string SourcePath;

        public int Length { get { return Channels.Length > 0 ? Channels[0].Length : 0; } }
        public int ChannelCount { get { return Channels.Length; } }
        public double Duration { get { return SampleRate > 0 ? (double)Length / SampleRate : 0; } }

        public float[] Mono()
        {
            int n = Length;
            float[] m = new float[n];
            int c = Channels.Length;
            for (int ch = 0; ch < c; ch++)
            {
                float[] src = Channels[ch];
                for (int i = 0; i < n; i++) m[i] += src[i];
            }
            if (c > 1) for (int i = 0; i < n; i++) m[i] /= c;
            return m;
        }

        public AudioClip Slice(int start, int end)
        {
            start = Math.Max(0, start); end = Math.Min(Length, end);
            if (end <= start) end = Math.Min(Length, start + 1);
            AudioClip a = new AudioClip();
            a.SampleRate = SampleRate;
            a.SourcePath = SourcePath;
            a.Channels = new float[Channels.Length][];
            for (int ch = 0; ch < Channels.Length; ch++)
            {
                a.Channels[ch] = new float[end - start];
                Array.Copy(Channels[ch], start, a.Channels[ch], 0, end - start);
            }
            return a;
        }
    }

    public static class AudioIO
    {
        public static AudioClip Load(string path)
        {
            byte[] head = new byte[12];
            using (FileStream fs = File.OpenRead(path))
            {
                fs.Read(head, 0, 12);
            }
            string id = Encoding.ASCII.GetString(head, 0, 4);
            string form = Encoding.ASCII.GetString(head, 8, 4);
            AudioClip clip = null;
            if ((id == "RIFF" || id == "RF64") && form == "WAVE")
            {
                try { clip = ReadWav(File.ReadAllBytes(path)); }
                catch (NotSupportedException) { clip = null; }
            }
            else if (id == "FORM" && (form == "AIFF" || form == "AIFC"))
            {
                try { clip = ReadAiff(File.ReadAllBytes(path)); }
                catch (NotSupportedException) { clip = null; }
            }
            if (clip == null) clip = LoadViaFfmpeg(path);
            clip.SourcePath = path;
            return clip;
        }

        // ------------------------------------------------------------ WAV
        public static AudioClip ReadWav(byte[] d)
        {
            int pos = 12;
            int fmtTag = 0, channels = 0, rate = 0, bits = 0, blockAlign = 0;
            int dataOff = -1; long dataLen = 0;
            while (pos + 8 <= d.Length)
            {
                string cid = Encoding.ASCII.GetString(d, pos, 4);
                long clen = BitConverter.ToUInt32(d, pos + 4);
                int body = pos + 8;
                if (cid == "fmt ")
                {
                    fmtTag = BitConverter.ToUInt16(d, body);
                    channels = BitConverter.ToUInt16(d, body + 2);
                    rate = BitConverter.ToInt32(d, body + 4);
                    blockAlign = BitConverter.ToUInt16(d, body + 12);
                    bits = BitConverter.ToUInt16(d, body + 14);
                    if (fmtTag == 0xFFFE && clen >= 26)
                        fmtTag = BitConverter.ToUInt16(d, body + 24); // sub-format GUID first 2 bytes
                }
                else if (cid == "data")
                {
                    dataOff = body;
                    dataLen = clen;
                    if (dataLen == 0xFFFFFFFF || body + dataLen > d.Length) dataLen = d.Length - body;
                    break;
                }
                pos = body + (int)clen + (int)(clen & 1);
            }
            if (dataOff < 0 || channels <= 0) throw new InvalidDataException("WAV file has no audio data.");
            if (fmtTag != 1 && fmtTag != 3) throw new NotSupportedException("Compressed WAV");
            int bps = bits / 8;
            if (blockAlign <= 0) blockAlign = bps * channels;
            int frames = (int)(dataLen / blockAlign);
            AudioClip c = new AudioClip();
            c.SampleRate = rate;
            c.Channels = new float[channels][];
            for (int ch = 0; ch < channels; ch++) c.Channels[ch] = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                int fo = dataOff + f * blockAlign;
                for (int ch = 0; ch < channels; ch++)
                {
                    int o = fo + ch * bps;
                    float v;
                    if (fmtTag == 3)
                        v = bits == 64 ? (float)BitConverter.ToDouble(d, o) : BitConverter.ToSingle(d, o);
                    else if (bits == 8) v = (d[o] - 128) / 128f;
                    else if (bits == 16) v = BitConverter.ToInt16(d, o) / 32768f;
                    else if (bits == 24) v = ((d[o] << 8 | d[o + 1] << 16 | d[o + 2] << 24) >> 8) / 8388608f;
                    else if (bits == 32) v = BitConverter.ToInt32(d, o) / 2147483648f;
                    else throw new NotSupportedException("Bit depth " + bits);
                    c.Channels[ch][f] = v;
                }
            }
            return c;
        }

        // ----------------------------------------------------------- AIFF
        private static int BE16(byte[] d, int o) { return (short)(d[o] << 8 | d[o + 1]); }
        private static int BE32(byte[] d, int o) { return d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]; }
        private static double Ext80(byte[] d, int o)
        {
            int exp = ((d[o] & 0x7F) << 8) | d[o + 1];
            ulong mant = 0;
            for (int i = 0; i < 8; i++) mant = (mant << 8) | d[o + 2 + i];
            if (exp == 0 && mant == 0) return 0;
            double v = mant * Math.Pow(2, exp - 16383 - 63);
            return (d[o] & 0x80) != 0 ? -v : v;
        }

        public static AudioClip ReadAiff(byte[] d)
        {
            bool aifc = Encoding.ASCII.GetString(d, 8, 4) == "AIFC";
            int pos = 12, channels = 0, frames = 0, bits = 16; double rate = 44100;
            int ssnd = -1; string comp = "NONE";
            while (pos + 8 <= d.Length)
            {
                string cid = Encoding.ASCII.GetString(d, pos, 4);
                int clen = BE32(d, pos + 4);
                int body = pos + 8;
                if (cid == "COMM")
                {
                    channels = BE16(d, body);
                    frames = BE32(d, body + 2);
                    bits = BE16(d, body + 6);
                    rate = Ext80(d, body + 8);
                    if (aifc && clen >= 22) comp = Encoding.ASCII.GetString(d, body + 18, 4);
                }
                else if (cid == "SSND")
                {
                    ssnd = body + 8 + BE32(d, body);
                }
                pos = body + clen + (clen & 1);
            }
            if (ssnd < 0 || channels <= 0) throw new InvalidDataException("AIFF has no audio data.");
            bool le = comp == "sowt";
            if (comp != "NONE" && comp != "sowt") throw new NotSupportedException("Compressed AIFF");
            int bps = (bits + 7) / 8;
            frames = Math.Min(frames, (d.Length - ssnd) / (bps * channels));
            AudioClip c = new AudioClip();
            c.SampleRate = (int)Math.Round(rate);
            c.Channels = new float[channels][];
            for (int ch = 0; ch < channels; ch++) c.Channels[ch] = new float[frames];
            for (int f = 0; f < frames; f++)
                for (int ch = 0; ch < channels; ch++)
                {
                    int o = ssnd + (f * channels + ch) * bps;
                    int s = 0;
                    if (le) { for (int b = bps - 1; b >= 0; b--) s = (s << 8) | d[o + b]; }
                    else { for (int b = 0; b < bps; b++) s = (s << 8) | d[o + b]; }
                    s <<= (32 - bps * 8);
                    c.Channels[ch][f] = s / 2147483648f;
                }
            return c;
        }

        // --------------------------------------------------------- ffmpeg
        public static string FindFfmpeg()
        {
            string[] names = { "ffmpeg.exe", "ffmpeg" };
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string n in names)
            {
                string p = Path.Combine(appDir, n);
                if (File.Exists(p)) return p;
            }
            string envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in envPath.Split(Path.PathSeparator))
            {
                foreach (string n in names)
                {
                    try
                    {
                        string p = Path.Combine(dir.Trim().Trim('"'), n);
                        if (File.Exists(p)) return p;
                    }
                    catch { }
                }
            }
            return null;
        }

        private static AudioClip LoadViaFfmpeg(string path)
        {
            string ff = FindFfmpeg();
            if (ff == null)
                throw new NotSupportedException(
                    "This file format is not supported natively (WAV and AIFF are).\n\n" +
                    "To open MP3, FLAC, OGG, M4A etc., place ffmpeg.exe next to SynthAnalyser.exe " +
                    "or install ffmpeg on your PATH.");
            string tmp = Path.Combine(Path.GetTempPath(), "synthanalyser_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(ff,
                    "-hide_banner -loglevel error -y -i \"" + path + "\" -vn -acodec pcm_f32le -f wav \"" + tmp + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(120000);
                    if (!File.Exists(tmp)) throw new InvalidDataException("ffmpeg could not decode the file:\n" + err);
                }
                return ReadWav(File.ReadAllBytes(tmp));
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // ---------------------------------------------------------- write
        public static byte[] ToWavBytes(float[][] ch, int rate)
        {
            int nch = ch.Length, n = ch[0].Length;
            int dataLen = n * nch * 2;
            MemoryStream ms = new MemoryStream(44 + dataLen);
            BinaryWriter w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataLen);
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)nch); w.Write(rate);
            w.Write(rate * nch * 2); w.Write((short)(nch * 2)); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataLen);
            for (int i = 0; i < n; i++)
                for (int c = 0; c < nch; c++)
                {
                    double v = Math.Max(-1.0, Math.Min(1.0, ch[c][i]));
                    w.Write((short)Math.Round(v * 32767));
                }
            w.Flush();
            return ms.ToArray();
        }
    }
}
