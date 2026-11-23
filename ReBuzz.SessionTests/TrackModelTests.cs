using ReBuzz.Core.Tracks;

namespace ReBuzz.SessionTests
{
    /// <summary>
    /// The track model's rules (docs/specs/ux-overhaul.md §11, MT1), on built graphs: a chain, fan-in to a group, a return fed by sends,
    /// a group that also receives a send, nested groups, a generator into two effects that meet again, control and unwired machines,
    /// a Neo Patch (one box), and the rule that every machine lands in exactly one place.
    /// </summary>
    [TestFixture]
    public class TrackModelTests
    {
        /// <summary>A graph from lines: "G Kick", "F Bus", "C LFO" (a generator, an effect, a control machine), "M Master", then wires "Kick>Bus".</summary>
        sealed class Built
        {
            public readonly TrackGraph Graph = new TrackGraph();
            readonly Dictionary<string, TrackNode> byName = new Dictionary<string, TrackNode>();

            public Built(string machines, string wires)
            {
                foreach (var part in machines.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    char kind = part[0];
                    string name = part.Substring(2);
                    byName[name] = Graph.Add(name, name, kind == 'G' ? TrackNodeKind.Generator : kind == 'M' ? TrackNodeKind.Master : TrackNodeKind.Effect, kind == 'C');
                }
                foreach (var part in wires.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var names = part.Split('>');
                    for (int i = 0; i + 1 < names.Length; i++) Graph.Connect(byName[names[i].Trim()], byName[names[i + 1].Trim()]);
                }
                Model = TrackModel.Compute(Graph);
            }

            public TrackModel Model { get; }

            public TrackNode this[string name] => byName[name];

            public TrackEntry Entry(string head) => Model.EntryWithHead(byName[head])!;

            public string Machines(string head) => string.Join(",", Entry(head).Machines.Select(m => m.Name));

            public string[] Names(string head) => Entry(head).Machines.Select(m => m.Name).ToArray();

            public string Members(string head) => string.Join(",", Entry(head).Members.Select(m => m.Name));

            public string Senders(string head) => string.Join(",", Entry(head).Senders.Select(m => m.Name));
        }

        [Test]
        public void AChainIsOneTrack()
        {
            var b = new Built("G Poly; F Drive; F Eq; M Master", "Poly>Drive>Eq>Master");
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Poly" }));
            Assert.That(b.Machines("Poly"), Is.EqualTo("Poly,Drive,Eq"));
            Assert.That(b.Entry("Poly").Chain, Is.EqualTo("Poly → Drive → Eq → Master"));
            Assert.That(b.Model.PlaceOf(b["Eq"]), Is.EqualTo(TrackPlace.Track));
            Assert.That(b.Model.PlaceOf(b["Master"]), Is.EqualTo(TrackPlace.Master));
            Assert.That(b.Model.Unrouted, Is.Empty);
        }

        [Test]
        public void FanInToAGroup()
        {
            var b = new Built("G Kick; G Snare; F Bus; F Comp; M Master", "Kick>Bus; Snare>Bus; Bus>Comp>Master");
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Kick", "track:Snare", "group:Bus" }));
            var bus = b.Entry("Bus");
            Assert.That(bus.Kind, Is.EqualTo(TrackKind.Group));
            Assert.That(b.Members("Bus"), Is.EqualTo("Kick,Snare"));
            Assert.That(bus.Senders, Is.Empty);
            Assert.That(b.Machines("Bus"), Is.EqualTo("Bus,Comp"), "what only the group feeds is the group's");
            Assert.That(b.Entry("Kick").Parent, Is.SameAs(bus));
            Assert.That(b.Entry("Kick").Chain, Is.EqualTo("Kick → Bus → Comp → Master"));
            Assert.That(b.Model.IsSend(b["Kick"], b["Bus"]), Is.False);
            Assert.That(bus.AllMachines.Select(m => m.Name), Is.EquivalentTo(new[] { "Bus", "Comp", "Kick", "Snare" }));
        }

