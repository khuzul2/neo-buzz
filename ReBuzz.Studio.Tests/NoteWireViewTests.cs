// Fork (docs/specs/note-wires.md §6, NW1): note wires in the Machines view: ports only on machines that have them, a drag from a note-out
// port to a note-in port makes a dotted wire (one undo step), a refused drop makes none and says why, a click selects a wire and Delete
// removes it, the "Note wires" toggle hides the wires and keeps the ports, and the Inspector lists the track's note chain.

using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReBuzz.Agent.Mcp;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Inspector;
using ReBuzz.Studio.Views.Machines;
using static ReBuzz.Studio.Tests.MachineViewTests;
using static ReBuzz.Studio.Tests.UxMt1Tests;

namespace ReBuzz.Studio.Tests
{
    public class NoteWireViewTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static void Drag(MainWindow window, Point from, Point to)
        {
            window.MouseMove(from);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2));
            window.MouseMove(to);
            window.MouseUp(to, MouseButton.Left);
            Settle();
        }

        [AvaloniaTest]
        public void PortsAndWiresInTheMachinesView()
        {
            var (window, view) = OpenEmpty();
            try
            {
                NoteWiresPreference.On = true;
                var voice = Put("Neo Voice to MIDI", "Voice", -0.6, -0.5);
                var poly = Put("Neo Poly", "Lead", 0.3, 0.3);
                Settle();
                var canvas = view.Canvas;
                Assert.That(canvas.HasNoteOut(voice), Is.True);
                Assert.That(canvas.HasNoteIn(voice), Is.False, "a machine that only sends has no note-in port");
                Assert.That(canvas.HasNoteIn(poly), Is.True);
                Assert.That(canvas.HasNoteOut(poly), Is.False);
                Assert.That(canvas.HasNoteIn(Master) || canvas.HasNoteOut(Master), Is.False, "the Master has no note ports");

                // A refused drop: the Master takes no notes. No wire, and the canvas says so.
                Drag(window, view.FromCanvas(canvas.NoteOutPortOf(voice), window), view.PointOf(Master, window));
                Assert.That(Core.NoteWires.State.Wires, Is.Empty);

                // A drag to the instrument makes the wire, as one undo step.
                Drag(window, view.FromCanvas(canvas.NoteOutPortOf(voice), window), view.PointOf(poly, window));
                Assert.That(Core.NoteWires.State.Wires.Select(w => (w.From, w.To)), Is.EqualTo(new[] { ("Voice", "Lead") }));
                Core.SongCore.ActionStack.Undo();
                Assert.That(Core.NoteWires.State.Wires, Is.Empty, "one undo takes the wire away");
                Assert.That(Machine("Voice"), Is.Not.Null, "and only the wire");
                Core.SongCore.ActionStack.Redo();
                Assert.That(Core.NoteWires.State.Wires.Count, Is.EqualTo(1));
                Assert.That(canvas.NoteEdges().Count, Is.EqualTo(1));

                // Select the wire with a click on its middle; Delete removes it.
                var a = canvas.NoteOutPortOf(voice); var b = canvas.NoteInPortOf(poly);
                var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                Assert.That(canvas.NoteWireAt(mid), Is.Not.Null, "the wire is under its middle");
                Click(window, view.FromCanvas(mid, window));
                Assert.That(canvas.SelectedNoteWire, Is.EqualTo(("Voice", "Lead")));
                canvas.DeleteSelection();
                Assert.That(Core.NoteWires.State.Wires, Is.Empty);
                Core.SongCore.ActionStack.Undo();
                Assert.That(Core.NoteWires.State.Wires.Count, Is.EqualTo(1), "the removal is one step, and it undoes");

                // The toggle hides the wires, not the ports.
                NoteWiresPreference.On = false;
                Settle();
                Assert.That(canvas.NoteWiresShown, Is.False);
                Assert.That(canvas.NoteWireAt(mid), Is.Null, "a hidden wire can't be picked");
                Assert.That(canvas.HasNoteIn(poly), Is.True, "the ports stay");
                NoteWiresPreference.On = true;
                Settle();
                Assert.That(canvas.NoteWiresShown, Is.True);
            }
            finally
            {
                NoteWiresPreference.On = true;
                Close(window);
            }
        }

        [AvaloniaTest]
        public void TheMenuAndTheInspectorListTheChain()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var voice = Put("Neo Voice to MIDI", "Voice", -0.6, -0.5);
                var poly = Put("Neo Poly", "Lead", 0.3, 0.3);
                Settle();
                var menu = new MachineMenus(Core, view.Canvas).ForMachine(voice, new[] { voice });
                var send = menu.Items.OfType<Avalonia.Controls.MenuItem>().FirstOrDefault(i => (string)i.Header == "Send notes to");
                Assert.That(send, Is.Not.Null, "a machine that sends notes has Send notes to");
                Assert.That(send.Items.OfType<Avalonia.Controls.MenuItem>().Select(i => (string)i.Header), Does.Contain("Lead"));
                var polyMenu = new MachineMenus(Core, view.Canvas).ForMachine(poly, new[] { poly });
                Assert.That(polyMenu.Items.OfType<Avalonia.Controls.MenuItem>().Any(i => (string)i.Header == "Send notes to"), Is.False, "an instrument sends none");

                Assert.That(Core.NoteWires.Connect("Voice", "Lead"), Is.Null);
                var section = new NoteChainSection(Core);
                section.Show(poly);
                Settle();
                Assert.That(section.Rows, Is.EqualTo(new[] { "in: Voice" }));
                section.Show(Master);
                Assert.That(section.IsVisible, Is.False, "the Master has no notes section");
                section.Detach();
            }
            finally { Close(window); }
        }

        // ------------------------------------------------------------------ the agent's tools

        static (bool Error, string Text, JsonObject Data) Try(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            if (r["error"] is JsonObject e) return (true, e["message"]!.GetValue<string>(), new JsonObject());
            var result = r["result"]!.AsObject();
            return (result["isError"]?.GetValue<bool>() == true, result["content"]![0]!["text"]!.GetValue<string>(), result["structuredContent"] as JsonObject ?? new JsonObject());
        }

        [AvaloniaTest]
        public void TheAgentToolsListConnectAndEditTheChainWithTheSameChecksAndUndo()
        {
            var (window, view) = OpenEmpty();
            try
            {
                Put("Neo Voice to MIDI", "Voice", -0.6, -0.5);
                var lead = Put("Neo Poly", "Lead", 0.3, 0.3);
                Wire("Lead>Master");
                Settle();
                var list = Try("note_wires");
                Assert.That(list.Error, Is.False, list.Text);
                Assert.That(list.Data["machines"]!.AsArray().Select(m => m!["machine"]!.GetValue<string>()), Is.EquivalentTo(new[] { "Voice", "Lead" }));

                var connect = Try("note_wire_connect", new JsonObject { ["source"] = "Voice", ["target"] = "Lead" });
                Assert.That(connect.Error, Is.False, connect.Text);
                Assert.That(Core.NoteWires.State.Wires.Count, Is.EqualTo(1));
                Assert.That(Try("note_wire_connect", new JsonObject { ["source"] = "Voice", ["target"] = "Lead" }).Error, Is.True, "twice is refused");
                Assert.That(Try("note_wire_connect", new JsonObject { ["source"] = "Lead", ["target"] = "Voice" }).Error, Is.True, "an instrument sends no notes");
                Assert.That(Try("note_wire_connect", new JsonObject { ["source"] = "Voice", ["target"] = "Nope" }).Error, Is.True, "an unknown name");
                Core.SongCore.ActionStack.Undo();
                Assert.That(Core.NoteWires.State.Wires, Is.Empty, "one undo step");
                Core.SongCore.ActionStack.Redo();

                var gone = Try("note_wire_disconnect", new JsonObject { ["source"] = "Voice", ["target"] = "Lead" });
                Assert.That(gone.Error, Is.False, gone.Text);
                Assert.That(Try("note_wire_disconnect", new JsonObject { ["source"] = "Voice", ["target"] = "Lead" }).Error, Is.True, "no wire to remove");

                // A chain needs a machine that takes and sends notes: Voice only sends, so it can't be an insert.
                var chain = Try("note_chain", new JsonObject { ["instrument"] = "Lead", ["inserts"] = new JsonArray("Voice") });
                Assert.That(chain.Error, Is.True, "an insert must take notes too: " + chain.Text);
                Assert.That(Try("note_chain", new JsonObject { ["instrument"] = "Lead" }).Error, Is.True, "nothing to do");
                var empty = Try("note_chain", new JsonObject { ["instrument"] = "Lead", ["inserts"] = new JsonArray() });
                Assert.That(empty.Error, Is.False, empty.Text);
            }
            finally { Close(window); }
        }
    }
}
