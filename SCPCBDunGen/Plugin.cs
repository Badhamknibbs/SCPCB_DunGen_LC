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
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInDependency(LethalLib.Plugin.ModGUID)]
    public class SCPCBDunGen : BaseUnityPlugin
    {
        private readonly Harmony harmony = new Harmony(PluginInfo.PLUGIN_GUID);

        private static SCPCBDunGen Instance;

        internal ManualLogSource mls;

        public static AssetBundle SCPCBAssets;

        private void Awake() {
            if (Instance == null) {
                Instance = this;
            }

            mls = BepInEx.Logging.Logger.CreateLogSource(PluginInfo.PLUGIN_GUID);

            // Prepare netcode patcher
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
            GameObject SCPVent = SCPCBAssets.LoadAsset<GameObject>("assets/Mods/SCP/prefabs/SCPVent.prefab");
            if (SCPVent == null) {
                mls.LogError("Failed to load SCP vent.");
                return;
            }

            // Register network prefabs
            LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(SCPDoor);
            LethalLib.Modules.NetworkPrefabs.RegisterNetworkPrefab(SCPVent);

            harmony.PatchAll(typeof(SCPCBDunGen));
            harmony.PatchAll(typeof(RoundManagerPatch));

            DunGen.Graph.DungeonFlow SCPFlow = SCPCBAssets.LoadAsset<DunGen.Graph.DungeonFlow>("assets/Mods/SCP/data/SCPFlow.asset");
            if (SCPFlow == null) {
                mls.LogError("Failed to load SCPCB Dungeon Flow.");
                return;
            }

            LethalLib.Extras.DungeonDef SCPDungeon = ScriptableObject.CreateInstance<LethalLib.Extras.DungeonDef>();
            SCPDungeon.dungeonFlow = SCPFlow;
            SCPDungeon.rarity = 99999; // DEBUG guarantee SCP dungeon
            SCPDungeon.firstTimeDungeonAudio = SCPCBAssets.LoadAsset<AudioClip>("assets/Mods/SCP/snd/Horror8.ogg");

            LethalLib.Modules.Dungeon.AddDungeon(SCPDungeon, LethalLib.Modules.Levels.LevelTypes.All);

            mls.LogInfo($"SCP:SB DunGen for Lethal Company [Version {PluginInfo.PLUGIN_VERSION}] successfully loaded.");
        }

        // Patch teleport doors (Dummies are used in the room tiles, both for copyright and networked prefab issues)
        [HarmonyPatch(typeof(RoundManager))]
        internal class RoundManagerPatch {
            [HarmonyPatch("GenerateNewFloor")]
            [HarmonyPostfix]
            static void FixTeleportDoors() {
                Instance.mls.LogInfo("Attempting to fix entrance teleporters.");
                SpawnSyncedObject[] SyncedObjects = FindObjectsOfType<SpawnSyncedObject>();
                NetworkManager networkManager = FindObjectOfType<NetworkManager>();
                bool bFoundEntranceA = false;
                bool bFoundEntranceB = false;
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
                    }
                }
                if (!bFoundEntranceA && !bFoundEntranceB) {
                    Instance.mls.LogError("Failed to find entrance teleporters to replace.");
                    return;
                }
            }
        }
    }
}