// Fork: PianoKeys' scan codes as physical keys (Z S X D C V G B H N J M, then Q 2 W 3 E R 5 T 6 Y 7 U I 9 O 0 P): the piano roll plays with them.

using System;
using Avalonia.Input;

namespace ReBuzz.Studio.Parameters
{
    internal static class PianoKeys
    {
        static readonly PhysicalKey[] NoteKeys =
        {
            PhysicalKey.Z, PhysicalKey.S, PhysicalKey.X, PhysicalKey.D, PhysicalKey.C, PhysicalKey.V, PhysicalKey.G, PhysicalKey.B,
            PhysicalKey.H, PhysicalKey.N, PhysicalKey.J, PhysicalKey.M, PhysicalKey.Q, PhysicalKey.Digit2, PhysicalKey.W,
            PhysicalKey.Digit3, PhysicalKey.E, PhysicalKey.R, PhysicalKey.Digit5, PhysicalKey.T, PhysicalKey.Digit6, PhysicalKey.Y,
            PhysicalKey.Digit7, PhysicalKey.U, PhysicalKey.I, PhysicalKey.Digit9, PhysicalKey.O, PhysicalKey.Digit0, PhysicalKey.P,
        };

        public static int IndexOf(PhysicalKey key) => Array.IndexOf(NoteKeys, key);
    }
}
