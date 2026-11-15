#nullable disable
// Fork (docs/specs/machines.md §3.4): versioned machine state. A machine of ours whose parameter layout or own data may change between releases
// declares a StateVersion; the song saves it with the machine's data (StateEnvelope), and on load the machine maps what an older version saved
// onto its current layout (a chain of migrations, version n -> n + 1). Old Buzz machines are not versioned: their layout is frozen as before.
//
// Everything here is plain data and runs on the UI/loader thread, never on the audio thread. Only global parameters carry saved values
// (track parameters are played events: notes and triggers), so only they migrate.

using System.Collections.Generic;

namespace Buzz.MachineInterface
{
    public interface IVersionedMachine
    {
        /// <summary>The version of the layout and data this build saves (0: the layout every song had before versions existed).</summary>
        int StateVersion { get; }

        /// <summary>
        /// The stable key of each global parameter of the current layout, in index order. A key is the parameter's identity for
        /// the native song format (.nbl) and for migrations: it never changes for the same parameter, whatever its name or position.
        /// </summary>
        IReadOnlyList<string> ParameterKeys { get; }

        /// <summary>
        /// Maps global parameter values saved at <paramref name="fromVersion"/> (in that version's order) to the current layout.
        /// An entry of int.MinValue was not saved (the default stands). Null when the machine cannot (a version newer than this build, or a failed migration): the caller keeps the defaults.
        /// </summary>
        int[] MigrateGlobals(int fromVersion, int[] saved);

        /// <summary>The same, by key: the saved values by the keys of <paramref name="fromVersion"/>, the result by current keys. Null when it cannot.</summary>
        IReadOnlyDictionary<string, int> MigrateKeyed(int fromVersion, IReadOnlyDictionary<string, int> saved);

        /// <summary>The machine's own data (the XML of its MachineState) saved at <paramref name="fromVersion"/>, as the current version reads it. Null when it cannot.</summary>
        byte[] MigrateData(int fromVersion, byte[] data);

        /// <summary>Where the global parameter at <paramref name="savedIndex"/> of version <paramref name="fromVersion"/> is now (-1: dropped, or unknown).</summary>
        int MapParameterIndex(int fromVersion, int savedIndex);
    }

    /// <summary>
    /// What ReBuzz saves as a managed machine's data. Version 0 machines save exactly what they always did
    /// (byte 2, the size as 4 bytes, the content); a machine with a StateVersion above 0 saves byte 3, the size, the content and the version (4 bytes).
    /// </summary>
    public static class StateEnvelope
    {
        public const byte Plain = 2, Versioned = 3;

        public static byte[] Write(byte[] content, int version)
        {
            int size = content?.Length ?? 0;
            var r = new byte[5 + size + (version > 0 ? 4 : 0)];
            r[0] = version > 0 ? Versioned : Plain;
            System.BitConverter.TryWriteBytes(new System.Span<byte>(r, 1, 4), size);
            if (size > 0) System.Buffer.BlockCopy(content, 0, r, 5, size);
            if (version > 0) System.BitConverter.TryWriteBytes(new System.Span<byte>(r, 5 + size, 4), version);
            return r;
        }

        /// <summary>The content and the version of a saved envelope (version 0 for the plain one, and for anything too short to say).</summary>
        public static (byte[] Content, int Version) Read(byte[] value)
        {
            if (value == null || value.Length < 5) return (System.Array.Empty<byte>(), 0);
            if (value[0] != Versioned)
            {
                var plain = new byte[value.Length - 5];
                System.Buffer.BlockCopy(value, 5, plain, 0, plain.Length);
                return (plain, 0);
            }
            int size = System.BitConverter.ToInt32(value, 1);
            if (size < 0 || 5L + size > value.Length) size = value.Length - 5;
            var content = new byte[size];
            System.Buffer.BlockCopy(value, 5, content, 0, size);
            int version = value.Length >= 5 + size + 4 ? System.BitConverter.ToInt32(value, 5 + size) : 0;
            return (content, version);
        }
    }
}