        [Test]
        public void AReturnFedBySends()
        {
            var b = new Built("G Pad; G Lead; F Ens; F Verb; M Master", "Pad>Ens>Master; Lead>Master; Pad>Verb; Lead>Verb; Verb>Master");
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Pad", "track:Lead", "return:Verb" }));
            var verb = b.Entry("Verb");
            Assert.That(verb.Kind, Is.EqualTo(TrackKind.Return));
            Assert.That(verb.Members, Is.Empty);
            Assert.That(b.Senders("Verb"), Is.EqualTo("Pad,Lead"));
            Assert.That(b.Model.IsSend(b["Pad"], b["Verb"]), Is.True);
            Assert.That(b.Model.IsSend(b["Lead"], b["Verb"]), Is.True);
            Assert.That(b.Model.IsSend(b["Ens"], b["Master"]), Is.False);
            Assert.That(b.Entry("Pad").Parent, Is.Null);
            Assert.That(b.Entry("Pad").Chain, Is.EqualTo("Pad → Ens → Master"));
            Assert.That(b.Entry("Pad").SendsTo.Select(e => e.Name), Is.EqualTo(new[] { "Verb" }));
            Assert.That(verb.Chain, Is.EqualTo("Verb → Master"));
        }

        [Test]
        public void AGroupThatAlsoReceivesASendStaysAGroup()
        {
            var b = new Built("G Kick; G Snare; G Bass; F Bus; M Master", "Kick>Bus; Snare>Bus; Bus>Master; Bass>Master; Bass>Bus");
            var bus = b.Entry("Bus");
            Assert.That(bus.Kind, Is.EqualTo(TrackKind.Group));
            Assert.That(b.Members("Bus"), Is.EqualTo("Kick,Snare"));
            Assert.That(b.Senders("Bus"), Is.EqualTo("Bass"), "its incoming send is marked");
            Assert.That(b.Model.IsSend(b["Bass"], b["Bus"]), Is.True);
            Assert.That(b.Model.IsSend(b["Kick"], b["Bus"]), Is.False);
            Assert.That(b.Entry("Bass").Parent, Is.Null);
        }

        [Test]
        public void GroupsNest()
        {
            var b = new Built("G Kick; G Snare; G Bass; F Drums; F Mix; M Master", "Kick>Drums; Snare>Drums; Drums>Mix; Bass>Mix; Mix>Master");
            Assert.That(b.Entry("Mix").Kind, Is.EqualTo(TrackKind.Group));
            Assert.That(b.Members("Mix"), Is.EqualTo("Bass,Drums"));
            Assert.That(b.Members("Drums"), Is.EqualTo("Kick,Snare"));
            Assert.That(b.Entry("Kick").Parent, Is.SameAs(b.Entry("Drums")));
            Assert.That(b.Entry("Drums").Parent, Is.SameAs(b.Entry("Mix")));
            Assert.That(b.Entry("Kick").Depth, Is.EqualTo(2));
            Assert.That(b.Entry("Kick").Chain, Is.EqualTo("Kick → Drums → Mix → Master"));
            Assert.That(b.Entry("Mix").AllMachines.Count, Is.EqualTo(6 - 1), "all but the Master");
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Kick", "track:Snare", "track:Bass", "group:Drums", "group:Mix" }));
        }

        [Test]
        public void AGeneratorIntoTwoEffectsThatMeetAgainIsOneTrack()
        {
            var b = new Built("G Gen; F A; F B; F Mix; M Master", "Gen>A>Mix; Gen>B>Mix; Mix>Master");
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Gen" }));
            Assert.That(b.Names("Gen"), Is.EquivalentTo(new[] { "Gen", "A", "B", "Mix" }));
            Assert.That(b.Entry("Gen").Chain, Is.EqualTo("Gen → A + B → Mix → Master"));
        }

        [Test]
        public void AnEffectWithNothingWiredIntoItIsARootToo()
        {
            var b = new Built("F Input; G Gen; M Master", "Input>Master; Gen>Master");
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Input", "track:Gen" }));
            Assert.That(b.Model.PlaceOf(b["Input"]), Is.EqualTo(TrackPlace.Track));
        }

        [Test]
        public void ControlAndUnwiredMachinesAreUnrouted()
        {
            var b = new Built("G Gen; C LFO; G Alone; F Dead; F Orphan; M Master", "Gen>Master; Gen>Dead");
            var why = b.Model.Unrouted.ToDictionary(u => u.Node.Name, u => u.Why);
            Assert.That(why["LFO"], Is.EqualTo(UnroutedReason.Control));
            Assert.That(why["Alone"], Is.EqualTo(UnroutedReason.NotWired));
            Assert.That(why["Orphan"], Is.EqualTo(UnroutedReason.NotWired));
            Assert.That(why["Dead"], Is.EqualTo(UnroutedReason.NoPathToMaster), "wired in, but it leads nowhere");
            Assert.That(why.Count, Is.EqualTo(4));
            Assert.That(b.Machines("Gen"), Is.EqualTo("Gen"), "the dead end is not on Gen's track");
            Assert.That(b.Model.PlaceOf(b["Dead"]), Is.EqualTo(TrackPlace.Unrouted));
        }

