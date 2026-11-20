// The pitch evaluation (docs/specs/pitch.md §7): runs the shared pitch engine on MDB-stem-synth, the Amateur Vocal Percussion
// dataset and VocalSet, and writes a Markdown report. The workflow .github/workflows/pitch-eval.yml downloads the data on the
// runner; nothing here is committed or uploaded but the report (and a CSV of the per-stem figures).
//
//   PitchEval [--mdb DIR] [--avp DIR] [--vocalset ZIP] [--tune ZIP] [--tune-excerpts DIR] [--out report.md] [--csv stems.csv] [--mdb-limit N] [--avp-participants N] [--vocalset-per-group N]

using System.Text;

namespace NeoBuzz.PitchEval
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 3 && args[0] == "--onsets") { Avp.Debug(args[1], args[2]); return 0; }
            if (args.Length == 4 && args[0] == "--pack") { TuneGates.PackDir(args[1], args[2], args[3]); return 0; }
            if (args.Length == 4 && args[0] == "--track") { Dbg.Track(args[1], args[2], int.Parse(args[3])); return 0; }
            if (args.Length == 3 && args[0] == "--am1-map") { Am1.Map(args[1], args[2]); return 0; }
            string am1Gs = null, am1Idmt = null, am1Mdb = null, plots = null; int gsEvery = 1, gsOffset = 0; string gsPlayers = ""; bool gsMono = false;
            string mdb = null, avp = null, vs = null, tune = null, excerpts = null, outPath = "pitch-eval.md", csvPath = null;
            int mdbLimit = 0, avpLimit = 0, vsPer = 2;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + a);
                switch (a)
                {
                    case "--am1-guitarset": am1Gs = Next(); break;
                    case "--am1-idmt": am1Idmt = Next(); break;
                    case "--am1-mdb": am1Mdb = Next(); break;
                    case "--am1-plots": plots = Next(); break;
                    case "--gs-players": gsPlayers = Next(); break;
                    case "--gs-mono": gsMono = true; break;
                    case "--gs-every": gsEvery = int.Parse(Next()); break;
                    case "--gs-offset": gsOffset = int.Parse(Next()); break;
                    case "--mdb": mdb = Next(); break;
                    case "--avp": avp = Next(); break;
                    case "--vocalset": vs = Next(); break;
                    case "--tune": tune = Next(); break;
                    case "--tune-excerpts": excerpts = Next(); break;
                    case "--out": outPath = Next(); break;
                    case "--csv": csvPath = Next(); break;
                    case "--mdb-limit": mdbLimit = int.Parse(Next()); break;
                    case "--avp-participants": avpLimit = int.Parse(Next()); break;
                    case "--vocalset-per-group": vsPer = int.Parse(Next()); break;
                    default: Console.Error.WriteLine("unknown argument " + a); return 2;
                }
            }
            if (args.Length == 3 && args[0] == "--onsets") { Avp.Debug(args[1], args[2]); return 0; }
            var md = new StringBuilder();
            var csv = new StringBuilder();
            md.AppendLine("# Pitch engine evaluation on real recordings");
            md.AppendLine();
            md.AppendLine($"Run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC on {Environment.ProcessorCount} cores, .NET {Environment.Version}. The engine is `Machines/NeoBuzz.Dsp` (PitchTracker, OnsetDetector, TriggerFeatures and TriggerClassifier, VowelEstimator) as the machines will use it, fed 256 samples at a time. The data are downloaded on the runner and are not kept; the licences are in the last section.");
            md.AppendLine();
            if (am1Gs != null && gsMono) Am1.GuitarMono(am1Gs, gsPlayers, md, plots);
            if (am1Gs != null) Am1.Guitar(am1Gs, gsEvery, gsOffset, md, plots, gsPlayers);
            if (am1Idmt != null) Am1.Drums(am1Idmt, md, plots);
            if (am1Mdb != null) Am1.Mdb(am1Mdb, mdbLimit, md);
            if (mdb != null) Mdb.Run(mdb, mdbLimit, md, csv);
            if (avp != null) Avp.Run(avp, avpLimit, md);
            if (vs != null) VocalSet.Run(vs, vsPer, md);
            if (tune != null) TuneEval.Run(tune, vsPer, excerpts, md);
            md.AppendLine("## Data and licences");
            md.AppendLine();
            md.AppendLine("Licence of each record as its Zenodo page states it, checked when the workflow was written (docs/specs/pitch.md §7.5):");
            md.AppendLine();
            md.AppendLine("- **MDB-stem-synth** (https://zenodo.org/records/1481172), J. Salamon, R. Bittner, J. Bonada, J. J. Bosch, E. Gomez, J. P. Bello: Creative Commons Attribution-NonCommercial 4.0 (CC BY-NC 4.0). Used here for measurement only; nothing derived from it is shipped.");
            md.AppendLine("- **Amateur Vocal Percussion Dataset** (https://zenodo.org/record/5036529), A. Delgado, S. McDonald, N. Xu, M. Sandler: CC BY 4.0.");
            md.AppendLine("- **VocalSet** (https://zenodo.org/records/1442513), J. Wilkins, P. Seetharaman, A. Wahl, B. Pardo: CC BY 4.0. Version 1.1 (VocalSet11.zip, 2 GB) is the one read; version 1.2 (6 GB) holds the same recordings in three folder layouts.");
            File.WriteAllText(outPath, md.ToString());
            if (csvPath != null && csv.Length > 0) File.WriteAllText(csvPath, csv.ToString());
            Console.WriteLine("wrote " + outPath);
            return 0;
        }
    }
}
