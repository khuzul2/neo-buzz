// Fork (docs/specs/studio-mvp.md §2): the engine's file choice (IFileNameChoice) through
// Avalonia's storage provider. The studio's own flows pick a file first (async) and hand it over
// as Next; anything else the engine asks for gets a picker run to its end (Dialogs.Modal).

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Platform
{
    internal sealed class StudioFileChoice : IFileNameChoice
    {
        readonly bool save;

        public StudioFileChoice(bool save) => this.save = save;

        /// <summary>The answer to the next question, chosen beforehand (one use).</summary>
        public string Next { get; set; }

        public ChosenValue<string> SelectFileName()
        {
            string path = Next;
            Next = null;
            path ??= Dialogs.Modal(() => save ? SongFiles.PickSave(null) : SongFiles.PickOpen());
            return path != null ? ChosenValue<string>.Just(path) : ChosenValue<string>.Nothing;
        }
    }
}