        [Test]
        public void AControlMachineWiredToTheMasterIsStillUnrouted()
        {
            // The engine refuses such a wire; the model does not trust that.
            var b = new Built("G Gen; C LFO; M Master", "Gen>Master; LFO>Master");
            Assert.That(b.Model.UnroutedOf(b["LFO"])!.Why, Is.EqualTo(UnroutedReason.Control));
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Gen" }));
        }

        [Test]
        public void ANeoPatchIsOneBox()
        {
            // The patch's own nodes are not machines of the song: it is a generator like any other, and its modulator a control machine.
            var b = new Built("G Neo Patch; C Neo LFO; F Neo Verb; M Master", "Neo Patch>Neo Verb>Master");
            Assert.That(b.Machines("Neo Patch"), Is.EqualTo("Neo Patch,Neo Verb"));
            Assert.That(b.Model.UnroutedOf(b["Neo LFO"])!.Why, Is.EqualTo(UnroutedReason.Control));
        }

        [Test]
        public void ALoopIsUnroutedAndTheRestStillWorks()
        {
            var b = new Built("G Gen; F A; F B; M Master", "Gen>Master; A>B; B>A; B>Master");
            Assert.That(b.Model.UnroutedOf(b["A"])!.Why, Is.EqualTo(UnroutedReason.Loop));
            Assert.That(b.Model.UnroutedOf(b["B"])!.Why, Is.EqualTo(UnroutedReason.Loop));
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Gen" }));
        }

        [Test]
        public void ANoMasterMeansEverythingIsUnrouted()
        {
            var b = new Built("G Gen; F Fx", "Gen>Fx");
            Assert.That(b.Model.Entries, Is.Empty);
            Assert.That(b.Model.Unrouted.Count, Is.EqualTo(2));
            Assert.That(b.Model.Master, Is.Null);
        }

        [Test]
        public void ASharedMachineWhoseFeedersAreOneTrackIsNotShared()
        {
            // Kick feeds Bus twice over (through two effects): one owner, so Bus is part of Kick's track.
            var b = new Built("G Kick; F A; F B; F Bus; M Master", "Kick>A>Bus; Kick>B>Bus; Bus>Master");
            Assert.That(b.Model.Entries.Count, Is.EqualTo(1));
        }

        [Test]
        public void ATrackWithOneBranchAroundTheSharedMachineSends()
        {
            // Gen feeds A (into Bus) and B (straight to the Master): the track reaches the Master without Bus.
            var b = new Built("G Gen; G Other; F A; F B; F Bus; M Master", "Gen>A>Bus; Gen>B>Master; Other>Bus; Bus>Master");
            Assert.That(b.Members("Bus"), Is.EqualTo("Other"));
            Assert.That(b.Senders("Bus"), Is.EqualTo("Gen"));
            Assert.That(b.Model.IsSend(b["A"], b["Bus"]), Is.True, "the wire carries the send, from the sender's machine");
        }

        [Test]
        public void ASendThatRejoinsItsOwnTrackIsNotASend()
        {
            // Pad reaches Delay both directly and through Verb: every machine has one owner, so it is all Pad's track.
            var b = new Built("G Pad; F Verb; F Delay; M Master", "Pad>Master; Pad>Verb; Verb>Delay; Pad>Delay; Delay>Master");
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Pad" }));
            Assert.That(b.Machines("Pad"), Is.EqualTo("Pad,Verb,Delay"));
        }

        [Test]
        public void AReturnNeedsFeedersFromSeveralTracks()
        {
            // One track sending to a bus of its own is a branch of that track, not a return.
            var b = new Built("G Pad; F Verb; M Master", "Pad>Master; Pad>Verb; Verb>Master");
            Assert.That(b.Model.Entries.Select(e => e.Id), Is.EqualTo(new[] { "track:Pad" }));
            Assert.That(b.Machines("Pad"), Is.EqualTo("Pad,Verb"));
        }

