// Fork (docs/specs/neo-learn.md §5.3, ux-overhaul.md P3e): what the device view reads of a manifest entry besides its range: the
// unit its numbers are in, and the Explain text (How: what the control does to the sound; Sound: what you will hear).

using ReBuzz.Agent.Model;

namespace ReBuzz.Studio.Views.Device
{
    internal static class DeviceExplain
    {
        public static string How(ManifestParameter p) => ""  /* TODO(wave 8): p?.How */;

        public static string Sound(ManifestParameter p) => ""  /* TODO(wave 8): p?.Sound */;

        /// <summary>The unit a manifest entry's numbers are in ("" for a count, a choice or a unit the machine's own text carries).</summary>
        public static string Unit(ManifestParameter p) => p == null ? "" : p.Mapping.Kind switch
        {
            MappingKind.Freq => "Hz",
            MappingKind.Time => "s",
            MappingKind.Db => "dB",
            MappingKind.Percent or MappingKind.Bipolar => "%",
            MappingKind.Ratio => ":1",
            MappingKind.Enum or MappingKind.Switch => "",
            _ => p.Mapping.Unit ?? "",
        };
    }
}
