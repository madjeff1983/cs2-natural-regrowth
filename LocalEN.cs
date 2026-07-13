using System.Collections.Generic;
using Colossal;

namespace NaturalRegrowth
{
    public sealed class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Natural Regrowth" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "Main" },

                { m_Setting.GetOptionGroupLocaleID(Setting.kMainGroup), "Reproduction" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ReproductionRate)), "Reproduction Rate" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ReproductionRate)),
                    "How quickly adult trees spawn new saplings nearby. Higher values mean faster reproduction. At 0%, reproduction is effectively paused." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.LocalDensityCap)), "Local Density Cap" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.LocalDensityCap)),
                    "Maximum number of trees allowed in a small area before reproduction stops there. Lower values keep forests sparser; higher values allow denser growth." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SpawnRadius)), "Spawn Radius" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.SpawnRadius)),
                    "How far (in meters) from a parent tree a new sapling can appear. Larger values spread trees out more; smaller values keep offspring close to the parent. The density check samples an area twice this radius." },
            };
        }

        public void Unload() { }
    }
}