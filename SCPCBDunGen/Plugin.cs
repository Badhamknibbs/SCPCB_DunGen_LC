using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DunGen;
using HarmonyLib;
using LethalLevelLoader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace SCPCBDunGen
{
    [BepInPlugin(PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency(LethalLib.Plugin.ModGUID)]
    public class SCPCBDunGen : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);

        private static SCPCBDunGen Instance;

        internal ManualLogSource mls;

        public static AssetBundle SCPCBAssets;

        // Configs
        private ConfigEntry<int> configSCPRarity;
        private ConfigEntry<string> configMoonsOld;
        private ConfigEntry<string> configMoons;
        private ConfigEntry<bool> configGuaranteedSCP;
        private ConfigEntry<int> configLengthOverride;

        private void Awake() {
            if (Instance == null) {
                Instance = this;
            }

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

            GameObject SCPDoor = SCPCBAssets.LoadAsset<GameObject>("assets/Mods/SCP/prefabs/SCPDoorBtn.prefab");
            if (SCPDoor == null) {
                mls.LogError("Failed to load SCP Door.");
                return;
            }
            //GameObject SCP914Controls = SCPCBAssets.LoadAsset<GameObject>("assets/Mods/SCP/prefabs/914_Controls.prefab");
            //if (SCP914Controls == null) {
            //    mls.LogError("Failed to load SCP 914 controls.");
            //    return;
            //}

            // Register network prefabs
            LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(SCPDoor);
            //LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(SCP914Controls);

            DunGen.Graph.DungeonFlow SCPFlow = SCPCBAssets.LoadAsset<DunGen.Graph.DungeonFlow>("assets/Mods/SCP/data/SCPFlow.asset");
            if (SCPFlow == null) {
                mls.LogError("Failed to load SCP:CB Dungeon Flow.");
                return;
            }

            // Config setup
            configSCPRarity = Config.Bind("General", "FoundationRarity", 100, new ConfigDescription("How rare it is for the foundation to be chosen. Higher values increases the chance of spawning the foundation.", new AcceptableValueRange<int>(0, 300)));
            configMoonsOld = Config.Bind("General", "FoundationMoons", "NULL", new ConfigDescription("OLD CONFIG SETTING, HAS NO EFFECT. Only here to clear the legacy config from updating."));
            configMoons = Config.Bind("General", "FoundationMoonsList", "TitanLevel", new ConfigDescription("The moon(s) that the foundation can spawn on, in the form of a comma separated list of selectable level names (e.g. \"TitanLevel,RendLevel,DineLevel\")\nNOTE: These must be the internal data names of the levels (all vanilla moons are \"MoonnameLevel\", for modded moon support you will have to find their name if it doesn't follow the convention).\nThe following strings: \"all\", \"vanilla\", \"modded\", \"paid\", \"free\" are dynamic presets which add the dungeon to that specified group (string must only contain one of these, or a manual moon name list).\nDefault dungeon generation size is balanced around the dungeon scale multiplier of Titan (2.35), moons with significantly different dungeon size multipliers (see Lethal Company wiki for values) may result in dungeons that are extremely small/large."));
            configGuaranteedSCP = Config.Bind("General", "FoundationGuaranteed", false, new ConfigDescription("If enabled, the foundation will be effectively guaranteed to spawn. Only recommended for debugging/sightseeing purposes."));
            configLengthOverride = Config.Bind("General", "FoundationLengthOverride", -1, new ConfigDescription($"If not -1, overrides the foundation length to whatever you'd like. Adjusts how long/large the dungeon generates.\nBe *EXTREMELY* careful not to set this too high (anything too big on a moon with a high dungeon size multipier can cause catastrophic problems, like crashing your computer or worse)\nFor reference, the default value for the current version [{PluginInfo.PLUGIN_VERSION}] is {SCPFlow.Length.Min}, balanced around the multiplier for Titan (2.35). See the wiki for multipliers for customization."));

            if (configMoonsOld.Value != "NULL") {
                mls.LogWarning("Old config parameters detected; config has changed since 1.3.1, check the config and set FoundationMoons to NULL to suppress this warning (and change FoundationMoonsList if you want)");
            }

            if (configLengthOverride.Value != -1) {
                mls.LogInfo($"Foundation length override has been set to {configLengthOverride.Value}. Be careful with this value.");
                SCPFlow.Length.Min = configLengthOverride.Value;
                SCPFlow.Length.Max = configLengthOverride.Value;
            }

            ExtendedDungeonFlow extendedDungeon = ScriptableObject.CreateInstance<ExtendedDungeonFlow>();
            //extendedDungeon.contentSourceName = "SCP Foundation Dungeon";
            extendedDungeon.dungeonFlow = SCPFlow;
            extendedDungeon.dungeonFirstTimeAudio = SCPCBAssets.LoadAsset<AudioClip>("assets/Mods/SCP/snd/Horror8.ogg");
            extendedDungeon.dungeonDefaultRarity = 0;

            int iRarity = configGuaranteedSCP.Value ? 99999 : configSCPRarity.Value; // If configGuaranteedSCP is true, set rarity absurdly high
            string sMoonConfig = configMoons.Value.ToLower();
            if (sMoonConfig == "all") {
                extendedDungeon.manualContentSourceNameReferenceList.Add(new StringWithRarity("Lethal Company", iRarity));
                extendedDungeon.manualContentSourceNameReferenceList.Add(new StringWithRarity("Custom", iRarity));
                mls.LogInfo("Registered SCP dungeon for all moons.");
            } else if (sMoonConfig == "vanilla") {
                extendedDungeon.manualContentSourceNameReferenceList.Add(new StringWithRarity("Lethal Company", iRarity));
                mls.LogInfo("Registered SCP dungeon for all vanilla moons.");
            } else if (sMoonConfig == "modded") {
                extendedDungeon.manualContentSourceNameReferenceList.Add(new StringWithRarity("Custom", iRarity));
                mls.LogInfo("Registered SCP dungeon for all modded moons.");
            } else if (sMoonConfig == "paid") {
                extendedDungeon.dynamicRoutePricesList.Add(new Vector2WithRarity(new Vector2(1, 9999), iRarity));
                mls.LogInfo("Registered SCP dungeon for all paid moons.");
            } else if (sMoonConfig == "free") {
                extendedDungeon.dynamicRoutePricesList.Add(new Vector2WithRarity(new Vector2(0, 0), iRarity));
                mls.LogInfo("Registered SCP dungeon for all paid moons.");
            } else {
                string[] arMoonNames = configMoons.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                StringWithRarity[] arMoonNamesRarity = new StringWithRarity[arMoonNames.Length];
                for (int i = 0; i < arMoonNames.Length; i++) {
                    arMoonNamesRarity[i] = new StringWithRarity(arMoonNames[i], iRarity);
                }
                extendedDungeon.manualPlanetNameReferenceList = arMoonNamesRarity.ToList();
            }
            extendedDungeon.dungeonSizeMin = 1.0f;
            extendedDungeon.dungeonSizeMax = 3.0f;
            extendedDungeon.dungeonSizeLerpPercentage = 0.0f;
            AssetBundleLoader.RegisterExtendedDungeonFlow(extendedDungeon);

            harmony.PatchAll(typeof(SCPCBDunGen));
            harmony.PatchAll(typeof(RoundManagerPatch));

            mls.LogInfo($"SCP:CB DunGen for Lethal Company [Version {PluginInfo.PLUGIN_VERSION}] successfully loaded.");
        }

        // Patch teleport doors/vents (Dummies are used in the room tiles, so we can reuse assets already in the game)
        [HarmonyPatch(typeof(RoundManager))]
        internal class RoundManagerPatch
        {
            // Find and replace the dummy scrap items with the ones already in LE (for some reason it compares the hash of the item directly, not any actual data in it)
            [HarmonyPatch("SpawnScrapInLevel")]
            [HarmonyPrefix]
            private static bool SetItemSpawnPoints(ref SelectableLevel ___currentLevel, ref RuntimeDungeon ___dungeonGenerator) {
                if (___dungeonGenerator.Generator.DungeonFlow.name != "SCPFlow") return true; // Do nothing to non-SCP dungeons
                // Grab the general and tabletop item groups from the bottle bin (a common item across all 8 moons right now)
                SpawnableItemWithRarity bottleItem = ___currentLevel.spawnableScrap.Find(x => x.spawnableItem.itemName == "Bottles");
                if (bottleItem == null) {
                    Instance.mls.LogError("Failed to find bottle bin item for reference snatching; is this a custom moon without the bottle bin item?");
                    return true;
                }
                // Grab the small item group from the fancy glass (only appears on paid moons, so this one is optional and replaced with tabletop items if invalid)
                SpawnableItemWithRarity fancyGlassItem = ___currentLevel.spawnableScrap.Find(x => x.spawnableItem.itemName == "Golden cup");

                int iGeneralScrapCount = 0;
                int iTabletopScrapCount = 0;
                int iSmallScrapCount = 0;

                // Grab the item groups
                ItemGroup itemGroupGeneral = bottleItem.spawnableItem.spawnPositionTypes.Find(x => x.name == "GeneralItemClass");
                ItemGroup itemGroupTabletop = bottleItem.spawnableItem.spawnPositionTypes.Find(x => x.name == "TabletopItems");
                ItemGroup itemGroupSmall = (fancyGlassItem == null) ? itemGroupTabletop : fancyGlassItem.spawnableItem.spawnPositionTypes.Find(x => x.name == "SmallItems"); // Use tabletop items in place of small items if not on a paid moon
                RandomScrapSpawn[] scrapSpawns = FindObjectsOfType<RandomScrapSpawn>();
                foreach (RandomScrapSpawn scrapSpawn in scrapSpawns) {
                    switch (scrapSpawn.spawnableItems.name) {
                        case "GeneralItemClassDUMMY":
                            scrapSpawn.spawnableItems = itemGroupGeneral;
                            iGeneralScrapCount++;
                            break;
                        case "TabletopItemsDUMMY":
                            scrapSpawn.spawnableItems = itemGroupTabletop;
                            iTabletopScrapCount++;
                            break;
                        case "SmallItemsDUMMY":
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
            // Use "*" to destroy an item, or the same item name to do no conversion
            private static void AddConversions(SCP914Converter SCP914, List<Item> lItems, string sItem, string[] arROUGH, string[] arCOARSE, string[] arONETOONE, string[] arFINE, string[] arVERYFINE) {

                // Array to reference arrays via type index
                string[][] arSettingToItems = [arROUGH, arCOARSE, arONETOONE, arFINE, arVERYFINE];

                Item itemConvert = lItems.Find(x => x.itemName.ToLower() == sItem); // Item we want to add conversions for
                foreach (SCP914Converter.SCP914Setting scp914Setting in Enum.GetValues(typeof(SCP914Converter.SCP914Setting))) { // Iterate all setting values
                    List<Item> lConvertItems = new List<Item>(); // Create a list of all items we want to add conversions for
                    foreach (string sItemName in arSettingToItems[(int)scp914Setting]) {
                        if (sItemName == "*") lConvertItems.Add(null);
                        else if (sItemName == itemConvert.itemName) lConvertItems.Add(itemConvert);
                        else lConvertItems.Add(lItems.Find(x => x.itemName.ToLower() == sItemName)); // OK to be null
                    }
                    SCP914.AddConversion(scp914Setting, itemConvert, lConvertItems.ToArray()); // Convert to array then add to conversion dictionary
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

                StartOfRound StartOfRound = FindObjectOfType<StartOfRound>();
                if (StartOfRound == null) {
                    Instance.mls.LogError("Failed to find StartOfRound object.");
                    return;
                }

                List<Item> lItems = StartOfRound.allItemsList.itemsList;

                // Default conversions
                AddConversions(SCP914, lItems, "walkie-talkie", ["*"], ["walkie-talkie"], ["walkie-talkie", "old phone"], ["walkie-talkie"], ["boombox"]);
                AddConversions(SCP914, lItems, "shovel", ["*"], ["*"], ["shovel"], ["stop sign", "yield sign"], ["*"]);
                AddConversions(SCP914, lItems, "flashlight", ["*"], ["laser pointer", "flashlight"], ["flashlight"], ["pro-flashlight", "flashlight"], ["*", "fancy lamp", "stun grenade"]);
                AddConversions(SCP914, lItems, "pro-flashlight", ["laser pointer"], ["flashlight", "pro-flashlight"], ["pro-flashlight"], ["stun grenade", "fancy lamp", "*"], ["*", "fancy lamp"]);
                AddConversions(SCP914, lItems, "key", ["*"], ["*"], ["key"], ["lockpicker"], ["*"]);
                AddConversions(SCP914, lItems, "homemade flashbang", ["*"], ["*"], ["homemade flashbang"], ["stun grenade"], ["stun grenade"]);
                AddConversions(SCP914, lItems, "tragedy", ["*"], ["comedy", "tragedy"], ["comedy"], ["comedy", "tragedy"], ["*"]);
                AddConversions(SCP914, lItems, "comedy", ["*"], ["comedy", "tragedy"], ["tragedy"], ["comedy", "tragedy"], ["*"]);
                AddConversions(SCP914, lItems, "toy robot", ["metal sheet"], ["v-type engine"], ["toy robot"], ["*"], ["*"]);
                AddConversions(SCP914, lItems, "v-type engine", ["metal sheet"], ["big bolt", "large axle"], ["v-type engine"], ["lockpicker", "toy robot"], ["toy robot", "toy robot", "*"]);
                AddConversions(SCP914, lItems, "large axle", ["metal sheet"], ["big bolt"], ["large axle"], ["v-type engine", "lockpicker"], ["lockpicker", "lockpicker", "toy robot", "*"]);
                AddConversions(SCP914, lItems, "big bolt", ["*"], ["metal sheet"], ["big bolt"], ["large axle"], ["v-type engine", "lockpicker"]);
                AddConversions(SCP914, lItems, "metal sheet", ["*"], ["*"], ["metal sheet"], ["big bolt"], ["large axle", "lockpicker"]);
                return;
            }
        }
    }
}