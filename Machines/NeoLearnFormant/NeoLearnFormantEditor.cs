#nullable disable
// Neo Learn Formant's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is the vowel filter's
// frequency response (log frequency across, 0 to -60 dB down) with its four formants marked F1 to F4: they move across the vowel space as the vowel
// and the tract size move, and narrow as Formant Q rises; the output's scope is faint behind. The pad is Vowel (across) and Tract Size (up).
using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnFormant
{
    internal sealed class NeoLearnFormantEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xD9, 0x8F, 0xB8));

        /// <summary>The curve's points and the frequencies it spans (Hz, logarithmic).</summary>
        public const int Points = 120;
        public const double LowHz = 80, HighHz = 6000;

        readonly double[] response = new double[Points];
        readonly double[] hz = new double[VowelTable.Bands], bw = new double[VowelTable.Bands], amp = new double[VowelTable.Bands];

        protected override string Title => "Neo Learn Formant";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Vocal";
        protected override int PadX => P.VowelMorph;
        protected override int PadY => P.TractScale;
        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnFormantMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            FormantMap.Formants(ctx.Value(P.VowelMorph), ctx.Value(P.TractScale), ctx.Value(P.FormantQ), 0, 0, 44100, hz, bw, amp);
            double lg = Math.Log(HighHz / LowHz);
            for (int i = 0; i < Points; i++) response[i] = VowelTable.Response(LowHz * Math.Exp(lg * i / (Points - 1)), hz, bw, amp);
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x40), 0.5, 0.3);
            LearnPictureParts.FormantResponse(g, r, response, Points, LowHz, HighHz, hz, VowelTable.Bands, accent);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Vowel ->, Tract Size up: drag the dot; double-click resets");
        }
    }
}
