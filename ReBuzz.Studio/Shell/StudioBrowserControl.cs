// Fork (docs/specs/browser.md, BR1): the studio's browser for the agent's browser_search and browser_places (IBrowserControl): the same
// MachineBrowserModel the panel and the canvas popup read, so the agent finds what the person would.

using System;
using System.Collections.Generic;
using System.Linq;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Browser;
using ReBuzz.Studio.Views.Browser;

namespace ReBuzz.Studio.Shell
{
    internal sealed class StudioBrowserControl : IBrowserControl
    {
        readonly StudioHost studio;

        public StudioBrowserControl(StudioHost studio) => this.studio = studio;

        MachineBrowserModel Model => MachineBrowserModel.For(studio.Core, studio.Registry);

        public IReadOnlyList<BrowserHit> Search(string query, string kind, int limit)
        {
            BrowserKind? k = kind != null && BrowserDragData.TryKind(kind, out var parsed) ? parsed : null;
            return Model.SearchAll(query, k, limit).Take(limit).Select(i => new BrowserHit(BrowserDragData.KindName(i.Kind), i.Name, i.Id, i.Group.Length > 0 ? i.Group : null,
                i.Detail, i.Path, i.Preset)).ToList();
        }

        public IReadOnlyList<BrowserPlace> Places()
        {
            var lib = Model.Library;
            return lib.Places.Select(p => new BrowserPlace(p, lib.Index.Entries.Count(e => e.Root == p), System.IO.Directory.Exists(p))).ToList();
        }

        public bool AddPlace(string path) => Model.Library.AddPlace(path);

        public bool RemovePlace(string path) => Model.Library.RemovePlace(path);

        public (string Status, int Indexed) Index()
        {
            var lib = Model.Library;
            return (lib.IsScanning ? lib.Progress.Text : "idle", lib.Index.Count);
        }

        public bool WaitForIndex(int milliseconds) => Model.Library.WaitForScan(milliseconds);
    }
}
