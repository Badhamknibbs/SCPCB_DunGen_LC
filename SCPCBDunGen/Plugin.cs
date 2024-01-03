using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using DunGen;
using HarmonyLib;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

namespace SCPCBDunGen
{
    [BepInPlugin(modGUID, modName, modVersion)]
    [BepInDependency(LethalLib.Plugin.ModGUID)]
    public class SCPCBDunGen : BaseUnityPlugin
    {
        private const string modGUID = "SCPCBDunGen";
        private const string modName = "SCPCBDunGen";
        private const string modVersion = "1.4.0";

        private readonly Harmony harmony = new Harmony(modGUID);

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

            mls = BepInEx.Logging.Logger.CreateLogSource(modGUID);

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

            // Register network prefabs
            LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(SCPDoor);

            harmony.PatchAll(typeof(SCPCBDunGen));
            harmony.PatchAll(typeof(RoundManagerPatch));

            DunGen.Graph.DungeonFlow SCPFlow = SCPCBAssets.LoadAsset<DunGen.Graph.DungeonFlow>("assets/Mods/SCP/data/SCPFlow.asset");
            if (SCPFlow == null) {
                mls.LogError("Failed to load SCP:CB Dungeon Flow.");
                return;
            }

            // Config setup
            configSCPRarity = Config.Bind("General", "FoundationRarity", 100, new ConfigDescription("How rare it is for the foundation to be chosen. Higher values increases the chance of spawning the foundation.", new AcceptableValueRange<int>(0, 300)));
            configMoonsOld = Config.Bind("General", "FoundationMoons", "NULL", new ConfigDescription("OLD CONFIG SETTING, HAS NO EFFECT. Only here to clear the legacy config from updating."));
            configMoons = Config.Bind("General", "FoundationMoonsList", "TitanLevel", new ConfigDescription("The moon(s) that the foundation can spawn on, in the form of a comma separated list of selectable level names (e.g. \"TitanLevel,RendLevel,DineLevel\")\nNOTE: These must be the internal data names of the levels (all vanilla moons are \"MoonnameLevel\", for modded moon support you will have to find their name if it doesn't follow the convention).\nUse \"all\" to generate in all moons (including modded and unsupported moons). May cause problems if fire exit count on a moon is > 1.\nDungeon generation size is balanced around the dungeon scale multiplier of Titan (2.35), moons with significantly different dungeon size multipliers (see Lethal Company wiki for values) may result in dungeons that are extremely small/large."));
            configGuaranteedSCP = Config.Bind("General", "FoundationGuaranteed", false, new ConfigDescription("If enabled, the foundation will be effectively guaranteed to spawn. Only recommended for debugging/sightseeing purposes."));
            configLengthOverride = Config.Bind("General", "FoundationLengthOverride", -1, new ConfigDescription($"If not -1, overrides the foundation length to whatever you'd like. Adjusts how long/large the dungeon generates.\nBe *EXTREMELY* careful not to set this too high (anything too big on a moon with a high dungeon size multipier can cause catastrophic problems, like crashing your computer or worse)\nFor reference, the default value for the current version [{modVersion}] is {SCPFlow.Length.Min}, balanced around the multiplier for Titan (2.35). See the wiki for multipliers for customization."));

            if (configMoonsOld.Value != "NULL") {
                mls.LogWarning("Old config parameters detected; config has changed since 1.3.1, check the config and set FoundationMoons to NULL to suppress this warning (and change FoundationMoonsList if you want)");
            }
            if (configLengthOverride.Value != -1) {
                mls.LogInfo($"Foundation length override has been set to {configLengthOverride.Value}. Be careful with this value.");
                SCPFlow.Length.Min = configLengthOverride.Value;
                SCPFlow.Length.Max = configLengthOverride.Value;
            }

            LethalLib.Extras.DungeonDef SCPDungeon = ScriptableObject.CreateInstance<LethalLib.Extras.DungeonDef>();
            SCPDungeon.dungeonFlow = SCPFlow;
            SCPDungeon.rarity = configGuaranteedSCP.Value ? 99999 : configSCPRarity.Value; // If configGuaranteedSCP is true, set rarity absurdly high (this is equivalent to a 99.996% chance of spawning the foundation)
            SCPDungeon.firstTimeDungeonAudio = SCPCBAssets.LoadAsset<AudioClip>("assets/Mods/SCP/snd/Horror8.ogg");

