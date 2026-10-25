using System;

namespace PedalFM
{
    /// <summary>
    /// FM algorithm topology table.
    ///
    /// Operators are evaluated strictly in order 0,1,2,3 in a forward pass.
    /// Operator N can only be modulated by ops with index &lt; N. This
    /// rules out feedback loops (which would require sample-by-sample
    /// state) and makes the renderer trivial: a 4-deep loop with bitmask
    /// modulator selection.
    ///
    /// 8 algorithms covering the chord-stab / pad / bell / brass space:
    ///
    ///   0 : 0 -&gt; 1 -&gt; 2 -&gt; 3 -&gt; out          linear chain (bell, EP)
    ///   1 : 0 -&gt; 1 -&gt; out,  2 -&gt; 3 -&gt; out    two parallel chains (brass, pad)
    ///   2 : 0 -&gt; 1 -&gt; 2 -&gt; 3 -&gt; out, 0 -&gt; 2 chain with branch (punchy stab)
    ///   3 : 0 -&gt; 3, 1 -&gt; 3, 2 -&gt; 3 -&gt; out   three modulators on one carrier
    ///                                          (bell cluster, dense stab)
    ///   4 : 0 -&gt; 1 -&gt; out, 2 -&gt; out, 3 -&gt; out 1 chain + 2 parallel carriers
    ///   5 : 0 -&gt; 1, 1 -&gt; 2, 1 -&gt; 3 (out 2,3) one mod feeding parallel pair
    ///   6 : 4 parallel sines                   additive
    ///   7 : 0 -&gt; 1 -&gt; 2 -&gt; out, 3 -&gt; out     chain + extra carrier
    /// </summary>
    internal static class Algorithms
    {
        public const int OpCount = 4;
        public const int Count   = 8;

        // ModMask[algo, opIndex]:
        // bitmask of which earlier operators modulate this op.
        // Bit M set in ModMask[a,N] => op M's output adds to op N's phase
        // modulation input. Only bits below N are valid.
        public static readonly byte[,] ModMask = new byte[Count, OpCount]
        {
            // op0  op1   op2   op3
            { 0,    0b0001, 0b0010, 0b0100 }, // 0: 0->1->2->3
            { 0,    0b0001, 0,      0b0100 }, // 1: 0->1, 2->3
            { 0,    0b0001, 0b0011, 0b0100 }, // 2: 0->1->2->3 with 0->2
            { 0,    0,      0,      0b0111 }, // 3: 0,1,2 -> 3
            { 0,    0b0001, 0,      0      }, // 4: 0->1, 2, 3 (parallel carriers)
            { 0,    0b0001, 0b0010, 0b0010 }, // 5: 0->1, 1->2, 1->3
            { 0,    0,      0,      0      }, // 6: 4 parallel
            { 0,    0b0001, 0b0010, 0      }, // 7: 0->1->2 + 3 parallel
        };

        // CarrierMask[algo]: which ops feed the audio output.
        // Bit N set => op N's output is summed into the voice output.
        public static readonly byte[] CarrierMask = new byte[Count]
        {
            0b1000, // 0: only op3
            0b1010, // 1: op1 + op3
            0b1000, // 2: only op3
            0b1000, // 3: only op3
            0b1110, // 4: op1 + op2 + op3
            0b1100, // 5: op2 + op3
            0b1111, // 6: all four
            0b1100, // 7: op2 + op3
        };

        public static readonly string[] Names = new string[Count]
        {
            "Linear (bell/EP)",
            "Dual chain (brass)",
            "Branched chain (stab)",
            "Three-on-one (cluster)",
            "Mixed (organ+bite)",
            "Twin pair (rich stab)",
            "Additive (4 parallel)",
            "Pad + carrier",
        };
    }
}
