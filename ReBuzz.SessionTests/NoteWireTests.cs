using System.Xml.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.NoteWires;

namespace ReBuzz.SessionTests
{
    /// <summary>The pure parts of note wires (docs/specs/note-wires.md): the state, the rules that refuse a wire, the queue and the merge.</summary>
    [TestFixture]
    public class NoteWireTests
    {
        static readonly Dictionary<string, NotePorts> Ports = new()
        {
            ["Src"] = new NotePorts(false, true),
            ["A"] = new NotePorts(true, true),
            ["B"] = new NotePorts(true, true),
            ["C"] = new NotePorts(true, true),
            ["Synth"] = new NotePorts(true, false),
            ["Synth2"] = new NotePorts(true, false),
            ["Fx"] = new NotePorts(false, false),
            ["Ctl"] = new NotePorts(true, true, Control: true),
        };

        static NotePorts? PortsOf(string n) => Ports.TryGetValue(n, out var p) ? p : null;
        static readonly NoteEdge[] NoAudio = Array.Empty<NoteEdge>();

        [TestCase(16)]
        [TestCase(64)]
        [TestCase(256)]
        [TestCase(512)]
        public void AnEventKeepsItsOffsetThroughAChainOfThree(int block)
        {
            // Src -> A -> B -> C: each hop's queue is merged into the next machine's input and re-emitted at the same offset.
            var rng = new Random(block);
            var sent = new List<NoteMessage>();
            var q0 = new NoteQueue();
            for (int i = 0; i < 9; i++)
            {
                var e = NoteMessage.On(rng.Next(block), i & 15, 40 + i, 100);
                sent.Add(e);
                q0.Add(e);
            }
            var scratch = new NoteMessage[NoteQueue.Capacity];
            NoteQueue cur = q0;
            for (int hop = 0; hop < 3; hop++)
            {
                int n = NoteMerge.Merge(new[] { cur }, 1, scratch);
                var next = new NoteQueue();
                for (int i = 0; i < n; i++) next.Add(scratch[i]);
                cur = next;
            }
            Assert.That(cur.Count, Is.EqualTo(sent.Count));
            var want = sent.OrderBy(e => e.Offset).ToList();       // stable: Linq's OrderBy is
            for (int i = 0; i < cur.Count; i++)
            {
                Assert.That(cur.Events[i].Offset, Is.EqualTo(want[i].Offset), $"event {i}");
                Assert.That(cur.Events[i].Data1, Is.EqualTo(want[i].Data1), $"event {i} (stable among equal offsets)");
            }
        }

        [Test]
        public void TwoWiresIntoOneMachineMergeByOffsetAndTieByWireOrder()
        {
            var a = new NoteQueue(); var b = new NoteQueue();
            a.Add(NoteMessage.On(10, 0, 60, 90)); a.Add(NoteMessage.On(30, 0, 62, 90));
            b.Add(NoteMessage.On(10, 0, 72, 90)); b.Add(NoteMessage.On(20, 0, 74, 90));
            var dest = new NoteMessage[16];
            int n = NoteMerge.Merge(new[] { a, b }, 2, dest);
            Assert.That(dest.Take(n).Select(e => (e.Offset, (int)e.Data1)), Is.EqualTo(new[] { (10, 60), (10, 72), (20, 74), (30, 62) }));
        }

        [Test]
        public void AFullQueueDropsAndCountsAndMergeIsBounded()
        {
            var q = new NoteQueue();
            for (int i = 0; i < NoteQueue.Capacity + 5; i++) q.Add(NoteMessage.On(i % 64, 0, 60, 100));
            Assert.That(q.Count, Is.EqualTo(NoteQueue.Capacity));
            Assert.That(q.Dropped, Is.EqualTo(5));
            var small = new NoteMessage[8];
            Assert.That(NoteMerge.Merge(new[] { q }, 1, small), Is.EqualTo(8));
        }

