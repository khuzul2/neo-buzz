#nullable disable
// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): the manifest editor's face, toolkit-free and
// engine-free. A host (ReBuzz.exe, the studio: Host/MachineFaces.cs) describes a machine's parameters
// from its manifest, with the agent server's own conversions as delegates (the value as param_get
// shows it, a typed value as param_set reads it, the physical value); this file decides what the
// editor shows: sections by the manifest's groups, a knob, choice buttons, a drop-down or a switch per
// parameter, an ADSR picture for each envelope (env.* and filter.env.* roles), the safe ranges, and a
// response plot where one can be honest (Core/ResponseModels.cs). Drawn through INeoCanvas, so one
// editor (ManifestEditor.cs) serves WPF and Avalonia, and NeoBuzz.MachineTests checks it on Linux.

using System;
using System.Collections.Generic;
using System.Linq;

namespace NeoBuzz.Editor
{
    /// <summary>How the editor shows a parameter.</summary>
    internal enum FaceControl
    {
        /// <summary>A rotary knob: continuous values, in units.</summary>
        Knob,
        /// <summary>A row of buttons, one per choice (a few short choices).</summary>
        Choice,
        /// <summary>Off / On buttons.</summary>
        Switch,
        /// <summary>A drop-down: many or long choices.</summary>
        Dropdown,
    }

    /// <summary>Where agents may set a parameter (machine-safety.md §3.4), in raw values, and why.</summary>
    internal sealed class FaceSafeRange
    {
        public int Min { get; init; }
        public int Max { get; init; }
        /// <summary>"-98.4% … 100.0%".</summary>
        public string Text { get; init; } = "";
        public string Reason { get; init; } = "";

        public bool Contains(int raw) => raw >= Min && raw <= Max;
    }

    /// <summary>One parameter as the manifest editor shows it.</summary>
    internal sealed class FaceParam
    {
        /// <summary>Its entry in the editor's table (EditorContext's slot).</summary>
        public int Slot { get; init; }
        /// <summary>The machine's name for it.</summary>
        public string Name { get; init; } = "";
        /// <summary>What the editor calls it: the curated plain name, else the machine's name.</summary>
        public string Label { get; init; } = "";
        /// <summary>The manifest's group (a section).</summary>
        public string Group { get; init; } = "";
        /// <summary>The curated role ("filter.cutoff", "env.attack"), or empty.</summary>
        public string Role { get; init; } = "";
        public string Description { get; init; } = "";
        /// <summary>A track parameter (shown for the first track).</summary>
        public bool IsTrack { get; init; }
        public int Min { get; init; }
        public int Max { get; init; }
        public int Default { get; init; }
        /// <summary>The choices of a choice or switch; null for a knob.</summary>
        public string[] Choices { get; init; }
        public bool IsSwitch { get; init; }
        /// <summary>A −/+ value: the knob's arc grows from its value 0 (<see cref="ArcOrigin"/>).</summary>
        public bool Bipolar { get; init; }
        /// <summary>The quantity of its value: "Hz", "s", "dB", "%", "st"… ("" for a bare number or a choice).</summary>
        public string Dimension { get; init; } = "";
        /// <summary>A "%" value held as a fraction (0.35 for 35 %) rather than as a percentage.</summary>
        public bool Fraction { get; init; }
        /// <summary>Its unit came from a description (a draft): approximate.</summary>
        public bool Approximate { get; init; }
        public FaceSafeRange SafeRange { get; init; }

        /// <summary>A raw value as the agent server shows it (param_get, machine_describe).</summary>
        public Func<int, string> Text { get; init; } = raw => raw.ToString(System.Globalization.CultureInfo.InvariantCulture);
        /// <summary>
        /// A typed value as param_set reads it ("800 Hz", "-6 dB", "35%", a choice, "raw:12"), from the raw value now,
        /// to a raw value; throws with the reason when it can't.
        /// </summary>
        public Func<string, int, int> Parse { get; init; }
        /// <summary>The physical value of a raw value, in <see cref="Dimension"/>'s base unit (s, Hz); NaN when none.</summary>
        public Func<int, double> Value { get; init; } = raw => double.NaN;

