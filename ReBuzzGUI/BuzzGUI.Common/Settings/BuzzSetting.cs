using System;

namespace BuzzGUI.Common.Settings
{
    public enum BuzzSettingType { Plain, FontFamily, FontStyle, FontWeight, FontStretch };

    [AttributeUsage(AttributeTargets.Property)]
    public class BuzzSetting : Attribute
    {
        public BuzzSetting(object defvalue) { DefaultValue = defvalue; }
        public object DefaultValue { get; private set; }

        public string Description { get; set; }
        /// <summary>The key a saved settings file holds when it is not the property's name (a renamed setting keeps reading and writing its old key).</summary>
        public string StoredName { get; set; }
        public BuzzSettingType Type { get; set; }
        public int Minimum { get; set; }
        public int Maximum { get; set; }

        public object[] Presets;
    }
}
