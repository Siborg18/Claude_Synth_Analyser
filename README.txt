SYNTH ANALYSER  v1.2
====================

Load a sample, and Synth Analyser measures the sound and writes a "recipe" for
rebuilding it on any subtractive synth: oscillators, filter, envelopes, LFOs
and effects. A built-in preview synth plays the recipe back, so you can A/B it
against the original.


GETTING STARTED
---------------
1. Double-click SynthAnalyser.exe. It needs no install and runs on Windows 10
   and 11, which already include .NET Framework 4.x.
   Windows may show "Windows protected your PC" the first time, because the
   app is not code-signed. Click "More info" and then "Run anyway".
2. Drag an audio file onto the window, or click "Open sample...".
   The whole file is analysed straight away (only the first 8 s of a long file).
3. Read the "Synth recipe" tab. The "Measurements" tab shows the raw numbers
   behind each suggestion.
4. Click "Play original" (Space) and "Play synth preview" (P) to compare them.
5. Build the patch in your own synth, starting from the recipe, and tweak it
   by ear.

Try the files in the "examples" folder first. Each one was generated with
known settings, so you can check what the analyser finds.


SELECTING A REGION
------------------
- Drag on the waveform to analyse just one note or a short phrase (max 30 s).
  The analysis runs again when you release the mouse.
- Double-click the waveform to select the whole file again.
- Dotted green lines mark the detected note onsets.
- The green shaded area is the note used for the timbre analysis.
  For a phrase, this is the most representative note.
Accuracy is best on a single, clean note with no drums or other instruments.


WHAT IT DETECTS
---------------
Oscillators   saw / square / pulse width / triangle / sine / noise,
              unison and detune, sub oscillator, noise layer or attack click
Filter        low-pass cutoff, slope (12/24 dB), resonance, high-pass,
              filter envelope amount and decay, band-pass for noise sounds
Envelopes     amp ADSR, hold, pitch envelope (including fast 808/kick drops)
Modulation    vibrato (rate, depth, delay), tremolo, filter LFO / wobble
              (with tempo-synced note values when a tempo is found)
Pitch         note name and tuning offset, chords (e.g. "Cm"), and
              inharmonic/bell sounds with a guessed FM ratio
Phrases       note list, arpeggio or melody, tempo estimate, gate length,
              legato and glide time
Effects       reverb (decay time and mix), delay (time, feedback, mix),
              stereo width / chorus, clipping / saturation


PANELS
------
Spectrum          Blue: the measured spectrum. Green dots: the detected
                  harmonics. Orange dashes: the fitted "waveform + filter"
                  model. The dotted line marks the filter cutoff.
Envelope & pitch  Amplitude in dB, plus the pitch track in semitones
                  relative to the detected note.


SAVE AS VITAL PRESET  (new in v1.2)
--------------------
"Save Vital preset..." writes the analysis as a .vital file (Vital 1.0.7+,
the free wavetable synth). If Vital's user preset folder exists
(Documents\Vital\User\Presets) the dialog opens there, so the preset shows
up in Vital's browser straight away. Otherwise use Vital's menu to open it.
What gets set:
  Osc 1   matched waveform (saw/square/pulse/triangle/sine), unison voices,
          detune, stereo spread and fine tune
  Osc 2   sine sub (-1 octave) or sine layer
  Sample  white noise layer, or a noise burst on the attack (Env 4)
  Filter 1  low/band/high-pass, 12 or 24 dB, cutoff, resonance, key tracking
  Filter 2  extra high-pass, if one was detected
  Env 1   amp ADSR.          Env 2  pitch envelope -> osc transpose
  Env 3   filter envelope.   Env 5  initial punch -> osc levels
  LFO 1   -> cutoff (wobble).  LFO 2 -> pitch (vibrato).  LFO 3 -> level (tremolo)
  Voice   mono/legato + glide, or poly
  FX      chorus, delay, reverb, distortion, when detected
Play the MIDI note written in the preset comments (also shown in the
status bar). Values are estimates: fine-tune them by ear in Vital.
Command line: SynthAnalyser.exe --analyse sample.wav --vital out.vital
Whole folder: SynthAnalyser.exe --batch "C:\My Samples" "C:\My Presets"
  (one .vital + recipe .txt per sample, plus _index.tsv; long files: first 30 s)


COMPARE MY VERSION  (new in v1.1)
------------------
Recreated the sound in your own synth? Record or bounce the same note to a
WAV file, then click "Compare my version...". The app analyses your recording
the same way and lists the biggest differences first: wrong octave, too
dark/bright, attack too slow, missing punch, LFO rate, detune, stereo width.
Command line: SynthAnalyser.exe --compare original.wav mine.wav > compare.txt


BUTTONS
-------
Copy recipe / Save recipe...   copy or save the recipe as text
Export preview WAV...          save the preview synth's rendering
Keys: Space = play original, P = play preview, Esc = stop, Ctrl+O = open


FILE FORMATS
------------
Built in:  WAV (8/16/24/32-bit, float) and AIFF.
MP3, FLAC, OGG, M4A and others work if ffmpeg.exe is next to
SynthAnalyser.exe or on your PATH (https://ffmpeg.org).


LIMITATIONS
-----------
All values are estimates. Treat them as a starting point, not an exact
answer. Things that make the analysis harder:
- Several instruments at once, or drums under the synth.
- Heavy effects. A long reverb can look like detune or a slow release.
- Wavetable, FM or sample-based sounds. The app suggests the closest basic
  waveform, and flags a poor match with a tip.
- Envelope times are measured as "time to fade to about -60 dB". Different
  synths use different envelope curves, so adjust the times by ear.


REBUILDING FROM SOURCE
----------------------
The C# source is in "source\src". Run "source\build.bat". It uses the C#
compiler that ships with Windows (.NET Framework 4.x), so there is nothing
else to install.

Command-line use (prints the recipe):
  SynthAnalyser.exe --analyse "my sample.wav" [--render preview.wav]
  (Output is easiest to see with:  SynthAnalyser.exe --analyse x.wav > recipe.txt)
