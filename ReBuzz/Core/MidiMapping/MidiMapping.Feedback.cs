// Fork (docs/specs/midi-mapping.md, MM2): controller profiles at work. Rebuild adds a profile's routes after the person's own maps (a control the
// person mapped keeps what they gave it), and makes the lights of the profile's controls: a ring that follows the value of what its knob drives,
// buttons that show a state (select, mute, solo, arm, play, record) and a grid of pads that show the clip.
//
// The lights are sent from the UI thread's timer (every 10 ms), never from the audio thread or the MIDI thread. Every light has the message it
// shows now and the one it was sent last: only a change is sent, the newest value of a light (a sweep of 1000 values is one message each tick), and
// a device gets at most 100 messages in any second (a window of the last 100 sends). A tick with nothing changed does nothing and allocates nothing.

using BuzzGUI.Interfaces;
using ReBuzz.Control;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ReBuzz.Core
{
    /// <summary>Where a controller's lights go: the MIDI outputs the system has. The engine's own is the default; tests use a fake.</summary>
    internal interface IFeedbackOutput
    {
        /// <summary>The outputs there are.</summary>
        IReadOnlyList<string> Names();
        /// <summary>Opens an output if it is not open.</summary>
        void Open(string output);
        /// <summary>A short message (status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16).</summary>
        void Send(string output, int message);
        /// <summary>A system exclusive message (F0 ... F7).</summary>
        void SendSysEx(string output, byte[] message);
    }

    /// <summary>The engine's outputs.</summary>
    sealed class EngineFeedbackOutput : IFeedbackOutput
    {
        readonly MidiEngine engine;
        readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);

        public EngineFeedbackOutput(MidiEngine engine) => this.engine = engine;

        public IReadOnlyList<string> Names() => engine?.OutputNames() ?? Array.Empty<string>();

        public void Open(string output)
        {
            if (engine == null || index.ContainsKey(output)) return;
            int i = engine.EnsureOutput(output);
            if (i >= 0) index[output] = i;
        }

        public void Send(string output, int message)
        {
            if (engine != null && index.TryGetValue(output, out int i)) engine.SendMidiOut(i, message);
        }

        public void SendSysEx(string output, byte[] message)
        {
            if (engine != null && index.TryGetValue(output, out int i)) engine.SendMidiOut(i, message);
        }
    }

    internal sealed partial class MidiMapping
    {
        // ------------------------------------------------------------------ the lights

        enum LedKind : byte { Ring, RingType, Select, Mute, Solo, Arm, ClipStop, Play, Record, KnobPan, KnobSend, Pad }

        /// <summary>The states of a pad, in the order the profile's names list them.</summary>
        enum PadKind { Empty, ArmedEmpty, Stopped, Queued, Playing, Stopping, Recording, RecordQueued }
        static readonly string[] PadNames = { "empty", "armedEmpty", "stopped", "queued", "playing", "stopping", "recording", "recordQueued" };

        /// <summary>One light: what it shows now (<see cref="Want"/>, worked out each tick) and what was sent last.</summary>
        sealed class Led
        {
            public LedKind Kind;
            public int Index, Row;              // the bank track (1 to 8), or a pad's column (0 to 7) and row
            public int Status, Number;          // the message's status byte (kind and channel) and its first data byte
            public int On = 127, Off;
            public int TypeOn, TypePan;         // a ring type's values
            public Route Ring;                  // the route whose value a ring shows
            public int Want = -1, WantBase = -1, Sent = -1, SentBase = -1;
        }

        sealed class DeviceFeedback
        {
            public string Device, Output, ProfileId;
            public ControllerProfile Profile;
            public Led[] Leds = Array.Empty<Led>();
            public PadLook[] Looks = new PadLook[PadNames.Length];
            public int Cursor;
            // The last sends (a window of 100): a device gets at most 100 messages in any 1000 ms.
            public readonly long[] Times = new long[Limit];
            public int Head, Count;
        }

        /// <summary>The most messages a device gets in a second.</summary>
        public const int MessagesPerSecond = 100;
        const int Limit = MessagesPerSecond;

        /// <summary>How often the lights are looked at (ms).</summary>
        public const int FeedbackPeriodMs = 10;

        IFeedbackOutput output;
        readonly Dictionary<string, DeviceFeedback> feedbackOf = new Dictionary<string, DeviceFeedback>(StringComparer.OrdinalIgnoreCase);
        System.Threading.Timer feedbackTimer;
        int feedbackPending;
        int knobMode;

        /// <summary>The outputs lights are sent to (the engine's; tests set a fake).</summary>
        internal IFeedbackOutput Output
        {
            get => output ??= new EngineFeedbackOutput(buzz.MidiInOutEngine);
            set { output = value; Rebuild(); }
        }

        /// <summary>The lights are sent by a timer (off in tests, which call <see cref="FeedbackTick"/>).</summary>
        internal bool FeedbackAutoTick { get; set; } = true;

        /// <summary>What the track knobs of a clip-grid controller drive: 0 the pan, 1 to 3 the send to the first, second or third return.</summary>
        public int KnobMode => knobMode;

        public bool SetKnobMode(int mode)
        {
            if (mode == knobMode) return false;
            knobMode = mode;
            Rebuild();
            Changed?.Invoke();
            return true;
        }

        bool NextKnobSend()
        {
            int sends = buzz.SongCore == null ? 0 : Math.Min(3, ReturnsOf(buzz.SongCore, Model).Count);
            return SetKnobMode(sends == 0 ? 0 : knobMode % sends + 1);
        }

        // ------------------------------------------------------------------ profile routes (Rebuild)

        /// <summary>
        /// Rebuild: adds the routes of every open controller that has a profile, and its lights. A person's own map of a control is added first, so
        /// it is the one the router uses.
        /// </summary>
        void ProfileRoutes(Snapshot s, Dictionary<long, List<Route>> lists, Func<string, int> deviceId, Action<MapSource, Route> add,
            Func<MapDrive, MapSource, KnobEncoding, Route> make, Func<int, MapSource, KnobEncoding, Route> knob, MixerContext ctx)
        {
            var lights = new List<DeviceFeedback>();
            var open = buzz.MidiInOutEngine?.OpenInputKeys() ?? Array.Empty<string>();
            var returnNames = ctx.Returns.Select(r => r.Name).ToList();
            foreach (string device in open)
            {
                var profile = ProfileOf(device);
                if (profile == null || RoleOf(device, profile) == DeviceRole.Keys) continue;
                if (buzz.SessionControllers?.ClaimsInput(device) == true) continue;         // the Session View's own script has this controller
                var setting = computer.SettingOf(device);
                var takeover = setting != null && setting.Takeover != Takeover.Pickup ? setting.Takeover : profile.Takeover;
                int knobs = setting != null && setting.KnobGroup > 0 ? setting.KnobGroup : profile.Knobs;
                bool taught = computer.GroupOf(device) != null;
                int mode = Math.Min(knobMode, returnNames.Count);

                MapDrive Drive(MapTarget t) => new MapDrive { Target = t, Takeover = takeover };
                foreach (var c in profile.Controls)
                {
                    var src = c.Source(device);
                    Route r = null;
                    switch (c.Action)
                    {
                        case "volume": r = make(Drive(MapTarget.Mixer(MixerControl.Volume, "", c.Index)), src, KnobEncoding.Absolute); break;
                        case "master": r = make(Drive(MapTarget.Mixer(MixerControl.Master)), src, KnobEncoding.Absolute); break;
                        case "knob":
                            if (c.Index > knobs || c.Index < 1 || taught) continue;               // a taught group has its own knobs
                            r = knob(c.Index - 1, src, KnobEncoding.Absolute);
                            break;
                        case "trackknob":
                            r = make(Drive(mode == 0 ? MapTarget.Mixer(MixerControl.Pan, "", c.Index) : MapTarget.Mixer(MixerControl.Send, "", c.Index, returnNames[mode - 1])), src, KnobEncoding.Absolute);
                            break;
                        case "select": r = make(Drive(MapTarget.Command(MapCommand.SelectBankTrack, c.Index)), src, KnobEncoding.Absolute); break;
                        case "arm": r = make(Drive(MapTarget.Command(MapCommand.ArmBankTrack, c.Index)), src, KnobEncoding.Absolute); break;
                        case "clipstop": r = make(Drive(MapTarget.Command(MapCommand.StopClipBankTrack, c.Index)), src, KnobEncoding.Absolute); break;
                        case "mute": r = make(Drive(MapTarget.Mixer(MixerControl.Mute, "", c.Index)), src, KnobEncoding.Absolute); break;
                        case "solo": r = make(Drive(MapTarget.Mixer(MixerControl.Solo, "", c.Index)), src, KnobEncoding.Absolute); break;
                        case "stopall": r = make(Drive(MapTarget.Command(MapCommand.StopAllClips)), src, KnobEncoding.Absolute); break;
                        case "scene": r = make(Drive(MapTarget.Command(MapCommand.LaunchScene, Math.Max(0, c.Index - 1))), src, KnobEncoding.Absolute); break;
                        case "play": r = make(Drive(MapTarget.Command(MapCommand.Play)), src, KnobEncoding.Absolute); break;
                        case "stop": r = make(Drive(MapTarget.Command(MapCommand.Stop)), src, KnobEncoding.Absolute); break;
                        case "record": r = make(Drive(MapTarget.Command(MapCommand.Record)), src, KnobEncoding.Absolute); break;
                        case "sceneup": r = make(Drive(MapTarget.Command(MapCommand.SceneUp)), src, KnobEncoding.Absolute); break;
                        case "scenedown": r = make(Drive(MapTarget.Command(MapCommand.SceneDown)), src, KnobEncoding.Absolute); break;
                        case "bankleft": r = make(Drive(MapTarget.Command(MapCommand.BankLeft)), src, KnobEncoding.Absolute); break;
                        case "bankright": r = make(Drive(MapTarget.Command(MapCommand.BankRight)), src, KnobEncoding.Absolute); break;
                        case "bankleft8": r = make(Drive(MapTarget.Command(MapCommand.BankLeft8)), src, KnobEncoding.Absolute); break;
                        case "bankright8": r = make(Drive(MapTarget.Command(MapCommand.BankRight8)), src, KnobEncoding.Absolute); break;
                        case "knobmode.pan": r = make(Drive(MapTarget.Command(MapCommand.KnobPan)), src, KnobEncoding.Absolute); break;
                        case "knobmode.send": r = make(Drive(MapTarget.Command(MapCommand.KnobSend)), src, KnobEncoding.Absolute); break;
                        default: continue;
                    }
                    add(src, r);
                }
                if (profile.Grid is { } grid)
                    for (int row = 0; row < grid.Rows; row++)
                        for (int col = 0; col < grid.Cols && col < MapTarget.BankSize; col++)
                        {
                            var src = new MapSource(device, grid.Kind, grid.Channel, grid.NumberOf(col, row));
                            add(src, make(Drive(MapTarget.Command(MapCommand.LaunchSlot, row * MapTarget.BankSize + col)), src, KnobEncoding.Absolute));
                        }

                // The lights.
                string port = OutputOf(device, profile);
                if (port.Length == 0) continue;
                Route RouteOf(MapKind mk, int channel, int number) =>
                    lists.TryGetValue(Key(deviceId(device), mk, channel, number), out var list) ? list.FirstOrDefault(x => !x.Free) : null;
                var leds = new List<Led>();
                foreach (var c in profile.Controls)
                {
                    if (c.Led == null) continue;
                    int status = (c.Led.Kind ?? c.Kind) == MapKind.Note ? 0x90 : 0xB0;
                    int channel = c.Led.Channel ?? c.Channel, number = c.Led.Number ?? c.Number;
                    switch (c.Action)
                    {
                        case "knob" when c.Index > knobs || c.Index < 1:
                        case "trackknob" when c.Index < 1:
                            continue;
                        case "knob":
                        case "trackknob":
                            leds.Add(new Led { Kind = LedKind.Ring, Index = c.Index, Status = status | channel, Number = number, Ring = RouteOf(c.Kind, c.Channel, c.Number) });
                            if (c.Led.TypeNumber is int typeNumber)
                                leds.Add(new Led { Kind = LedKind.RingType, Index = c.Action == "trackknob" ? 1 : 0, Status = 0xB0 | (c.Led.TypeChannel ?? c.Channel), Number = typeNumber, TypeOn = c.Led.TypeValue, TypePan = c.Led.TypePanValue });
                            break;
                        default:
                            LedKind? kind = c.Action switch
                            {
                                "select" => LedKind.Select, "mute" => LedKind.Mute, "solo" => LedKind.Solo, "arm" => LedKind.Arm, "clipstop" => LedKind.ClipStop,
                                "play" => LedKind.Play, "record" => LedKind.Record, "knobmode.pan" => LedKind.KnobPan, "knobmode.send" => LedKind.KnobSend, _ => null,
                            };
                            if (kind != null) leds.Add(new Led { Kind = kind.Value, Index = c.Index, Status = status | channel, Number = number, On = c.Led.On, Off = c.Led.Off });
                            break;
                    }
                }
                if (profile.Grid is { } g)
                    for (int row = 0; row < g.Rows; row++)
                        for (int col = 0; col < g.Cols && col < MapTarget.BankSize; col++)
                            leds.Add(new Led { Kind = LedKind.Pad, Index = col, Row = row, Status = g.Kind == MapKind.Note ? 0x90 : 0xB0, Number = g.NumberOf(col, row) });

                Output.Open(port);
                feedbackOf.TryGetValue(device, out var old);
                bool same = old != null && old.ProfileId == profile.Id && old.Output == port;
                var fb = same ? old : new DeviceFeedback { Device = device, Output = port, ProfileId = profile.Id, Profile = profile };
                if (same && old.Leds.Length == leds.Count)
                    for (int i = 0; i < leds.Count; i++) { leds[i].Sent = old.Leds[i].Sent; leds[i].SentBase = old.Leds[i].SentBase; }
                fb.Leds = leds.ToArray();
                if (profile.Grid != null)
                    for (int i = 0; i < PadNames.Length; i++) fb.Looks[i] = profile.Grid.Look(PadNames[i]);
                lights.Add(fb);
                if (!same)
                {
                    feedbackOf[device] = fb;
                    if (old != null) foreach (var bytes in old.Profile.Exit) Output.SendSysEx(old.Output, bytes);
                    foreach (var bytes in profile.Enter) Output.SendSysEx(port, bytes);          // the mode switch: the host drives the lights from now on
                }
            }
            // A controller that has no profile now (or no output, or went away) gets its own mode back.
            foreach (var gone in feedbackOf.Where(kv => !lights.Contains(kv.Value)).ToList())
            {
                feedbackOf.Remove(gone.Key);
                foreach (var bytes in gone.Value.Profile.Exit) Output.SendSysEx(gone.Value.Output, bytes);
            }
            s.Feedback = lights.ToArray();
            if (lights.Count > 0) EnsureFeedbackTimer();
        }

        /// <summary>Every light is sent again (the bank moved, or the device came back).</summary>
        public void InvalidateFeedback()
        {
            foreach (var fb in feedbackOf.Values)
                foreach (var led in fb.Leds) { led.Sent = -1; led.SentBase = -1; }
        }

        /// <summary>The controllers get their own mode back (the app closes, or they are turned off).</summary>
        public void ReleaseControllers()
        {
            foreach (var fb in feedbackOf.Values.ToList())
                foreach (var bytes in fb.Profile.Exit) Output.SendSysEx(fb.Output, bytes);
            feedbackOf.Clear();
            var s = snap;
            if (s != null) s.Feedback = Array.Empty<DeviceFeedback>();
        }

        void EnsureFeedbackTimer()
        {
            if (feedbackTimer != null || !FeedbackAutoTick) return;
            long start = Stopwatch.GetTimestamp();
            feedbackTimer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.Exchange(ref feedbackPending, 1) == 1) return;      // the UI thread has not caught up: skip
                buzz.dispatcher.BeginInvoke(() =>
                {
                    Volatile.Write(ref feedbackPending, 0);
                    FeedbackTick((Stopwatch.GetTimestamp() - start) * 1000 / Stopwatch.Frequency);
                });
            }, null, FeedbackPeriodMs, FeedbackPeriodMs);
        }

        // ------------------------------------------------------------------ the tick (UI thread)

        /// <summary>
        /// Works out what every light shows and sends the ones that changed, as many as each device's budget allows (at most 100 in any second;
        /// the lights not sent yet go first next time). Returns the messages sent. <paramref name="nowMs"/> is a clock in milliseconds.
        /// </summary>
        internal int FeedbackTick(long nowMs)
        {
            var s = snap;
            var all = s.Feedback;
            if (all == null || all.Length == 0) return 0;
            int sent = 0;
            for (int d = 0; d < all.Length; d++)
            {
                var fb = all[d];
                var leds = fb.Leds;
                for (int i = 0; i < leds.Length; i++) Compute(s, fb, leds[i]);
                while (fb.Count > 0 && nowMs - fb.Times[fb.Head] >= 1000)
                {
                    fb.Head = (fb.Head + 1) % Limit;
                    fb.Count--;
                }
                int n = leds.Length;
                for (int k = 0; k < n; k++)
                {
                    int at = (fb.Cursor + k) % n;
                    var led = leds[at];
                    if (led.Want == led.Sent && led.WantBase == led.SentBase) continue;
                    int cost = led.WantBase >= 0 ? 2 : 1;
                    if (fb.Count + cost > Limit) break;
                    var port = Output;
                    if (led.WantBase >= 0) { port.Send(fb.Output, led.WantBase); Stamp(fb, nowMs); }
                    port.Send(fb.Output, led.Want);
                    Stamp(fb, nowMs);
                    led.Sent = led.Want;
                    led.SentBase = led.WantBase;
                    sent += cost;
                    fb.Cursor = (at + 1) % n;
                }
            }
            return sent;
        }

        static void Stamp(DeviceFeedback fb, long nowMs)
        {
            fb.Times[(fb.Head + fb.Count) % Limit] = nowMs;
            fb.Count++;
        }

        static int Pack(int status, int number, int value) => status | (number << 8) | (Math.Clamp(value, 0, 127) << 16);

        void Compute(Snapshot s, DeviceFeedback fb, Led led)
        {
            int slot = led.Index - 1;
            switch (led.Kind)
            {
                case LedKind.Ring:
                    led.Want = Pack(led.Status, led.Number, led.Ring?.Position127() ?? 0);
                    break;
                case LedKind.RingType:
                    led.Want = Pack(led.Status, led.Number, led.Index == 1 && knobMode == 0 ? led.TypePan : led.TypeOn);
                    break;
                case LedKind.Select:
                    led.Want = Pack(led.Status, led.Number, slot >= 0 && slot < s.BankIds.Length && s.BankIds[slot] != null && s.BankIds[slot] == s.SelectedId ? led.On : led.Off);
                    break;
                case LedKind.Mute:
                    led.Want = Pack(led.Status, led.Number, slot >= 0 && slot < s.BankMachines.Length && s.BankMachines[slot]?.IsMuted == true ? led.On : led.Off);
                    break;
                case LedKind.Solo:
                    led.Want = Pack(led.Status, led.Number, slot >= 0 && slot < s.BankMachines.Length && s.BankMachines[slot]?.IsSoloed == true ? led.On : led.Off);
                    break;
                case LedKind.Arm:
                    led.Want = Pack(led.Status, led.Number, slot >= 0 && slot < s.BankGenerators.Length && s.BankGenerators[slot] is { } gen && buzz.SessionLayout?.IsMachineArmed(gen) == true ? led.On : led.Off);
                    break;
                case LedKind.ClipStop:
                    {
                        int track = bankFeed.TrackOf(led.Index);
                        var layout = buzz.SessionLayout;
                        bool playing = track >= 0 && layout?.SequenceOf(track) is { } seq && buzz.Session?.GetPlaying(seq) != null;
                        led.Want = Pack(led.Status, led.Number, playing ? led.On : led.Off);
                        break;
                    }
                case LedKind.Play: led.Want = Pack(led.Status, led.Number, buzz.Playing ? led.On : led.Off); break;
                case LedKind.Record: led.Want = Pack(led.Status, led.Number, buzz.Recording ? led.On : led.Off); break;
                case LedKind.KnobPan: led.Want = Pack(led.Status, led.Number, knobMode == 0 ? led.On : led.Off); break;
                case LedKind.KnobSend: led.Want = Pack(led.Status, led.Number, knobMode != 0 ? led.On : led.Off); break;
                case LedKind.Pad: ComputePad(fb, led); break;
            }
        }

        /// <summary>A pad: the state of the clip at the bank's column and the scene window's row, as the profile shows it.</summary>
        void ComputePad(DeviceFeedback fb, Led led)
        {
            var layout = buzz.SessionLayout;
            var session = buzz.Session;
            int track = bankFeed.TrackOf(led.Index + 1), scene = SceneOffset + led.Row;
            PadKind state = PadKind.Empty;
            int clipColour = 0;
            if (layout != null && session != null && track >= 0 && scene < layout.SceneCount && layout.SequenceOf(track) is { } seq)
            {
                var clip = layout.LaunchMap.Slot(track, scene)?.Clip;     // the launch map holds the slots as the Session View has them (ClipAt looks the clip up by name: it allocates)
                if (clip == null) state = layout.IsArmedTrack(seq) ? PadKind.ArmedEmpty : PadKind.Empty;
                else
                {
                    var clipState = layout.SlotState(seq, clip, track, scene);
                    if (layout.IsRecordingInto(seq, clip))
                        state = clipState == ClipState.Playing || clipState == ClipState.Stopping ? PadKind.Recording : PadKind.RecordQueued;
                    else
                        state = clipState switch { ClipState.Queued => PadKind.Queued, ClipState.Playing => PadKind.Playing, ClipState.Stopping => PadKind.Stopping, _ => PadKind.Stopped };
                    clipColour = PaletteOf(ClipColors.Get(clip));
                }
            }
            var look = fb.Looks[(int)state];
            if (look == null) { led.Want = Pack(led.Status, led.Number, 0); led.WantBase = -1; return; }
            int velocity = look.Velocity == -1 ? clipColour : look.Velocity;
            led.Want = Pack(led.Status | look.Channel, led.Number, velocity);
            led.WantBase = look.BaseVelocity == -2 ? -1 : Pack(led.Status | look.BaseChannel, led.Number, look.BaseVelocity == -1 ? clipColour : look.BaseVelocity);
        }

        /// <summary>The pad colour nearest a clip's colour (an entry of the clip-grid controller's palette).</summary>
        static int PaletteOf(int colourIndex)
        {
            var choices = SessionColourChoices.Choices;
            if (colourIndex < 0 || choices == null || choices.Length == 0) return ControllerPalette.Nearest(new Rgb(0x8E, 0xB9, 0xC2));
            uint argb = choices[colourIndex % choices.Length].Argb;
            return ControllerPalette.Nearest(new Rgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }
    }
}
