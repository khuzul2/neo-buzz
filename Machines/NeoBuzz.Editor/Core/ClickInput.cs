#nullable disable
// Shared by the MIDI machines' editors (Neo Voice to MIDI, Neo Audio to MIDI): hands a surface's pointer to one function (the keyboard, the pads).

using System;

namespace NeoBuzz.Editor
{
    /// <summary>Hands a surface's pointer to two functions (the keyboard and the pads).</summary>
    internal sealed class ClickInput : IGraphInput
    {
        readonly Func<double, double, bool, bool> down;
        public ClickInput(Func<double, double, bool, bool> down) { this.down = down; }
        public bool Down(double x, double y, int clicks, bool fine) => down(x, y, fine);
        public bool Move(double x, double y, bool fine) => false;
        public bool Up() => false;
        public bool Wheel(double x, double y, int delta, bool fine) => false;
        public bool Leave() => false;
        public bool Capturing => false;
    }
}
