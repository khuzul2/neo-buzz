using ReBuzz.Core.Session;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ReBuzz.AppViews.Session
{
    internal sealed partial class SessionView
    {
        internal sealed partial class ClipPanel
        {
            /// <summary>
            /// An audio clip's audio (docs/specs/session-view.md 1.0, Live's clip view): its wave,
            /// warp mode (and what Beats keeps), its tempo (halve, double, detect again), warp
            /// markers, gain and pitch. Each change is committed through the given callback.
            /// </summary>
            internal sealed class AudioEditor : StackPanel
            {
                internal static readonly (string Value, string Label)[] Warps =
                {
                    ("Beats", "Beats"), ("Complex", "Complex"), ("Repitch", "Re-Pitch"), ("Off", "Off (unwarped)"),
                };

                internal static readonly (string Value, string Label)[] Preserves =
                {
                    ("Transients", "Transients"), ("1/4", "1/4"), ("1/8", "1/8"), ("1/16", "1/16"),
                };

                readonly Action<Action<SessionDocument.AudioSettings>> commit;
                readonly TextBlock about;
                readonly ComboBox warp;
                readonly ComboBox preserve;
                readonly FrameworkElement preserveRow;
                readonly TextBox bpm;
                readonly TextBox gain;
                readonly TextBox transpose;
                readonly TextBox detune;
                readonly TextBlock markers;
                readonly FrameworkElement transposeRow, detuneRow;
                string shownBpm, shownGain, shownTranspose, shownDetune;     // as loaded: a box commits only when typed in
                bool loading;

                public AudioEditor(Action<Action<SessionDocument.AudioSettings>> commit, Func<double> detect, Action editMarkers)
                {
                    this.commit = commit;
                    about = new TextBlock { Foreground = SessionPalette.DimText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), FontSize = 11 };
                    warp = new ComboBox
                    {
                        Width = 110,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "How the clip follows the song's tempo (its warp mode):\n" +
                                  "Beats: cut at its transients (or a grid), each piece played on its beat at its own speed: drums, loops.\n" +
                                  "Complex: stretched by a phase vocoder, pitch kept: any material.\n" +
                                  "Re-Pitch: faster or slower with the tempo, its pitch with it (a turntable).\n" +
                                  "Off: plays at its own speed from the clip's start, whatever the tempo."
                    };
                    foreach (var w in Warps) warp.Items.Add(w.Label);
                    warp.SelectionChanged += (s, e) => { if (!loading && warp.SelectedIndex >= 0) Commit(a => a.Warp = Warps[warp.SelectedIndex].Value); };
                    preserve = new ComboBox
                    {
                        Width = 110,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "Beats mode cuts the audio at: its transients (and on each beat between far-apart ones),\n" +
                                  "or a grid of 1/4, 1/8 or 1/16 notes."
                    };
                    foreach (var p in Preserves) preserve.Items.Add(p.Label);
                    preserve.SelectionChanged += (s, e) => { if (!loading && preserve.SelectedIndex >= 0) Commit(a => a.Preserve = Preserves[preserve.SelectedIndex].Value); };
                    preserveRow = Row("Preserve", preserve);

                    bpm = Box("The audio's own tempo: its length in beats is its seconds times this over 60.\n" +
                              "The clip's length follows (without warp markers). Found when the file was dropped.", CommitFields);
                    var half = new FlatButton("÷2", () => Commit(a => { a.Bpm = Math.Max(1, a.Bpm / 2); a.Markers.Clear(); }), "Half the tempo: the clip is half as many beats long") { Margin = new Thickness(4, 0, 0, 0) };
                    var twice = new FlatButton("×2", () => Commit(a => { a.Bpm = Math.Min(999, a.Bpm * 2); a.Markers.Clear(); }), "Double the tempo: the clip is twice as many beats long") { Margin = new Thickness(2, 0, 0, 0) };
                    var find = new FlatButton("Detect", () => { if (!loading) detect(); }, "Find the tempo again from the audio (its length in bars, and its transients); clears warp markers") { Margin = new Thickness(2, 0, 0, 0) };
                    var bpmRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { bpm, half, twice, find } };

                    gain = Box("Clip gain in dB (−70 to +24)", CommitFields);
                    transpose = Box("Transpose in semitones (−48 to 48). Beats and Off: played faster or slower, as a sampler would;\n" +
                                    "Complex: the pitch alone. Re-Pitch has none: the tempo sets its pitch.", CommitFields);
                    detune = Box("Detune in cents (−50 to 50)", CommitFields);
                    transposeRow = Row("Transpose", transpose);
                    detuneRow = Row("Detune (ct)", detune);

                    markers = new TextBlock { Foreground = SessionPalette.DimText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), FontSize = 11 };
                    var editButton = new FlatButton("Warp markers…", () => { if (!loading) editMarkers(); },
                        "Pin moments of the audio to beats of the clip (warp markers): the audio between two markers\n" +
                        "is stretched to fit. For audio that wasn't played to a click.");
                    var markerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { editButton, markers } };

                    Children.Add(about);
                    Children.Add(Row("Warp", warp));
                    Children.Add(preserveRow);
                    Children.Add(Row("Tempo", bpmRow));
                    Children.Add(Row("Gain (dB)", gain));
                    Children.Add(transposeRow);
                    Children.Add(detuneRow);
                    Children.Add(markerRow);
                }

                internal ComboBox WarpBox => warp;
                internal ComboBox PreserveBox => preserve;
                internal TextBox BpmBox => bpm;
                internal TextBox GainBox => gain;
                internal TextBox TransposeBox => transpose;
                internal TextBox DetuneBox => detune;
                internal string AboutText => about.Text;
                internal void CommitTypedFields() => CommitFields();

                public void Load(SessionDocument.AudioSettings a, (string Wave, double Seconds, int Rate, int Channels, double Beats)? info)
                {
                    loading = true;
                    try
                    {
                        about.Text = info is { } i
                            ? $"Wave {a.Wave + 1:X2} {i.Wave} · {i.Seconds:0.00} s · {i.Rate / 1000.0:0.#} kHz {(i.Channels > 1 ? "stereo" : "mono")} · {i.Beats:0.##} beats"
                            : $"Wave {a.Wave + 1:X2} is empty: the clip is silent.";
                        warp.SelectedIndex = Math.Max(0, Array.FindIndex(Warps, w => w.Value == a.Warp));
                        preserve.SelectedIndex = Math.Max(0, Array.FindIndex(Preserves, p => p.Value == a.Preserve));
                        preserveRow.Visibility = a.Warp == "Beats" ? Visibility.Visible : Visibility.Collapsed;
                        bpm.Text = shownBpm = a.Bpm.ToString("0.##", CultureInfo.InvariantCulture);
                        bpm.IsEnabled = a.Markers.Count < 2;
                        gain.Text = shownGain = a.GainDb.ToString("0.#", CultureInfo.InvariantCulture);
                        transpose.Text = shownTranspose = a.Transpose.ToString(CultureInfo.InvariantCulture);
                        detune.Text = shownDetune = a.Detune.ToString(CultureInfo.InvariantCulture);
                        // Re-Pitch's pitch is its speed: no transposition.
                        transposeRow.Visibility = detuneRow.Visibility = a.Warp == "Repitch" ? Visibility.Collapsed : Visibility.Visible;
                        markers.Text = a.Markers.Count >= 2 ? $"{a.Markers.Count} markers" : "none";
                    }
                    finally
                    {
                        loading = false;
                    }
                }

                void Commit(Action<SessionDocument.AudioSettings> change)
                {
                    if (!loading) commit(change);
                }

                void CommitFields()
                {
                    if (loading) return;
                    // Only what was typed in: the others show rounded values, which mustn't be written back.
                    double b = 0, g = 0, t = 0, d = 0;
                    bool bpmOk = bpm.Text != shownBpm && Number(bpm.Text, out b) && b >= 1 && b <= 999;
                    bool gainOk = gain.Text != shownGain && Number(gain.Text, out g) && g >= -70 && g <= 24;
                    bool transposeOk = transpose.Text != shownTranspose && Number(transpose.Text, out t) && t >= -48 && t <= 48;
                    bool detuneOk = detune.Text != shownDetune && Number(detune.Text, out d) && d >= -50 && d <= 50;
                    if (!bpmOk && !gainOk && !transposeOk && !detuneOk) return;
                    Commit(a =>
                    {
                        if (bpmOk && Math.Abs(b - a.Bpm) > 1e-9 && a.Markers.Count < 2) a.Bpm = b;
                        if (gainOk) a.GainDb = Math.Round(g, 1);
                        if (transposeOk) a.Transpose = (int)Math.Round(t);
                        if (detuneOk) a.Detune = (int)Math.Round(d);
                    });
                }

                /// <summary>A number typed either way (1.5 or 1,5); empty is 0.</summary>
                internal static bool Number(string text, out double value)
                {
                    text = (text ?? "").Trim().Replace('−', '-');
                    if (text.Length == 0) { value = 0; return true; }
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                           double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
                }

                static TextBox Box(string tip, Action commitText)
                {
                    var box = new TextBox
                    {
                        Width = 52,
                        Background = SessionPalette.Empty,
                        Foreground = SessionPalette.Text,
                        BorderBrush = SessionPalette.Header,
                        CaretBrush = SessionPalette.Text,
                        Padding = new Thickness(2, 1, 2, 1),
                        ToolTip = tip
                    };
                    box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { commitText(); e.Handled = true; } };
                    box.LostKeyboardFocus += (s, e) => commitText();
                    return box;
                }
            }
        }
    }
}
