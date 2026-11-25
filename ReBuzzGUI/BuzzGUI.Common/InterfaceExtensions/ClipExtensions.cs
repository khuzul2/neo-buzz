using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common.InterfaceExtensions
{
    public static class ClipExtensions
    {
        public static void UpdateWaveReferences(this IClip clip, IDictionary<int, int> map)
        {
            foreach (var c in clip.Columns.Where(c => c.Type == ClipColumnType.Parameter && c.Parameter.Flags.HasFlag(ParameterFlags.Wave)))
                c.RemapValues(map);

            clip.UpdatePEMachineWaveReferences(map);
        }
    }
}
