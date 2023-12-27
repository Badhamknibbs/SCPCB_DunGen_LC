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
using static LethalLib.Modules.Levels;

namespace SCPCBDunGen
{
    [BepInPlugin(modGUID, modName, modVersion)]
    [BepInDependency(LethalLib.Plugin.ModGUID)]
    public class SCPCBDunGen : BaseUnityPlugin
    {
        private const string modGUID = "SCPCBDunGen";
        private const string modName = "SCPCBDunGen";
        private const string modVersion = "1.3.0";

        private readonly Harmony harmony = new Harmony(modGUID);

        private static SCPCBDunGen Instance;

        internal ManualLogSource mls;

        public static AssetBundle SCPCBAssets;

        // Configs
        private ConfigEntry<int> configSCPRarity;
        private ConfigEntry<string> configMoons;
        private ConfigEntry<bool> configGuaranteedSCP;

        private string[] MoonConfigs = {
            "all",
            "paid",
            "titan"
        };

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

            // Config setup
            configSCPRarity = Config.Bind("General", "FoundationRarity", 100, new ConfigDescription("How rare it is for the foundation to be chosen. Higher values increases the chance of spawning the foundation.", new AcceptableValueRange<int>(0, 300)));
            configMoons = Config.Bind("General", "FoundationMoons", "titan", new ConfigDescription("The moon(s) that the foundation can spawn on, from the given presets.", new AcceptableValueList<string>(MoonConfigs)));
            configGuaranteedSCP = Config.Bind("General", "FoundationGuaranteed", false, new ConfigDescription("If enabled, the foundation will be effectively guaranteed to spawn. Only recommended for debugging/sightseeing purposes."));

            DunGen.Graph.DungeonFlow SCPFlow = SCPCBAssets.LoadAsset<DunGen.Graph.DungeonFlow>("assets/Mods/SCP/data/SCPFlow.asset");
            if (SCPFlow == null) {
                mls.LogError("Failed to load SCP:CB Dungeon Flow.");
                return;
            }

            LevelTypes SCPLevelType = GetLevelTypeFromMoonConfig(configMoons.Value.ToLower()); // Convert to lower just in case the user put in caps characters by accident, for leniency
            if (SCPLevelType == LevelTypes.None) {
                mls.LogError("Config file invalid, moon config does not match one of the preset values.");
                return;
            }

            LethalLib.Extras.DungeonDef SCPDungeon = ScriptableObject.CreateInstance<LethalLib.Extras.DungeonDef>();
            SCPDungeon.dungeonFlow = SCPFlow;
            SCPDungeon.rarity = configGuaranteedSCP.Value ? 99999 : configSCPRarity.Value; // If configGuaranteedSCP is true, set rarity absurdly high (this is equivalent to a 99.996% chance of spawning the foundation)
            SCPDungeon.firstTimeDungeonAudio = SCPCBAssets.LoadAsset<AudioClip>("assets/Mods/SCP/snd/Horror8.ogg");

            LethalLib.Modules.Dungeon.AddDungeon(SCPDungeon, SCPLevelType);

            mls.LogInfo($"SCP:SB DunGen for Lethal Company [Version {modVersion}] successfully loaded.");
        }

        private LevelTypes GetLevelTypeFromMoonConfig(string sConfigName) {
            // See MoonConfigs
            switch (sConfigName) {
                case "all": return (LevelTypes.All & ~(LevelTypes.MarchLevel)); // March with 3 exits is not supported
                case "paid": return (LevelTypes.TitanLevel | LevelTypes.DineLevel | LevelTypes.RendLevel);
                case "titan": return LevelTypes.TitanLevel;
                default: return LevelTypes.None;
            }
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
                        NetworkPrefab networkPrefab = networkManager.NetworkConfig.Prefabs.m_Prefabs.First(x => x.Prefab.name == "VentEntrance");
                        if (networkPrefab == null) {
                            Instance.mls.LogError("Failed to find VentEntrance prefab.");
                            return;
                        }
                        Instance.mls.LogInfo("Found and replaced VentEntrance prefab.");
                        iVentsFound++;
                        syncedObject.spawnPrefab = networkPrefab.Prefab;
                    }
                }
                if (!bFoundEntranceA && !bFoundEntranceB) {
                    Instance.mls.LogError("Failed to find entrance teleporters to replace.");
                    return;
                }
                if (iVentsFound == 0) {
                    Instance.mls.LogError("No vents found to replace.");
                } else Instance.mls.LogInfo($"{iVentsFound} vents found and replaced with network prefab.");
            }

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

                // Grab the item groups
                ItemGroup itemGroupGeneral = bottleItem.spawnableItem.spawnPositionTypes.Find(x => x.name == "GeneralItemClass");
                ItemGroup itemGroupTabletop = bottleItem.spawnableItem.spawnPositionTypes.Find(x => x.name == "TabletopItems");
                ItemGroup itemGroupSmall = (fancyGlassItem == null) ? itemGroupTabletop : fancyGlassItem.spawnableItem.spawnPositionTypes.Find(x => x.name == "SmallItems"); // Use tabletop items in place of small items if not on a paid moon
                RandomScrapSpawn[] scrapSpawns = FindObjectsOfType<RandomScrapSpawn>();
                foreach (RandomScrapSpawn scrapSpawn in scrapSpawns) {
                    switch (scrapSpawn.spawnableItems.name) {
                        case "GeneralItemClassDUMMY": scrapSpawn.spawnableItems = itemGroupGeneral; break;
                        case "TabletopItemsDUMMY": scrapSpawn.spawnableItems = itemGroupTabletop; break;
                        case "SmallItemsDUMMY": scrapSpawn.spawnableItems = itemGroupSmall; break;
                    }
                }
                return true;
            }
        }
    }
}