        public FaceControl Control =>
            Choices == null ? FaceControl.Knob
            : IsSwitch ? FaceControl.Switch
            : Choices.Length <= 5 && Choices.All(c => (c ?? "").Length <= 9) ? FaceControl.Choice
            : FaceControl.Dropdown;

        /// <summary>A raw value as a position 0 … 1 in its range.</summary>
        public double Position(int raw) => Max == Min ? 0 : Math.Clamp((raw - Min) / (double)(Max - Min), 0, 1);

        /// <summary>
        /// Where its knob's value arc starts (0 … 1): where a −/+ value is 0 (the middle only for a symmetric range), else
        /// the range's minimum. A value with no physical reading starts at the middle, as before.
        /// </summary>
        public double ArcOrigin
        {
            get
            {
                if (!Bipolar) return 0;
                double lo = SafeValue(Min), hi = SafeValue(Max);
                if (double.IsNaN(lo) || double.IsNaN(hi)) return 0.5;
                return ParamMapping.ZeroPosition(SafeValue, Min, Max);
            }
        }

        /// <summary>The safe range as knob positions, or null.</summary>
        public (double From, double To)? SafePositions =>
            SafeRange == null || (SafeRange.Min <= Min && SafeRange.Max >= Max) ? null : (Position(SafeRange.Min), Position(SafeRange.Max));

        /// <summary>
        /// A level 0 … 1 for pictures (an envelope's sustain): a fraction, a percentage, decibels as gain, else the
        /// position in its range.
        /// </summary>
        public double Level(int raw)
        {
            double v = SafeValue(raw);
            if (double.IsNaN(v)) return Position(raw);
            switch (Dimension)
            {
                case "%": return Math.Clamp(Fraction ? v : v / 100, 0, 1);
                case "dB": return double.IsNegativeInfinity(v) ? 0 : Math.Clamp(Math.Pow(10, v / 20), 0, 1);
                default: return Position(raw);
            }
        }

        /// <summary>The physical value, NaN when the host can't say.</summary>
        public double SafeValue(int raw)
        {
            try
            {
                return Value?.Invoke(raw) ?? double.NaN;
            }
            catch (Exception)
            {
                return double.NaN;
            }
        }

        public override string ToString() => Label;
    }

    /// <summary>An envelope's stages in one section (env.* or filter.env.*), for its ADSR picture.</summary>
    internal sealed class FaceEnvelope
    {
        public string Title { get; init; } = "";
        public FaceParam Attack { get; init; }
        public FaceParam Decay { get; init; }
        public FaceParam Sustain { get; init; }
        public FaceParam Release { get; init; }

        public IEnumerable<FaceParam> Stages => new[] { Attack, Decay, Sustain, Release }.Where(p => p != null);

        IEnumerable<FaceParam> Times => new[] { Attack, Decay, Release }.Where(p => p != null);

        /// <summary>Every time stage it has is in seconds: the picture is to scale.</summary>
        public bool InSeconds => Times.All(p => p.Dimension == "s");

        /// <summary>
        /// Its times are all in seconds, or all bare scales (drawn by position, "relative"); a mix of the two
        /// (FSM KickXP's decay in seconds, its release a bare slope) has no honest picture.
        /// </summary>
        public bool Drawable => InSeconds || Times.All(p => p.Dimension.Length == 0);
    }

    /// <summary>A section: the parameters of one manifest group, in the machine's order.</summary>
    internal sealed class FaceSection
    {
        public string Title { get; init; } = "";
        public List<FaceParam> Params { get; } = new List<FaceParam>();
        public List<FaceEnvelope> Envelopes { get; } = new List<FaceEnvelope>();
    }

