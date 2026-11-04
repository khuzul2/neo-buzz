// Fork (docs/specs/studio-mvp.md §7, test reliability): a closed studio window lets go. Nothing in the
// engine or the studio's statics keeps it or its views once it is closed, whether its tabs were shown or
// not: their subscriptions to the engine are gone, and so are their timers (a running DispatcherTimer is
// held by the dispatcher and holds its view, so a view that is released has none running). Two views used
// to subscribe as they were made and let go only once shown and hidden again (Song Notes) or once their
// window closed after their tab was shown (Pattern): every main window whose tabs weren't all shown stayed
// alive, its views following the engine, for as long as the process ran. A song's Modern Pattern Editor
// lets go of the engine's song when it is released (it took the song again on the next machine added, and
// stayed). And the double clicks and the clicks the tests make are what they say, however long a press
// takes (HeadlessInput.cs).

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    using static MachineViewTests;

    public class WindowLifetimeTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        [AvaloniaTest]
        [TestCase(false)]
        [TestCase(true)]
        public void AClosedMainWindowIsReleasedWithItsViewsAndTheirSubscriptions(bool showEveryTab)
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;     // never keep unsaved changes here
            try
            {
                SongFiles.New().Wait();
                OpenAndClose(true);     // what the studio makes once per engine (the sequence editor's song state) is made
                Settle();
                int before = StudioSubscribers(out var held);

                var (window, views) = OpenAndClose(showEveryTab);
                for (int i = 0; i < 4; i++)
                {
                    Settle();
                    System.GC.Collect();
                    System.GC.WaitForPendingFinalizers();
                }

                int after = StudioSubscribers(out var still);
                Assert.That(after, Is.EqualTo(before), "the closed window's views no longer listen to the engine; still there: " + string.Join(", ", still.Except(held)));
                Assert.That(views.Where(v => v.Value.TryGetTarget(out _)).Select(v => v.Key), Is.Empty, "its views are released");
                Assert.That(window.TryGetTarget(out _), Is.False, "the closed window is released");
            }
            finally
            {
                Dialogs.Responder = null;
            }
        }

        /// <summary>
        /// A song's Modern Pattern Editor (the editor machine's own, with its view) is released with the song: it
        /// used to stay subscribed to the engine's song, which outlives songs, so every editor of a run stayed.
        /// </summary>
        [AvaloniaTest]
        public void ANewSongReleasesThePatternEditorOfTheLastOne()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            try
            {
                SongFiles.New().Wait();
                Settle();
                int before = EditorsOnTheSong();
                var (editor, view) = EditAPatternThenNewSong();
                for (int i = 0; i < 4; i++)
                {
                    Settle();
                    System.GC.Collect();
                    System.GC.WaitForPendingFinalizers();
                }
                Assert.That(EditorsOnTheSong(), Is.EqualTo(before), "the old song's editor no longer listens to the song");
                Assert.That(editor.TryGetTarget(out _), Is.False, "the old song's pattern editor is released");
                Assert.That(view.TryGetTarget(out _), Is.False, "and its view");
            }
            finally
            {
                Dialogs.Responder = null;
            }
        }

        /// <summary>A pattern shown in the Modern Pattern Editor in a main window; the window closed, a new song; weakly held.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (System.WeakReference<object> Editor, System.WeakReference<object> View) EditAPatternThenNewSong()
        {
            Core.SongCore.CreateMachine("Neo Poly", null, "Lifetime Poly", null, null, null, -1, 0.3f, 0.3f);
            var machine = Core.SongCore.MachinesList.First(m => m.Name == "Lifetime Poly");
            machine.CreatePattern("00", 16);
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            Views.PatternView.Edit(machine.Patterns.First());
            Settle();
            object view = Views.PatternView.Page.EditorControl;
            Assert.That(view, Is.Not.Null, "the Pattern tab shows the Modern Pattern Editor");
            object editor = ((dynamic)view).Editor;
            Assert.That(editor?.GetType().FullName, Is.EqualTo("WDE.ModernPatternEditor.PatternEditor"));
            window.Close();
            Settle();
            SongFiles.New().Wait();
            Settle();
            return (new System.WeakReference<object>(editor), new System.WeakReference<object>(view));
        }

        /// <summary>The Modern Pattern Editors subscribed to the engine's song (SongCore.MachineAdded).</summary>
        static int EditorsOnTheSong()
        {
            var f = typeof(SongCore).GetField("MachineAdded", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            var d = f?.GetValue(Core.SongCore) as System.Delegate;
            Assert.That(f, Is.Not.Null, "SongCore.MachineAdded is a field-like event");
            return d?.GetInvocationList().Count(x => x.Target?.GetType().FullName == "WDE.ModernPatternEditor.PatternEditor") ?? 0;
        }

        /// <summary>A main window on the song, its tabs shown (or only the first), closed; weakly held.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (System.WeakReference<MainWindow> Window, Dictionary<string, System.WeakReference<object>> Views) OpenAndClose(bool showEveryTab)
        {
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            var tabs = window.Tabs.Items.Cast<TabItem>().ToList();
            Assert.That(tabs.Select(t => t.Header), Is.EqualTo(new[] { "Machines", "Sequence", "Session", "Pattern", "Song Notes" }));
            if (showEveryTab)
                foreach (var tab in tabs)
                {
                    tab.IsSelected = true;
                    Settle();
                }
            window.Tabs.SelectedIndex = 0;
            Settle();
            var views = tabs.ToDictionary(t => (string)t.Header, t => new System.WeakReference<object>(t.Content));
            window.Close();
            Settle();
            Assert.That(window.IsVisible, Is.False, "the window closed");
            return (new System.WeakReference<MainWindow>(window), views);
        }

        /// <summary>
        /// The studio's subscriptions to the engine: delegates held by the engine's objects (the engine, the song,
        /// its machines, patterns and sequences, the Session View's layout, session and MIDI, the song's notes)
        /// and by the statics of the engine, BuzzGUI.Common and the studio, whose target is the studio's.
        /// </summary>
        static int StudioSubscribers(out List<string> where)
        {
            var studio = typeof(MainWindow).Assembly;
            var found = new List<string>();
            int count = 0;
            void Count(object owner, FieldInfo f, object value)
            {
                if (value is not System.Delegate d) return;
                foreach (var x in d.GetInvocationList())
                    if (x.Target != null && x.Target.GetType().Assembly == studio)
                    {
                        count++;
                        found.Add($"{(owner?.GetType() ?? f.DeclaringType).Name}.{f.Name} → {x.Method.DeclaringType?.Name}");
                    }
            }
            var song = Core.SongCore;
            var objects = new List<object> { Core, song, Core.SongNotes, Core.SessionLayout, Core.Session, Core.SessionMidi };
            foreach (var m in song.MachinesList)
            {
                objects.Add(m);
                objects.AddRange(m.PatternsList);
            }
            objects.AddRange(song.SequencesList);
            foreach (var o in objects.Where(o => o != null))
                for (var t = o.GetType(); t != null; t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (typeof(System.Delegate).IsAssignableFrom(f.FieldType))
                            Count(o, f, f.GetValue(o));
            foreach (var asm in new[] { typeof(ReBuzzCore).Assembly, typeof(BuzzGUI.Common.Global).Assembly, studio })
                foreach (var t in asm.GetTypes().Where(t => !t.ContainsGenericParameters))
                    foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (typeof(System.Delegate).IsAssignableFrom(f.FieldType))
                            Count(null, f, f.GetValue(null));
            where = found.GroupBy(x => x).Select(g => $"{g.Key} ×{g.Count()}").ToList();
            return count;
        }

        /// <summary>
        /// A double click is one however long its first press takes (here 600 ms in its handler, longer than
        /// Avalonia's 500 ms), and two clicks are two single clicks however quickly they come.
        /// </summary>
        [AvaloniaTest]
        public void ADoubleClickIsOneHoweverSlowAndTwoClicksAreTwo()
        {
            var counts = new List<int>();
            var target = new Border { Background = Avalonia.Media.Brushes.Gray, Width = 200, Height = 100 };
            target.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
            {
                counts.Add(e.ClickCount);
                if (slow && e.ClickCount == 1) System.Threading.Thread.Sleep(600);
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            var window = new Window { Width = 300, Height = 200, Content = target };
            window.Show();
            try
            {
                Settle();
                var p = target.TranslatePoint(new Point(100, 50), window)!.Value;
                slow = true;
                DoubleClick(window, p);
                Assert.That(counts, Is.EqualTo(new[] { 1, 2 }), "a slow first press, then the double click's second");
                slow = false;
                counts.Clear();
                Click(window, p);
                Click(window, p);
                Assert.That(counts, Is.EqualTo(new[] { 1, 1 }), "two clicks, each a first click");
                counts.Clear();
                DoubleClick(window, p);
                Assert.That(counts, Is.EqualTo(new[] { 1, 2 }), "a double click right after them is still one");
            }
            finally
            {
                slow = false;
                window.Close();
                Settle();
            }
        }

        static bool slow;
    }
}