            if (configMoons.Value == "all") {
                LethalLib.Modules.Dungeon.AddDungeon(SCPDungeon, LethalLib.Modules.Levels.LevelTypes.All);
                mls.LogInfo("Registered SCP dungeon for all moons. Compatability not guaranteed!");
            } else {
                string[] arMoonNames = configMoons.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
                LethalLib.Modules.Dungeon.AddDungeon(SCPDungeon, LethalLib.Modules.Levels.LevelTypes.None, arMoonNames);
                mls.LogInfo($"Registered SCP dungeon for the following moons: {configMoons.Value}");
            }

            mls.LogInfo($"SCP:CB DunGen for Lethal Company [Version {modVersion}] successfully loaded.");
        }

        // Patch teleport doors/vents (Dummies are used in the room tiles, so we can reuse assets already in the game)
        [HarmonyPatch(typeof(RoundManager))]
        internal class RoundManagerPatch
        {
            [HarmonyPatch("GenerateNewFloor")]
            [HarmonyPostfix]
            static void FixTeleportDoors(ref RuntimeDungeon ___dungeonGenerator) {
                if (___dungeonGenerator.Generator.DungeonFlow.name != "SCPFlow") return; // Do nothing to non-SCP dungeons
                Instance.mls.LogInfo("Attempting to fix entrance teleporters.");
                SpawnSyncedObject[] SyncedObjects = FindObjectsOfType<SpawnSyncedObject>();
                NetworkManager networkManager = FindObjectOfType<NetworkManager>();
                NetworkPrefab networkVentPrefab = networkManager.NetworkConfig.Prefabs.m_Prefabs.First(x => x.Prefab.name == "VentEntrance");
                if (networkVentPrefab == null) {
                    Instance.mls.LogError("Failed to find VentEntrance prefab.");
                    return;
                }
                bool bFoundEntranceA = false;
                bool bFoundEntranceB = false;
                int iVentsFound = 0;
                foreach (SpawnSyncedObject syncedObject in SyncedObjects) {
                    if (syncedObject.spawnPrefab.name == "EntranceTeleportA_EMPTY") {
                        NetworkPrefab networkPrefab = networkManager.NetworkConfig.Prefabs.m_Prefabs.First(x => x.Prefab.name == "EntranceTeleportA");
                        if (networkPrefab == null) {
                            Instance.mls.LogError("Failed to find EntranceTeleportA prefab.");
                            return;
                        }
                        Instance.mls.LogInfo("Found and replaced EntranceTeleportA prefab.");
                        bFoundEntranceA = true;
                        syncedObject.spawnPrefab = networkPrefab.Prefab;
                    } else if (syncedObject.spawnPrefab.name == "EntranceTeleportB_EMPTY") {
                        NetworkPrefab networkPrefab = networkManager.NetworkConfig.Prefabs.m_Prefabs.First(x => x.Prefab.name == "EntranceTeleportB");
                        if (networkPrefab == null) {
                            Instance.mls.LogError("Failed to find EntranceTeleportB prefab.");
                            return;
                        }
                        Instance.mls.LogInfo("Found and replaced EntranceTeleportB prefab.");
                        bFoundEntranceB = true;
                        syncedObject.spawnPrefab = networkPrefab.Prefab;
                    } else if (syncedObject.spawnPrefab.name == "VentDummy") {
                        Instance.mls.LogInfo("Found and replaced VentEntrance prefab.");
                        iVentsFound++;
                        syncedObject.spawnPrefab = networkVentPrefab.Prefab;
                    }
                }
                if (!bFoundEntranceA) Instance.mls.LogError("Failed to find entrance teleporter for main entrance to replace.");
                if (!bFoundEntranceB) Instance.mls.LogError("Failed to find entrance teleporter for fire exit to replace.");
                if (iVentsFound == 0) Instance.mls.LogError("No vents found to replace.");
                else Instance.mls.LogInfo($"{iVentsFound} vents found and replaced with network prefab.");
            }

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
        }
    }
}