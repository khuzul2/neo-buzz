// Fork (docs/specs/ux-overhaul.md P3f): what Preferences shows and changes is the agent's settings list (settings_get's entries and
// keys, ReBuzz.Agent's SettingEntry), so there is one source of truth: a change made in the window goes through SettingsService (saved,
// checked as settings_set checks, logged in settings_history with the source Manual), and a change by the agent reaches the window
// through SettingsService.Changed.

using System;
using System.Collections.Generic;
using System.Linq;
using ReBuzz.Agent.App;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class PrefsModel
    {
        readonly IAppServices app;
        IReadOnlyList<SettingEntry> entries;

        public PrefsModel(IAppServices app) => this.app = app;

        public IAppServices App => app;

        /// <summary>Every setting, as settings_get lists them (read once: the entries read and write their live values).</summary>
        public IReadOnlyList<SettingEntry> Entries => entries ??= app.Settings();

        public IEnumerable<SettingEntry> Section(string section) => Entries.Where(e => e.Section == section);

        public SettingEntry Entry(string key) => Entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

        /// <summary>Sets a setting as the person chose it: checked as the agent's tool checks, saved, logged as Manual. Throws with what it takes.</summary>
        public string Apply(SettingEntry entry, string text) => SettingsService.Apply(app, entry, SettingsService.Normalize(entry, text));

        /// <summary>Sets a setting by key (tests, the pages' own controls).</summary>
        public string Apply(string key, string text) => Apply(Entry(key) ?? throw new ArgumentException("No setting " + key), text);

        /// <summary>Logs a change a page made through its own control (the audio device and its buffer, which settings_set sends to audio_set).</summary>
        public void Record(string key, string from, string to)
        {
            if (Entry(key) is { } e) SettingsService.Record(app, e, from, to);
        }

        public string Get(string key) => Entry(key) is { } e ? SettingsService.Get(e) : "";

        /// <summary>Puts a section back to its defaults (leaving what would end the agent's connection); the keys that changed.</summary>
        public IReadOnlyList<string> Restore(string section) => SettingsService.Reset(app, null, section);
    }
}
