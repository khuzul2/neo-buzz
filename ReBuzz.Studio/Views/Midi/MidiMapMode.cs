// Fork (docs/specs/midi-mapping.md, MM1): the studio's half of MIDI mapping. MapMode is the one state of "map mode" (MM-A: every mappable control
// outlined, a click arms one, the next control moved on any controller maps to it) that the layer, the banner, the popover (MM-B) and the
// tests share; MidiMap marks a control as mappable; the selection bridge keeps the studio's selected machine and the mapping's selected track
// one thing (the controllers' knobs follow what the studio shows, and a controller's "next track" moves the studio's selection).

using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using AvControl = Avalonia.Controls.Control;
using ReBuzz.Core;
using ReBuzz.Midi.Mapping;

namespace ReBuzz.Studio.Views.Midi
{
    /// <summary>Marks a control as mappable: what MIDI would drive when it is mapped (a parameter, or a transport command).</summary>
    internal static class MidiMap
    {
        public static readonly AttachedProperty<Func<MapTarget>> TargetProperty = AvaloniaProperty.RegisterAttached<AvControl, AvControl, Func<MapTarget>>("MidiTarget");

        public static void SetTarget(AvControl control, Func<MapTarget> target) => control.SetValue(TargetProperty, target);

        /// <summary>The control's target now, or null when it has none (or it is not mappable now).</summary>
        public static MapTarget GetTarget(AvControl control)
        {
            var f = control.GetValue(TargetProperty);
            if (f == null) return null;
            try { return f(); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Map mode: on or off, the scope a mapped control goes to, and which control is armed. One per song window.</summary>
    internal sealed class MapMode
    {
        static readonly ConditionalWeakTable<ReBuzzCore, MapMode> all = new();

        public static MapMode For(ReBuzzCore core) => all.GetValue(core, c => new MapMode(c));

        readonly ReBuzzCore core;
        bool active;
        MapScope scope = MapScope.Track;
        MapTarget armed;
        string lastMapped;

        MapMode(ReBuzzCore core)
        {
            this.core = core;
            if (core.MidiMapping is { } mm)
            {
                mm.Mapped += r =>
                {
                    lastMapped = r.Ok ? r.Text : r.Error;
                    if (active) armed = null;
                    Changed?.Invoke();
                };
                mm.Changed += () => Changed?.Invoke();
            }
        }

        public ReBuzzCore Core => core;

        public MidiMapping Mapping => core.MidiMapping;

        public bool Active => active;

        /// <summary>The scope a control mapped now goes to: the selected track's page, or the global map.</summary>
        public MapScope Scope
        {
            get => scope;
            set { if (scope != value) { scope = value; Changed?.Invoke(); } }
        }

        /// <summary>The control waiting for a controller (map mode's click, or the popover's Listen), or null.</summary>
        public MapTarget Armed => armed;

        /// <summary>What the last mapping said ("K3 → Lead · Cutoff on Lead"), or why it did not map.</summary>
        public string LastMapped => lastMapped;

        public bool IsListening => Mapping?.IsListening == true;

        public event Action Changed;

        public void Toggle() { if (active) Exit(); else Enter(); }

        public void Enter()
        {
            if (active || Mapping == null) return;
            active = true;
            armed = null;
            lastMapped = null;
            if (Mapping.SelectedTrackId == null) scope = MapScope.Global;
            Changed?.Invoke();
        }

        /// <summary>Esc: map mode ends, and whatever was listening stops.</summary>
        public void Exit()
        {
            if (!active) return;
            active = false;
            armed = null;
            Mapping?.CancelListening();
            Changed?.Invoke();
        }

        /// <summary>Arms a control: the next control moved on any controller maps to it, in <see cref="Scope"/>. False (with the reason in <paramref name="error"/>) when it cannot be.</summary>
        public bool Arm(MapTarget target, out string error)
        {
            error = null;
            var mm = Mapping;
            if (mm == null || target == null) { error = "MIDI mapping is not available."; return false; }
            var scopeNow = target.IsCommand ? MapScope.Global : scope;
            var drive = mm.InfoOf(target) is { } info ? new MapDrive { Min = Range(info.Range).min, Max = Range(info.Range).max, Invert = info.Invert, Takeover = info.Takeover } : null;
            var result = mm.BeginMap(target, scopeNow, drive);
            if (!result.Ok) { error = result.Error; lastMapped = result.Error; Changed?.Invoke(); return false; }
            armed = target;
            lastMapped = null;
            Changed?.Invoke();
            return true;
        }

        /// <summary>The armed control stops listening (map mode stays on).</summary>
        public void Disarm()
        {
            armed = null;
            Mapping?.CancelListening();
            Changed?.Invoke();
        }

        /// <summary>Delete: what the armed control (or the given one) is mapped to is let go.</summary>
        public bool Unmap(MapTarget target = null)
        {
            target ??= armed;
            if (target == null || Mapping == null) return false;
            Mapping.CancelListening();
            armed = null;
            bool changed = Mapping.UnmapTarget(target);
            lastMapped = changed ? "Unmapped " + target.Text : null;
            Changed?.Invoke();
            return changed;
        }

        /// <summary>"25%–75%" (a MapInfo's range text) as two fractions; "full" is 0 to 1.</summary>
        public static (double min, double max) Range(string text)
        {
            if (string.IsNullOrEmpty(text) || text == "full") return (0, 1);
            var parts = text.Replace("%", "").Split('–', '-');
            if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lo)
                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hi))
                return (Math.Clamp(lo / 100, 0, 1), Math.Clamp(hi / 100, 0, 1));
            return (0, 1);
        }
    }

    /// <summary>Keeps the studio's selection and the mapping's selected track one: a click on a machine moves the knobs to its track's page, and a controller's next-track button moves the studio's selection.</summary>
    internal static class SelectionBridge
    {
        static readonly ConditionalWeakTable<ReBuzzCore, object> attached = new();
        static readonly object Token = new object();

        public static void Attach(ReBuzzCore core)
        {
            var mapping = core?.MidiMapping;
            if (mapping == null || attached.TryGetValue(core, out _)) return;
            attached.Add(core, Token);
            var selection = StudioSelection.For(core);
            selection.Changed += source =>
            {
                if (ReferenceEquals(source, Token)) return;
                mapping.Select(selection.Machine, Token);
            };
            mapping.SelectionChanged += source =>
            {
                if (ReferenceEquals(source, Token)) return;
                if (mapping.SelectedMachine is { } machine) selection.SelectMachine(machine, Token);
            };
            if (selection.Machine != null) mapping.Select(selection.Machine, Token);
        }
    }
}