        [Test]
        public void MergeAllocatesNothing()
        {
            var qs = Enumerable.Range(0, 16).Select(k => { var q = new NoteQueue(); for (int i = 0; i < 20; i++) q.Add(NoteMessage.On((i * 7 + k) % 256, 0, 60 + i, 100)); return q; }).ToArray();
            var dest = new NoteMessage[16384];
            NoteMerge.Merge(qs, qs.Length, dest);      // warm up
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int r = 0; r < 100; r++) NoteMerge.Merge(qs, qs.Length, dest);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0));
        }

        [Test]
        public void AWireNeedsASourceThatSendsAndATargetThatTakes()
        {
            var s = NoteWireState.Empty;
            Assert.That(NoteWireRules.CheckWire(s, NoAudio, PortsOf, "Src", "Synth"), Is.Null);
            Assert.That(NoteWireRules.CheckWire(s, NoAudio, PortsOf, "Synth", "Src"), Does.Contain("doesn't send"));
            Assert.That(NoteWireRules.CheckWire(s, NoAudio, PortsOf, "Src", "Fx"), Does.Contain("doesn't take"));
            Assert.That(NoteWireRules.CheckWire(s, NoAudio, PortsOf, "Src", "Nobody"), Does.Contain("no machine named Nobody"));
            Assert.That(NoteWireRules.CheckWire(s, NoAudio, PortsOf, "Src", "Src"), Is.Not.Null);
            Assert.That(NoteWireRules.CheckWire(s, NoAudio, PortsOf, "Src", "Ctl"), Does.Contain("control"));
            Assert.That(NoteWireRules.CheckWire(s.WithWire(new NoteEdge("Src", "Synth")), NoAudio, PortsOf, "Src", "Synth"), Does.Contain("already"));
        }

        [Test]
        public void ALoopThroughNoteWiresAloneIsRefusedWithAReason()
        {
            var s = NoteWireState.Empty.WithWire(new NoteEdge("A", "B")).WithWire(new NoteEdge("B", "C"));
            string why = NoteWireRules.CheckWire(s, NoAudio, PortsOf, "C", "A");
            Assert.That(why, Does.Contain("loop"));
            Assert.That(NoteWireRules.CheckWire(s, NoAudio, PortsOf, "A", "C"), Is.Null, "a shortcut is no loop");
        }

        [Test]
        public void ALoopThroughTheAudioAndTheNoteWiresTogetherIsRefused()
        {
            // Audio: Synth2 -> Fx. Note: A -> Synth2 ... a note wire Fx? Fx takes no notes; use A: audio A -> B, note B -> A would loop.
            var audio = new[] { new NoteEdge("A", "B") };
            Assert.That(NoteWireRules.CheckWire(NoteWireState.Empty, audio, PortsOf, "B", "A"), Does.Contain("loop"));
            // And an audio connection that would close a loop with a note wire is refused too.
            var s = NoteWireState.Empty.WithWire(new NoteEdge("A", "B"));
            Assert.That(NoteWireRules.CheckAudio(s, NoAudio, "B", "A"), Does.Contain("loop"));
            Assert.That(NoteWireRules.CheckAudio(s, NoAudio, "A", "B"), Is.Null);
        }

        [Test]
        public void AChainDerivesItsEdgesAndRemovingAnInsertClosesTheGap()
        {
            var s = NoteWireState.Empty.WithChain("Synth", new[] { "A", "B" });
            Assert.That(s.ChainEdges(), Is.EqualTo(new[] { new NoteEdge("A", "B"), new NoteEdge("B", "Synth") }));
            Assert.That(s.InstrumentOf("B"), Is.EqualTo("Synth"));
            var removed = s.WithChain("Synth", s.ChainOf("Synth").Where(n => n != "A"));
            Assert.That(removed.ChainEdges(), Is.EqualTo(new[] { new NoteEdge("B", "Synth") }));
            var empty = removed.WithChain("Synth", Array.Empty<string>());
            Assert.That(empty.IsEmpty, Is.True);
            Assert.That(empty.AllEdges(), Is.Empty);
        }

        [Test]
        public void AnInsertBelongsToOneChain()
        {
            var s = NoteWireState.Empty.WithChain("Synth", new[] { "A", "B" }).WithChain("Synth2", new[] { "B", "C" });
            Assert.That(s.ChainOf("Synth"), Is.EqualTo(new[] { "A" }));
            Assert.That(s.ChainOf("Synth2"), Is.EqualTo(new[] { "B", "C" }));
        }

        [Test]
        public void ChainChecksRefuseNonInsertsAndLoops()
        {
            var s = NoteWireState.Empty;
            Assert.That(NoteWireRules.CheckChain(s, NoAudio, PortsOf, "Synth", new[] { "A", "B" }), Is.Null);
            Assert.That(NoteWireRules.CheckChain(s, NoAudio, PortsOf, "Synth", new[] { "Synth2" }), Does.Contain("can't be a note insert"));
            Assert.That(NoteWireRules.CheckChain(s, NoAudio, PortsOf, "Fx", new[] { "A" }), Does.Contain("doesn't take"));
            Assert.That(NoteWireRules.CheckChain(s, NoAudio, PortsOf, "Synth", new[] { "A", "A" }), Does.Contain("once"));
            // A free wire Synth? no: B -> A exists; a chain A then B would run A before B: a loop.
            var wired = s.WithWire(new NoteEdge("B", "A"));
            Assert.That(NoteWireRules.CheckChain(wired, NoAudio, PortsOf, "Synth", new[] { "A", "B" }), Does.Contain("loop"));
            Assert.That(NoteWireRules.CheckChain(wired, NoAudio, PortsOf, "Synth", new[] { "B", "A" }), Is.Null);
        }

        [Test]
        public void RenameCarriesWiresAndChainsAndPruneDropsWhatIsGone()
        {
            var s = NoteWireState.Empty.WithWire(new NoteEdge("Src", "Synth")).WithChain("Synth", new[] { "A", "B" });
            var r = s.Renamed("Synth", "Lead").Renamed("A", "Chord");
            Assert.That(r.Wires, Is.EqualTo(new[] { new NoteEdge("Src", "Lead") }));
            Assert.That(r.ChainOf("Lead"), Is.EqualTo(new[] { "Chord", "B" }));
            var pruned = r.Pruned(n => n != "Chord" && n != "Src");
            Assert.That(pruned.Wires, Is.Empty);
            Assert.That(pruned.ChainOf("Lead"), Is.EqualTo(new[] { "B" }), "the chain closes up around a deleted insert");
            Assert.That(r.Pruned(n => n != "Lead").IsEmpty, Is.True, "a deleted instrument takes its chain");
        }

        [Test]
        public void TheSavedFormRoundTrips()
        {
            var s = NoteWireState.Empty.WithWire(new NoteEdge("Src", "Synth")).WithChain("Synth2", new[] { "A", "B", "C" });
            var xml = s.ToXml(1).ToString(SaveOptions.DisableFormatting);
            var back = NoteWireState.FromXml(XElement.Parse(xml));
            Assert.That(back.SameAs(s), Is.True);
            Assert.That(xml, Does.StartWith("<NoteWires version=\"1\">"));
        }
    }
}
