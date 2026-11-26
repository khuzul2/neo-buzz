#nullable disable
// Neo Envelope's versioned state (docs/specs/machines.md section 3.4). Version 1 (docs/specs/notes.md G4, CU6a): "Velocity" was appended, default 0 %,
// the amount a note's velocity scales the output in Note mode. A song saved before it takes 0 % too, so every note opens the envelope fully, as before.

using NeoBuzz.Dsp;

namespace NeoBuzz.EnvelopeMachine
{
    internal static class EnvelopeMigrations
    {
        public static readonly string[] Version0 = { "mode", "attack", "decay", "sustain", "release", "threshold", "curve", "gate", "glide" };

        public static readonly StateMigrations Chain = new StateMigrations(Params.Table)
            .Step(Version0, s => s.Add("velocity", 0));
    }
}
