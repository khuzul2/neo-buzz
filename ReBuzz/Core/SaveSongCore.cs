using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.IO;

namespace ReBuzz.Core
{
    internal class SaveSongCore : ISaveSong
    {
        public ISong Song { get; set; }

        /// <summary>Fork (neo-audio.md): the file is a .bmx, .bmw or .bmxml (or an export for them): audio clips are written as waves.</summary>
        public bool Legacy { get; set; }

        /// <summary>Only find out what a legacy save would write and lose (the export preview): no wave is made.</summary>
        public bool DryRun { get; set; }

        /// <summary>Audio assets the legacy save wrote into waves.</summary>
        public int AudioWaves { get; set; }

        /// <summary>Names of assets the wavetable had no slot for.</summary>
        public List<string> AudioLosses { get; } = new List<string>();

        /// <summary>What to undo after the file is written (the temporary waves of a legacy save).</summary>
        public List<System.Action> Cleanup { get; } = new List<System.Action>();

        public void RunCleanup()
        {
            foreach (var c in Cleanup)
            {
                try { c(); } catch (System.Exception) { }
            }
            Cleanup.Clear();
        }

        readonly Dictionary<string, MemoryStream> streamsDictionary = new Dictionary<string, MemoryStream>();

        public Dictionary<string, MemoryStream> GetStreamsDict() { return streamsDictionary; }

        public Stream CreateSubSection(string name)
        {
            MemoryStream stream = new MemoryStream();
            streamsDictionary[name] = stream;
            return stream;
        }
    }
}
