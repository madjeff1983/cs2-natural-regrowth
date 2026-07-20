using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;

namespace NaturalRegrowth
{
    [FileLocation("NaturalRegrowth")]
    [SettingsUIGroupOrder(kMainGroup)]
    [SettingsUIShowGroupName(kMainGroup)]
    public sealed class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kMainGroup = "Reproduction";

        public Setting(IMod mod) : base(mod) { }

        // How quickly adult vegetation reproduces. Higher = faster.
        // This maps to the spawn-interval range in the system (fast = short interval).
        [SettingsUISlider(min = 0, max = 100, step = 5, scalarMultiplier = 1, unit = Unit.kPercentage)]
        [SettingsUISection(kSection, kMainGroup)]
        public int ReproductionRate { get; set; } = 50;

        // Max number of trees allowed within the local search radius before
        // reproduction is suppressed in that spot. Prevents runaway forests.
        [SettingsUISlider(min = 1, max = 50, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kMainGroup)]
        public int LocalDensityCap { get; set; } = 12;

        // Maximum distance (metres) from a parent tree that a sapling may spawn.
        // The density check samples a wider area (2x this radius).
        [SettingsUISlider(min = 5, max = 40, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kMainGroup)]
        public int SpawnRadius { get; set; } = 15;

        // Growth-rate multiplier for young trees (saplings and teens) spawned by
        // this mod. 1 = normal game growth (the mod does not alter growth at all).
        // 2 = grows 2x the amount per natural growth step, up to 10 = 10x. The
        // timing between growth steps always matches the game's normal cadence;
        // only the amount gained per step changes.
        [SettingsUISlider(min = 1, max = 10, step = 1, scalarMultiplier = 1, unit = Unit.kInteger)]
        [SettingsUISection(kSection, kMainGroup)]
        public int GrowthRate { get; set; } = 1;

        public override void SetDefaults()
        {
            ReproductionRate = 50;
            LocalDensityCap = 12;
            SpawnRadius = 15;
            GrowthRate = 1;
        }
    }
}