        [Test]
        public void ChipOrderIsTracksThenGroupsThenReturns()
        {
            var b = new Built("G Kick; G Snare; F Bus; G Pad; G Lead; F Verb; M Master", "Kick>Bus; Snare>Bus; Bus>Master; Pad>Master; Lead>Master; Pad>Verb; Lead>Verb; Verb>Master");
            Assert.That(b.Model.Entries.Select(e => e.Kind), Is.EqualTo(new[] { TrackKind.Track, TrackKind.Track, TrackKind.Track, TrackKind.Track, TrackKind.Group, TrackKind.Return }));
        }

        [Test]
        public void ThePathOfATrackIsItsMachinesAndEverythingDownstream()
        {
            var b = new Built("G Kick; G Snare; F Bus; F Comp; G Bass; M Master", "Kick>Bus; Snare>Bus; Bus>Comp>Master; Bass>Master");
            var kick = b.Model.PathOf(b.Entry("Kick")).Select(n => n.Name);
            Assert.That(kick, Is.EquivalentTo(new[] { "Kick", "Bus", "Comp", "Master" }));
            var group = b.Model.PathOf(b.Entry("Bus")).Select(n => n.Name);
            Assert.That(group, Is.EquivalentTo(new[] { "Kick", "Snare", "Bus", "Comp", "Master" }));
        }

        [Test]
        public void TheHeadIsTheKeyAndTheIdFollowsItsName()
        {
            var b = new Built("G Bass; M Master", "Bass>Master");
            Assert.That(b.Entry("Bass").Id, Is.EqualTo("track:Bass"));
            Assert.That(b.Entry("Bass").Head.Tag, Is.EqualTo("Bass"));
        }

        // ---------------------------------------------------------------------------------------------- every machine in exactly one place

        /// <summary>A graph of <paramref name="n"/> machines wired at random (wires only go forward, so there is no loop), with controls and strays.</summary>
        static TrackGraph Random(int seed, int n, double density)
        {
            var rnd = new Random(seed);
            var g = new TrackGraph();
            var nodes = new List<TrackNode>();
            for (int i = 0; i < n - 1; i++)
            {
                int r = rnd.Next(10);
                nodes.Add(r < 4 ? g.Add(i, "G" + i, TrackNodeKind.Generator) : r < 9 ? g.Add(i, "F" + i, TrackNodeKind.Effect) : g.Add(i, "C" + i, TrackNodeKind.Effect, true));
            }
            var master = g.Add(n - 1, "Master", TrackNodeKind.Master);
            nodes.Add(master);
            for (int i = 0; i < n; i++)
            {
                if (nodes[i].IsControl || nodes[i] == master) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (nodes[j].Kind == TrackNodeKind.Generator) continue;
                    double p = nodes[j] == master ? density * 1.5 : density;
                    if (rnd.NextDouble() < p / Math.Max(1, (j - i) / 3.0)) g.Connect(nodes[i], nodes[j]);
                }
            }
            return g;
        }

