// Fork (machine-safety.md §3.2-3.3, docs/specs/ux-overhaul.md P3f): Preferences ▸ Safety, a page of its own: the safety limiter on the output
// (on by default; off asks first), its ceiling, the clip indicator's threshold, and the guard on every machine (off only for this song,
// after asking; never saved off). The settings are the agent's "safety" section (safety.SafetyLimiter...): a change is logged in
// settings_history as Manual, and one the agent makes shows here at once (an agent's tool call is annotated destructive, so its client asks
// the person first).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using ReBuzz.Audio;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class SafetyPage : PrefsPage
    {
        readonly ReBuzzCore core;
        readonly CheckBox limiter = new CheckBox { Content = "Safety limiter on the output" };
        readonly ComboBox ceiling = Choices(160), threshold = Choices(160);
        readonly CheckBox guard = new CheckBox { Content = "Guard every machine" };
        List<double> ceilings = new List<double>(), thresholds = new List<double>();
        static readonly double[] Ceilings = { 0, -0.1, -0.3, -1, -3, -6 };
        static readonly double[] Thresholds = { -6, -3, -1, 0, 1, 2, 3, 6, 12 };

        public SafetyPage(ReBuzzCore core, PrefsModel model) : base(model)
        {
            this.core = core;
            Heading("Speakers and machines");
            Note("These guard your speakers, your headphones and your ears. Turning a guard off asks first.");
            Row("", limiter, "A zero-latency limiter on what goes to the speakers: nothing above its ceiling reaches them, and below it the sound " +
                             "is untouched. Renders and exports get the true signal.");
            Row("Limiter ceiling", ceiling);
            Row("Clip indicator", threshold, "The master meter turns red above this, measured before the limiter, and keeps the highest over until you click it.");
            Row("", guard, "A machine putting out NaN, infinity or more than +60 dBFS is silenced, stopped and muted (Reset and unmute on its menu). " +
                           "Off only for this song: a new or opened song turns it on again.");
            Avalonia.Automation.AutomationProperties.SetName(limiter, "Safety limiter");
            Avalonia.Automation.AutomationProperties.SetName(ceiling, "Limiter ceiling");
            Avalonia.Automation.AutomationProperties.SetName(threshold, "Clip indicator threshold");
            Avalonia.Automation.AutomationProperties.SetName(guard, "Machine guard");
            limiter.IsCheckedChanged += (s, e) => { if (!Filling) _ = SetLimiter(limiter.IsChecked == true); };
            guard.IsCheckedChanged += (s, e) => { if (!Filling) _ = SetGuard(guard.IsChecked == true); };
            OnPick(ceiling, i => { Model?.Apply("safety.LimiterCeilingDb", ceilings[i].ToString("R", CultureInfo.InvariantCulture)); Refresh(); });
            OnPick(threshold, i => { Model?.Apply("safety.ClipThresholdDb", thresholds[i].ToString("R", CultureInfo.InvariantCulture)); Refresh(); });
        }

        public override string Title => "Safety";
        public override bool ConfirmRestore => true;
        public override string RestoreWarning => "Put the safety settings back to their defaults? The limiter and the machine guard turn on, with the usual ceiling and threshold.";

        public CheckBox LimiterBox => limiter;
        public ComboBox CeilingBox => ceiling;
        public ComboBox ThresholdBox => threshold;
        public CheckBox GuardBox => guard;

        public override void RestoreOwn()
        {
            Model?.Restore("safety");
            RefreshSettings();
        }

        static string Dbfs(double db) => db == 0 ? "0 dBFS" : $"{db:+0.##;−0.##} dBFS";

        public override void Refresh()
        {
            Filling = true;
            try
            {
                var safe = core.SafeMaster;
                limiter.IsChecked = safe.LimiterOn;
                ceilings = Ceilings.Append(safe.CeilingDb).Distinct().OrderByDescending(v => v).ToList();
                ceiling.ItemsSource = ceilings.Select(Dbfs).ToList();
                ceiling.SelectedIndex = ceilings.IndexOf(safe.CeilingDb);
                ceiling.IsEnabled = safe.LimiterOn;
                thresholds = Thresholds.Append(safe.ClipThresholdDb).Distinct().OrderBy(v => v).ToList();
                threshold.ItemsSource = thresholds.Select(Dbfs).ToList();
                threshold.SelectedIndex = thresholds.IndexOf(safe.ClipThresholdDb);
                guard.IsChecked = MachineGuard.Enabled;
            }
            finally
            {
                Filling = false;
            }
        }

        /// <summary>The safety limiter on or off; off asks first.</summary>
        async Task SetLimiter(bool on)
        {
            if (!on && core.SafeMaster.LimiterOn)
            {
                Refresh();      // stays on until confirmed
                var answer = await Dialogs.Ask("Turn the safety limiter off? Then nothing stops a loud or broken signal from reaching your " +
                                               "speakers or headphones at full scale and beyond.", "Safety limiter", UserButtons.YesNo, DialogChoices.TurnOff);
                if (answer != UserAnswer.Yes) return;
            }
            Model?.Apply("safety.SafetyLimiter", on ? "True" : "False");
            Refresh();
        }

        /// <summary>The guard on every machine on or off (this song only); off asks first.</summary>
        async Task SetGuard(bool on)
        {
            if (!on && MachineGuard.Enabled)
            {
                Refresh();
                var answer = await Dialogs.Ask("Turn the machine guard off for this song? A machine that goes wild (NaN, infinity or more than " +
                                               "+60 dBFS) then plays on into the rest of the song; only the safety limiter stands between it and " +
                                               "your speakers. A new or opened song turns the guard on again.", "Machine guard", UserButtons.YesNo, DialogChoices.TurnOff);
                if (answer != UserAnswer.Yes) return;
            }
            Model?.Apply("safety.MachineGuard", on ? "True" : "False");
            Refresh();
        }
    }
}