    /// <summary>What the editor shows of a machine.</summary>
    internal sealed class MachineFace
    {
        /// <summary>The gear's name ("Pedal SH101").</summary>
        public string Gear { get; init; } = "";
        /// <summary>"curated", "draft" or "generated": how far its units are to be trusted.</summary>
        public string Quality { get; init; } = "";
        public string Summary { get; init; } = "";
        public bool Generator { get; init; }
        /// <summary>The manifest's type ("monosynth", "reverb"): its category, for the editor's accent (ux-overhaul.md P2a).</summary>
        public string Type { get; init; } = "";
        /// <summary>Its track count when the editor was made (track parameters show the first track's).</summary>
        public int Tracks { get; init; }
        public IReadOnlyList<FaceParam> Params { get; init; } = Array.Empty<FaceParam>();
        public IReadOnlyList<FaceSection> Sections { get; init; } = Array.Empty<FaceSection>();
        /// <summary>The response plot, or null when none can be honest.</summary>
        public FaceResponse Response { get; init; }

        /// <summary>The line under the title: what the values rest on.</summary>
        public string Provenance => Quality switch
        {
            "curated" => "Curated manifest: names, units and roles checked against the machine's source and texts.",
            "draft" => "Draft manifest: names are the machine's own, units fitted to its value texts" +
                       (Params.Any(p => p.Approximate) ? " (≈: read from a description, approximate)." : "."),
            _ => "Generated manifest: exact units.",
        };

        public FaceParam Find(string nameOrLabel) =>
            Params.FirstOrDefault(p => p.Name == nameOrLabel) ?? Params.FirstOrDefault(p => p.Label == nameOrLabel);

        /// <summary>
        /// Plans the face: a section per group in the order the groups first appear (track parameters last, as
        /// "Track 1" when the machine has more than one track), an ADSR picture for each envelope with two or more
        /// stages, and the response plot (ResponseModels).
        /// </summary>
        public static MachineFace Plan(string gear, string quality, string summary, bool generator, int tracks,
            IReadOnlyList<FaceParam> ps, Func<FaceParam, int> raw, Func<double> sampleRate, string type = "")
        {
            var sections = new List<FaceSection>();
            var byTitle = new Dictionary<string, FaceSection>(StringComparer.Ordinal);
            foreach (var p in ps.Where(p => !p.IsTrack).Concat(ps.Where(p => p.IsTrack)))
            {
                string title = SectionTitle(p, tracks);
                if (!byTitle.TryGetValue(title, out var s))
                {
                    s = new FaceSection { Title = title };
                    byTitle[title] = s;
                    sections.Add(s);
                }
                s.Params.Add(p);
            }
            foreach (var s in sections) s.Envelopes.AddRange(Envelopes(s.Params));
            return new MachineFace
            {
                Gear = gear ?? "",
                Quality = quality ?? "",
                Summary = summary ?? "",
                Generator = generator,
                Type = type ?? "",
                Tracks = tracks,
                Params = ps,
                Sections = sections,
                Response = ResponseModels.For(gear ?? "", ps, raw, sampleRate, generator),
            };
        }

        static string SectionTitle(FaceParam p, int tracks)
        {
            string g = (p.Group ?? "").Trim();
            if (p.IsTrack)
            {
                // Curated track parameters are grouped "Track", or by what they do (FSM KickXP's Tone, Layers, Amp).
                if (g.Length == 0 || g == "Track") return tracks > 1 ? "Track 1" : "Track";
                return tracks > 1 ? g + " (track 1)" : g;
            }
            return g.Length == 0 ? "Parameters" : g;
        }

        static readonly string[] Prefixes = { "env.", "filter.env." };

        /// <summary>The envelopes among a section's parameters: env.attack … env.release, filter.env.attack … (two stages at least).</summary>
        internal static IEnumerable<FaceEnvelope> Envelopes(IReadOnlyList<FaceParam> ps)
        {
            foreach (var prefix in Prefixes)
            {
                FaceParam Stage(string stage) => ps.FirstOrDefault(p => p.Role == prefix + stage);
                var env = new FaceEnvelope
                {
                    Title = prefix == "env." ? "Envelope" : "Filter envelope",
                    Attack = Stage("attack"),
                    Decay = Stage("decay"),
                    Sustain = Stage("sustain"),
                    Release = Stage("release"),
                };
                if (env.Stages.Count() >= 2 && env.Drawable) yield return env;
            }
        }
    }
}