        /// <summary>Whether <paramref name="from"/> reaches <paramref name="to"/> without going through <paramref name="avoid"/> (a plain search, to check the model against).</summary>
        static bool Reaches(TrackNode from, TrackNode to, TrackNode? avoid)
        {
            var seen = new HashSet<TrackNode>();
            var stack = new Stack<TrackNode>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                var x = stack.Pop();
                if (x == avoid || !seen.Add(x)) continue;
                if (x == to) return true;
                foreach (var d in x.Destinations) stack.Push(d);
            }
            return false;
        }

        [Test]
        public void EveryMachineLandsInExactlyOnePlace([Values(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12)] int seed)
        {
            var g = Random(seed, 40, 0.18 + seed % 4 * 0.05);
            var model = TrackModel.Compute(g);
            var master = model.Master!;
            int counted = 0;
            foreach (var node in g.Nodes)
            {
                int places = 0;
                if (node == master) places++;
                if (model.Entries.Count(e => e.Machines.Contains(node)) is int inEntries) places += inEntries;
                if (model.Unrouted.Any(u => u.Node == node)) places++;
                Assert.That(places, Is.EqualTo(1), node.Name + " lands in exactly one place");
                counted++;
                var place = model.PlaceOf(node);
                Assert.That(place == TrackPlace.Unrouted, Is.EqualTo(model.Unrouted.Any(u => u.Node == node)));
                // A machine is routed exactly when it is not a control machine and has a way to the Master (no loops here).
                bool reaches = node != master && !node.IsControl && Reaches(node, master, null);
                Assert.That(place != TrackPlace.Unrouted && place != TrackPlace.Master, Is.EqualTo(reaches), node.Name + " routed");
            }
            Assert.That(counted, Is.EqualTo(g.Nodes.Count));
            Assert.That(model.Entries.SelectMany(e => e.Machines).Count() + model.Unrouted.Count + 1, Is.EqualTo(g.Nodes.Count));
        }

        [Test]
        public void MembershipAgreesWithAPlainSearch([Values(21, 22, 23, 24, 25, 26, 27, 28, 29, 30)] int seed)
        {
            var g = Random(seed, 36, 0.2);
            var model = TrackModel.Compute(g);
            var master = model.Master!;
            foreach (var e in model.Entries)
            {
                // A shared machine's feeders are members when no way leads round it; senders otherwise.
                foreach (var f in e.Members) Assert.That(Reaches(f.Head, master, e.Head), Is.False, f.Name + " is a member of " + e.Name);
                foreach (var f in e.Senders) Assert.That(Reaches(f.Head, master, e.Head), Is.True, f.Name + " sends to " + e.Name);
                Assert.That(e.Members.Intersect(e.Senders), Is.Empty);
                Assert.That(e.Kind != TrackKind.Group || e.Members.Count > 0, "a group has members");
                Assert.That(e.Kind != TrackKind.Return || e.Members.Count == 0, "a return has none");
                if (e.Kind == TrackKind.Track) { Assert.That(e.Members, Is.Empty); Assert.That(e.Senders, Is.Empty); }
                foreach (var m in e.Members) Assert.That(m.Parent, Is.SameAs(e));
                // A member's entry has one parent; a track's machines all come after its head.
                Assert.That(e.Machines[0], Is.SameAs(e.Head));
            }
            // A wire into a shared machine is a send exactly when its source is not a member's.
            foreach (var n in g.Nodes)
                foreach (var d in n.Destinations)
                {
                    var target = model.EntryWithHead(d);
                    var from = model.EntryOf(n);
                    bool expected = target != null && from != null && from != target && target.Kind != TrackKind.Track && !target.Members.Contains(from) && target.Head.Sources.Contains(n);
                    Assert.That(model.IsSend(n, d), Is.EqualTo(expected), n.Name + ">" + d.Name);
                }
        }

        [Test]
        public void TwoHundredMachinesComputeInUnderTwoMilliseconds()
        {
            var g = new TrackGraph();
            var master = g.Add(0, "Master", TrackNodeKind.Master);
            var buses = new List<TrackNode>();
            for (int b = 0; b < 10; b++)
            {
                var bus = g.Add(b, "Bus" + b, TrackNodeKind.Effect);
                g.Connect(bus, master);
                buses.Add(bus);
            }
            var verb = g.Add(0, "Verb", TrackNodeKind.Effect);
            g.Connect(verb, master);
            for (int t = 0; t < 60; t++)
            {
                var gen = g.Add(t, "Gen" + t, TrackNodeKind.Generator);
                var fx = g.Add(t, "Fx" + t, TrackNodeKind.Effect);
                var eq = g.Add(t, "Eq" + t, TrackNodeKind.Effect);
                g.Connect(gen, fx);
                g.Connect(fx, eq);
                g.Connect(eq, buses[t % 10]);
                if (t % 3 == 0) { g.Connect(eq, verb); g.Connect(eq, master); }
            }
            for (int c = 0, controls = 200 - g.Nodes.Count; c < controls; c++) g.Add(c, "Lfo" + c, TrackNodeKind.Effect, true);
            Assert.That(g.Nodes.Count, Is.EqualTo(200));
            for (int i = 0; i < 20; i++) TrackModel.Compute(g);
            var times = new List<double>();
            var clock = new System.Diagnostics.Stopwatch();
            for (int i = 0; i < 60; i++)
            {
                clock.Restart();
                var m = TrackModel.Compute(g);
                times.Add(clock.Elapsed.TotalMilliseconds);
                Assert.That(m.Entries.Count, Is.EqualTo(60 + 10 + 1));
            }
            times.Sort();
            double median = times[times.Count / 2];
            TestContext.Out.WriteLine($"[mt1] 200 machines: median {median:0.000} ms, fastest {times[0]:0.000}, slowest {times[^1]:0.000}");
            Assert.That(median, Is.LessThan(2.0));
        }
    }
}
