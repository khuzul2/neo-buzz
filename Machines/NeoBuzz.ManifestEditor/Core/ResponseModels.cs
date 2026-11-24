#nullable disable
// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): the manifest editor's response plot, only where it
// can be honest. No machine of the studio has a design known well enough to compute a curve from, so
// Every effect with frequency parameters in filter or EQ roles gets markers: a line at each of those
// frequencies and a caption saying that no curve is drawn. A curve guessed from roles alone (a closed
// machine's) would be made up, so none is. Generators get nothing: a synth's filter moves with its envelope,
// key tracking and velocity, so a line at the knob's cutoff would say little; its knobs say that already.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace NeoBuzz.Editor
{
    /// <summary>The response plot of a machine's face.</summary>
    internal sealed class FaceResponse
    {
        /// <summary>A curve computed from the machine's known design; false: markers only.</summary>
        public bool Computed { get; init; }
        /// <summary>The magnitude in dB at a frequency in Hz, from the current settings (computed plots).</summary>
        public Func<double, double> DbAt { get; init; }
        /// <summary>Parameters in Hz marked as lines (on a computed plot too: its bands and corners).</summary>
        public IReadOnlyList<FaceParam> Markers { get; init; } = Array.Empty<FaceParam>();
        /// <summary>What the plot rests on, shown under it.</summary>
        public string Caption { get; init; } = "";
        public double MinDb { get; init; } = -24;
        public double MaxDb { get; init; } = 24;
        public double DbStep { get; init; } = 6;
    }

    internal static class ResponseModels
    {
        /// <summary>A filter or EQ frequency the plot marks.</summary>
        public static bool IsMarked(FaceParam p) =>
            p.Dimension == "Hz" && (p.Role.StartsWith("filter.", StringComparison.Ordinal) || p.Role.StartsWith("eq.", StringComparison.Ordinal));

        /// <param name="raw">A parameter's raw value now.</param>
        /// <param name="sampleRate">The engine's sample rate now.</param>
        /// <param name="generator">A generator: no markers without a model (its filter moves with each note).</param>
        public static FaceResponse For(string gear, IReadOnlyList<FaceParam> ps, Func<FaceParam, int> raw, Func<double> sampleRate, bool generator = false)
        {
            var markers = ps.Where(IsMarked).ToList();
            if (markers.Count == 0 || generator) return null;
            return new FaceResponse
            {
                Computed = false,
                Markers = markers,
                Caption = "Markers only: its response isn't computed here (no known design to compute it from), so no curve is drawn; " +
                          "the lines mark the frequencies it is set to.",
            };
        }

    }
}
