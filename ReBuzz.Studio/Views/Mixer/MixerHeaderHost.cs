// Fork (docs/specs/ux-overhaul.md §11, MX1): the host of the header density strips (the Arrangement's track headers): the returns the
// sends go to, and the options the density asks for. The strips are the mixer's own parts, so a header's fader and the mixer's are one.

using System.Collections.Generic;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Mixer
{
    internal sealed class MixerHeaderHost : IMixerHost
    {
        IReadOnlyList<IMachine> returns = new List<IMachine>();
        ICollection<IMachine> returnSet = new HashSet<IMachine>();

        public MixerHeaderHost(ReBuzzCore core)
        {
            Core = core;
            Options = MixerOptions.For(MixerDensity.Header);
        }

        public ReBuzzCore Core { get; }
        public MixerOptions Options { get; }
        public IReadOnlyList<IMachine> Returns => returns;
        public ICollection<IMachine> ReturnSet => returnSet;

        /// <summary>Looks at the song's returns again (the headers call this before they build their strips).</summary>
        public void Refresh()
        {
            var list = MixerModel.ReturnsOf(Core);
            returns = list;
            returnSet = new HashSet<IMachine>(list);
        }

        public void ToggleFold(string groupId) { }

        public void OpenDevice(IMachine machine) => StudioSelectionOpen?.Invoke(machine);

        public System.Action<IMachine> StudioSelectionOpen { get; set; }
    }
}
