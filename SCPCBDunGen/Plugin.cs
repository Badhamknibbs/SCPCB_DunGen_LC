using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DunGen;
using HarmonyLib;
using LethalLevelLoader;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SCPCBDunGen
{
    // Represents conversions from a given item and what it can become depending on the setting
    // Used by the dictionary
    public struct SCP914Conversion
    {
        public string ItemName;

        public List<string> RoughResults { get; set; }
        public List<string> CoarseResults { get; set; }
        public List<string> OneToOneResults { get; set; }
        public List<string> FineResults { get; set; }
        public List<string> VeryFineResults { get; set; }
    }

    // Keyed collection of SCP 914 conversions
    public class SCP914ConversionSet:KeyedCollection<string, SCP914Conversion>
    {
        protected override string GetKeyForItem(SCP914Conversion conversion) => conversion.ItemName;
    }

    [BepInPlugin(PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency(LethalLib.Plugin.ModGUID)]
    public class SCPCBDunGen : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);

        public static SCPCBDunGen Instance;

        public ManualLogSource mls;

        public static AssetBundle SCPCBAssets;

        // Configs
        private ConfigEntry<int> configSCPRarity;
        private ConfigEntry<string> configMoonsOld;
        private ConfigEntry<string> configMoons;
        private ConfigEntry<bool> configGuaranteedSCP;
        private ConfigEntry<int> configLengthOverride;
        private ConfigEntry<bool> configDefault914;

        // SCP 914 conversion dictionary (using KeyedCollection for easier json conversion)
        SCP914ConversionSet SCP914Conversions = new SCP914ConversionSet();

        private void Awake() {
            if (Instance == null) {
                Instance = this;
            }

            // Netcode patcher magic
            var types = Assembly.GetExecutingAssembly().GetTypes();
            foreach (var type in types) {
                var methods = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (var method in methods) {
                    var attributes = method.GetCustomAttributes(typeof(RuntimeInitializeOnLoadMethodAttribute), false);
                    if (attributes.Length > 0) {
                        method.Invoke(null, null);
                    }
                }
            }

            mls = BepInEx.Logging.Logger.CreateLogSource(PluginInfo.PLUGIN_GUID);

            string sAssemblyLocation = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            SCPCBAssets = AssetBundle.LoadFromFile(Path.Combine(sAssemblyLocation, "scpcb_dungeon"));
            if (SCPCBAssets == null) {
                mls.LogError("Failed to load SCPCB Dungeon assets.");
                return;
            }

            DunGen.Graph.DungeonFlow SCPFlow = SCPCBAssets.LoadAsset<DunGen.Graph.DungeonFlow>("assets/Mods/SCP/data/SCPFlow.asset");
            if (SCPFlow == null) {
                mls.LogError("Failed to load SCP:CB Dungeon Flow.");
                return;
            }

            // Config setup
            configSCPRarity = Config.Bind("General", "FoundationRarity", 150, new ConfigDescription("How rare it is for the foundation to be chosen. Higher values increases the chance of spawning the foundation. Vanillas' main dungeons use a value of 300. Google Weighted Random if you don't know how it works, as that's how Lethal Company rarities function."));
            configMoonsOld = Config.Bind("General", "FoundationMoons", "NULL", new ConfigDescription("OLD CONFIG SETTING, HAS NO EFFECT. Only here to clear the legacy config from updating."));
            configMoons = Config.Bind("General", "FoundationMoonsList", "Titan,Secret Labs@500", new ConfigDescription("The moon(s) that the foundation can spawn on, in the form of a comma separated list of selectable level names and optionally a weight value by using an '@' and weight value after it (e.g. \"Titan@300,Dine,Rend@10,Secret Labs@9999\")\nThe name matching is lenient and should pick it up if you use the terminal name or internal mod name. If no rarity is specified, the FoundationRarity parameter is used.\nThe following strings: \"all\", \"vanilla\", \"modded\", \"paid\", \"free\" are dynamic presets which add the dungeon to that specified group (string must only contain one of these, or a manual moon name list).\n"));
            configGuaranteedSCP = Config.Bind("General", "FoundationGuaranteed", false, new ConfigDescription("OLD CONFIG SETTING, HAS NO EFFECT. Only here to clear the legacy config from updating.\nIf you want to effectively guarantee the foundation, use a weight of something like '99999'"));
            configLengthOverride = Config.Bind("General", "FoundationLengthOverride", -1, new ConfigDescription($"If not -1, overrides the foundation length to whatever you'd like. Adjusts how long/large the dungeon generates.\nBe *EXTREMELY* careful not to set this too high (anything too big on a moon with a high dungeon size multipier can cause catastrophic problems, like crashing your computer or worse)\nFor reference, the default value for the current version [{PluginInfo.PLUGIN_VERSION}] is {SCPFlow.Length.Min}. If it's too big, make this lower e.g. 6, if it's too small use something like 10 (or higher, but don't go too crazy with it)."));
            configDefault914 = Config.Bind("General", "Default914Recipes", true, new ConfigDescription("If false, any custom 914 Json files named \"default.json\" will be ignored (i.e. the default 914 config will not be loaded).\nSome custom 914 implementations may want to fully override the default settings, in which case this can be set to false."));

            if (configMoonsOld.Value != "NULL") {
                mls.LogWarning("Old config parameters detected for old moon setting; config has changed since 1.3.1, check the config and set FoundationMoons to NULL to suppress this warning (and change FoundationMoonsList if you want)");
            }
            if (configGuaranteedSCP.Value) {
                mls.LogWarning("Old config parameters detected for Guaranteed SCP; config has changed since 2.0.2, check the config and set GuaranteedSCP to false to suppress this warning (see uncapped FoundationRarity)");
            }

            if (configLengthOverride.Value != -1) {
                mls.LogInfo($"Foundation length override has been set to {configLengthOverride.Value}. Be careful with this value.");
                SCPFlow.Length.Min = configLengthOverride.Value;
                SCPFlow.Length.Max = configLengthOverride.Value;
            }

            ExtendedDungeonFlow extendedDungeon = ScriptableObject.CreateInstance<ExtendedDungeonFlow>();
            extendedDungeon.contentSourceName = "SCP Foundation Dungeon";
            extendedDungeon.dungeonDisplayName = "SCP Foundation";
            extendedDungeon.dungeonFlow = SCPFlow;
            extendedDungeon.dungeonFirstTimeAudio = SCPCBAssets.LoadAsset<AudioClip>("assets/Mods/SCP/snd/SCPHorror8.ogg");
            extendedDungeon.dungeonDefaultRarity = 0;

            int iDefaultRarity = configGuaranteedSCP.Value ? 99999 : configSCPRarity.Value; // If configGuaranteedSCP is true, set rarity absurdly high
            string sMoonConfig = configMoons.Value.ToLower();
            if (sMoonConfig == "all") {
                extendedDungeon.dynamicLevelTagsList.Add(new StringWithRarity("Vanilla", iDefaultRarity));
                extendedDungeon.dynamicLevelTagsList.Add(new StringWithRarity("Custom", iDefaultRarity));
                mls.LogInfo("Registered SCP dungeon for all moons.");
            } else if (sMoonConfig == "vanilla") {
                extendedDungeon.dynamicLevelTagsList.Add(new StringWithRarity("Vanilla", iDefaultRarity));
                mls.LogInfo("Registered SCP dungeon for all vanilla moons.");
            } else if (sMoonConfig == "modded") {
                extendedDungeon.dynamicLevelTagsList.Add(new StringWithRarity("Custom", iDefaultRarity));
                mls.LogInfo("Registered SCP dungeon for all modded moons.");
            } else if (sMoonConfig == "paid") {
                extendedDungeon.dynamicRoutePricesList.Add(new Vector2WithRarity(new Vector2(1, 9999), iDefaultRarity));
                mls.LogInfo("Registered SCP dungeon for all paid moons.");
            } else if (sMoonConfig == "free") {
                extendedDungeon.dynamicRoutePricesList.Add(new Vector2WithRarity(new Vector2(0, 0), iDefaultRarity));
                mls.LogInfo("Registered SCP dungeon for all free moons.");
            } else {
                mls.LogInfo("Registering SCP dungeon for predefined moon list.");
                // First split by comma, then split by @ symbol if present
                string[] arMoonNames = configMoons.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                List<StringWithRarity> lMoonsWithRarity = new List<StringWithRarity>();
                for (int i = 0; i < arMoonNames.Length; i++) {
                    string[] arMoonNameRarity = arMoonNames[i].Split('@', StringSplitOptions.RemoveEmptyEntries);
                    int iLength = arMoonNameRarity.Length;
                    if (iLength > 2) {
                        mls.LogError($"Invalid setup for moon rarity config: {arMoonNames[i]}. Skipping.");
                        continue;
                    }
                    if (iLength == 1) {
                        mls.LogInfo($"Registering SCP dungeon for moon {arMoonNames[i]} at default rarity {iDefaultRarity}");
                        lMoonsWithRarity.Add(new StringWithRarity(arMoonNames[i], iDefaultRarity));
                    } else { // Length == 2
                        int iRarity;
                        if (!int.TryParse(arMoonNameRarity[1], out iRarity)) {
                            mls.LogError($"Failed to parse rarity value for moon {arMoonNameRarity[0]}: {arMoonNameRarity[1]}. Skipping.");
                            continue;
                        }
                        mls.LogInfo($"Registering SCP dungeon for moon {arMoonNameRarity[0]} at rarity {iRarity}");
                        lMoonsWithRarity.Add(new StringWithRarity(arMoonNameRarity[0], iRarity));
                    }
                }
                extendedDungeon.manualPlanetNameReferenceList = lMoonsWithRarity;
            }
            extendedDungeon.dungeonSizeMin = 1.5f;
            extendedDungeon.dungeonSizeMax = 3.0f;
            extendedDungeon.dungeonSizeLerpPercentage = 0.0f;
            PatchedContent.RegisterExtendedDungeonFlow(extendedDungeon);

            // SCP 914 item conversion registry
            foreach (string sJsonFile in DiscoverConfiguredRecipeFiles()) {
                if (!configDefault914.Value && (Path.GetFileName(sJsonFile) == "default.json")) continue; // Skip any files named "default.json" if the config is set to false
                StreamReader streamReader = new StreamReader(sJsonFile);
                string sJsonValue = streamReader.ReadToEnd();
                try {
                    List<SCP914Conversion> Conversions = JsonConvert.DeserializeObject<List<SCP914Conversion>>(sJsonValue);
                    foreach (SCP914Conversion Conversion in Conversions) {
                        // If the given item already has conversions, add the possible conversions to it
                        // Using ToLowerInvariant which should help other langauges deal with item names (if they need to)
                        if (SCP914Conversions.Contains(Conversion.ItemName.ToLowerInvariant())) {
                            SCP914Conversions[Conversion.ItemName].RoughResults.AddRange(Conversion.RoughResults);
                            SCP914Conversions[Conversion.ItemName].CoarseResults.AddRange(Conversion.CoarseResults);
                            SCP914Conversions[Conversion.ItemName].OneToOneResults.AddRange(Conversion.OneToOneResults);
                            SCP914Conversions[Conversion.ItemName].FineResults.AddRange(Conversion.FineResults);
                            SCP914Conversions[Conversion.ItemName].VeryFineResults.AddRange(Conversion.VeryFineResults);
                        } else SCP914Conversions.Add(Conversion);
                    }
                    mls.LogInfo($"Registed SCP 914 json file successfully: {sJsonFile}");
                } catch (JsonException exception) {
                    mls.LogError($"Failed to deserialize file: {sJsonFile}. Exception: {exception.Message}");
                }
            }

            harmony.PatchAll(typeof(SCPCBDunGen));
            harmony.PatchAll(typeof(RoundManagerPatch));

            mls.LogInfo($"SCP:CB DunGen for Lethal Company [Version {PluginInfo.PLUGIN_VERSION}] successfully loaded.");
        }

        // Thanks to Lordfirespeed for this json file path getter function
        IEnumerable<string> DiscoverConfiguredRecipeFiles() {
            return Directory.GetDirectories(Paths.PluginPath) // get plugin directories
                .Select(pluginDirectory => Path.Join(pluginDirectory, "badhamknibbs-scp914-recipes")) // map to path of special dir
                .Where(Directory.Exists) // filter out files and paths that don't exist
                .SelectMany(Directory.GetFiles) // select the files inside those directories and flatten
                .Where(filePath => Path.GetExtension(filePath) == ".json"); // filter out files without the '.json' extension
        }

        [HarmonyPatch(typeof(RoundManager))]
        internal class RoundManagerPatch
        {
            // Find and replace the dummy scrap items with the ones already in LE (for some reason it compares the hash of the item directly, not any actual data in it)
            [HarmonyPatch("SpawnScrapInLevel")]
            [HarmonyPrefix]
            private static bool SetItemSpawnPoints(ref RuntimeDungeon ___dungeonGenerator) {
                if (___dungeonGenerator.Generator.DungeonFlow.name != "SCPFlow") return true; // Do nothing to non-SCP dungeons
                // Grab the general and tabletop item groups from the bottle bin item
                StartOfRound startOfRound = StartOfRound.Instance;
                if (startOfRound == null) {
                    Instance.mls.LogError("Failed to get start of round instance. Scrap spawns may not work correctly.");
                    return true;
                }

                Item bottleItem = startOfRound.allItemsList.itemsList.Find(x => x.name == "BottleBin");
                if (bottleItem == null) {
                    Instance.mls.LogError("Failed to find bottle bin item for reference snatching; scrap spawn may be significantly lower than expected.");
                    return true;
                }
                // Grab the small item group from the fancy glass (only appears on paid moons, so this one is optional and replaced with tabletop items if invalid)
                Item fancyGlassItem = startOfRound.allItemsList.itemsList.Find(x => x.name == "FancyCup");

                int iGeneralScrapCount = 0;
                int iTabletopScrapCount = 0;
                int iSmallScrapCount = 0;

                // Grab the item groups
                ItemGroup itemGroupGeneral = bottleItem.spawnPositionTypes.Find(x => x.name == "GeneralItemClass");
                ItemGroup itemGroupTabletop = bottleItem.spawnPositionTypes.Find(x => x.name == "TabletopItems");
                ItemGroup itemGroupSmall = (fancyGlassItem == null) ? itemGroupTabletop : fancyGlassItem.spawnPositionTypes.Find(x => x.name == "SmallItems"); // Use tabletop items in place of small items if not on a paid moon
                RandomScrapSpawn[] scrapSpawns = FindObjectsOfType<RandomScrapSpawn>();
                foreach (RandomScrapSpawn scrapSpawn in scrapSpawns) {
                    switch (scrapSpawn.spawnableItems.name) {
                        case "GeneralItemClass":
                            scrapSpawn.spawnableItems = itemGroupGeneral;
                            iGeneralScrapCount++;
                            break;
                        case "TabletopItems":
                            scrapSpawn.spawnableItems = itemGroupTabletop;
                            iTabletopScrapCount++;
                            break;
                        case "SmallItems":
                            scrapSpawn.spawnableItems = itemGroupSmall;
                            iSmallScrapCount++;
                            break;
                    }
                }

                Instance.mls.LogInfo($"Totals for scrap replacement: General: {iGeneralScrapCount}, Tabletop: {iTabletopScrapCount}, Small: {iSmallScrapCount}");
                if ((iGeneralScrapCount + iTabletopScrapCount + iSmallScrapCount) < 10) Instance.mls.LogWarning("Unusually low scrap spawn count; scrap may be sparse.");

                return true;
            }

            // Add conversions for a specified item to possible results
            // Use "*" to destroy an item, or "@" to do no conversion (same item as input)
            public static void AddConversions(SCP914Converter SCP914, List<Item> lItems, string sItem, IEnumerable<string> arROUGH, IEnumerable<string> arCOARSE, IEnumerable<string> arONETOONE, IEnumerable<string> arFINE, IEnumerable<string> arVERYFINE) {

                // Array to reference arrays via type index
                IEnumerable<string>[] arSettingToItems = [arROUGH, arCOARSE, arONETOONE, arFINE, arVERYFINE];

                Item itemConvert = lItems.Find(x => x.itemName.ToLowerInvariant() == sItem); // Item we want to add conversions for
                if (itemConvert == null) {
                    Instance.mls.LogError($"Failed to find item for conversion \"{sItem}\", skipping.");
                    return;
                }
                foreach (SCP914Converter.SCP914Setting scp914Setting in Enum.GetValues(typeof(SCP914Converter.SCP914Setting))) { // Iterate all setting values
                    List<Item> lConvertItems = new List<Item>(); // Create a list of all items we want to add conversions for
                    foreach (string sItemName in arSettingToItems[(int)scp914Setting]) {
                        if (sItemName == "*") lConvertItems.Add(null);
                        else if (sItemName == "@") lConvertItems.Add(itemConvert);
                        else lConvertItems.Add(lItems.Find(x => x.itemName.ToLowerInvariant() == sItemName)); // OK to be null
                    }
                    SCP914.AddConversion(scp914Setting, itemConvert, lConvertItems); // Add to conversion dictionary
                }
            }

            [HarmonyPatch("SpawnSyncedProps")]
            [HarmonyPostfix]
            private static void SCP914Configuration() {
                SCP914Converter SCP914 = FindObjectOfType<SCP914Converter>();
                if (SCP914 == null) { // No 914, don't do anything
                    Instance.mls.LogInfo("No 914 room found.");
                    return;
                }

                StartOfRound StartOfRound = StartOfRound.Instance;
                if (StartOfRound == null) {
                    Instance.mls.LogError("Failed to find StartOfRound object.");
                    return;
                }

                List<Item> lItems = StartOfRound.allItemsList?.itemsList;
                if (lItems == null) {
                    Instance.mls.LogError("Failed to get item list from StartOfRound.");
                    return;
                }
                if (lItems.Count == 0) {
                    Instance.mls.LogError("Item list was empty from StartOfRound.");
                    return;
                }

                // Add conversions
                foreach (var conversion in Instance.SCP914Conversions) {
                    AddConversions(SCP914, lItems, conversion.ItemName, conversion.RoughResults, conversion.CoarseResults, conversion.OneToOneResults, conversion.FineResults, conversion.VeryFineResults);
                }
            }
        }
    }
}