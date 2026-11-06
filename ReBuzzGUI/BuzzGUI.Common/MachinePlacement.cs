using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common
{
    /// <summary>
    /// Fork: where a new machine goes so that it doesn't land on another (gallery mac-510: a new
    /// machine on top of the Master). Positions are the song's (-1..1 across the view); a machine's
    /// box is about 0.14 wide and 0.13 high there at the default zoom, so two machines closer than
    /// <see cref="MinDx"/> across and <see cref="MinDy"/> down overlap.
    /// </summary>
    public static class MachinePlacement
    {
        public const float MinDx = 0.18f, MinDy = 0.16f;
        const float Limit = 0.95f;

        /// <summary>No machine's box would overlap one at (x, y).</summary>
        public static bool IsFree(IEnumerable<IMachine> machines, float x, float y) =>
            machines.All(m => m.Position == null || Math.Abs(m.Position.Item1 - x) >= MinDx || Math.Abs(m.Position.Item2 - y) >= MinDy);

        /// <summary>
        /// (x, y) when it is free, else the nearest free spot on a grid of box-sized steps around it
        /// (within the song's area); (x, y) when the song is full.
        /// </summary>
        public static (float X, float Y) Free(IEnumerable<IMachine> machines, float x, float y)
        {
            var taken = machines.Where(m => m.Position != null).Select(m => m.Position).ToList();
            bool Free(float px, float py) => taken.All(p => Math.Abs(p.Item1 - px) >= MinDx || Math.Abs(p.Item2 - py) >= MinDy);
            x = Math.Clamp(x, -Limit, Limit);
            y = Math.Clamp(y, -Limit, Limit);
            if (Free(x, y)) return (x, y);
            const float sx = MinDx + 0.02f, sy = MinDy + 0.02f;
            for (int ring = 1; ring <= 12; ring++)
            {
                (float X, float Y)? best = null;
                double bestDistance = double.MaxValue;
                for (int i = -ring; i <= ring; i++)
                    for (int j = -ring; j <= ring; j++)
                    {
                        if (Math.Max(Math.Abs(i), Math.Abs(j)) != ring) continue;      // this ring only
                        float px = x + i * sx, py = y + j * sy;
                        if (Math.Abs(px) > Limit || Math.Abs(py) > Limit || !Free(px, py)) continue;
                        double d = Math.Pow(i * sx, 2) + Math.Pow(j * sy, 2);
                        if (d < bestDistance) { bestDistance = d; best = (px, py); }
                    }
                if (best is { } b) return b;
            }
            return (x, y);
        }
    }
}
