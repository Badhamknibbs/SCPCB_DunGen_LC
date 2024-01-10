using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;
using GameNetcodeStuff;
using Unity.Netcode;
using System.Runtime.CompilerServices;
using System.ComponentModel;
using System.Collections;

public class SCPDoorMover : NetworkBehaviour
{
    public AnimatedObjectTrigger animObjectTrigger;
    public NavMeshObstacle navObstacle;

    private float fEnemyDoorMeter = 0.0f;

    private void ToggleDoor(PlayerControllerB player) {
        animObjectTrigger.TriggerAnimation(player);
        navObstacle.enabled = !animObjectTrigger.boolValue;
    }

    private void OnTriggerStay(Collider other) {
        if (animObjectTrigger == null || NetworkManager.Singleton == null || !IsServer || animObjectTrigger.boolValue || !other.CompareTag("Enemy")) return; // Anim object boolvalue == isOpen

        EnemyAICollisionDetect collisionDetect = other.GetComponent<EnemyAICollisionDetect>();
        if (collisionDetect != null) {
            fEnemyDoorMeter += Time.deltaTime * collisionDetect.mainScript.openDoorSpeedMultiplier;
            if (fEnemyDoorMeter > 1.0f) {
                fEnemyDoorMeter = 0.0f;
                animObjectTrigger.TriggerAnimationNonPlayer(collisionDetect.mainScript.useSecondaryAudiosOnAnimatedObjects, overrideBool: true);
                navObstacle.enabled = false; // Enemy never closes a door, so always disable navObstacle
            }
        }
    }
}

public class SCP914InputStore : NetworkBehaviour
{
    // Store list of items entered
    // Only listen to the host when it detects something entering/exiting the trigger
    private void OnTriggerEnter(Collider other) {
        if (!NetworkManager.IsServer) return;
        Debug.Log($"SCPCB New thing entered input trigger: {other.gameObject.name}.");
        GrabbableObject grabbable = other.GetComponent<GrabbableObject>();
        if (grabbable == null) return;
        Debug.Log("SCPCB Thing was Grabbable.");
        lContainedItems.Add(other.gameObject);
    }

    private void OnTriggerExit(Collider other) {
        if (!NetworkManager.IsServer) return;
        Debug.Log($"SCPCB Thing has left input trigger: {other.gameObject.name}.");
        lContainedItems.Remove(other.gameObject);
    }

    public List<GameObject> lContainedItems;
}

public class SCP914Converter : NetworkBehaviour
{
    public SCP914InputStore InputStore;
    public Collider colliderOutput;

    public InteractTrigger SettingKnobTrigger;
    public GameObject SettingKnobPivot;
    public AudioSource SettingKnobSoundSrc;

    public InteractTrigger ActivateTrigger;
    public AudioSource ActivateAudioSrc;

    public AudioSource RefineAudioSrc;
    public Animator DoorIn;
    public Animator DoorOut;

    public enum SCP914Setting
    {
        ROUGH = 0,
        COARSE = 1,
        ONETOONE = 2,
        FINE = 3,
        VERYFINE = 4
    }

    private readonly ValueTuple<SCP914Setting, float>[] SCP914SettingRotations =
    [
        (SCP914Setting.ROUGH, 90),
        (SCP914Setting.COARSE, 45),
        (SCP914Setting.ONETOONE, 0),
        (SCP914Setting.FINE, -45),
        (SCP914Setting.VERYFINE, -90)
    ];

    private Dictionary<Item, Item[]>[] arItemMappings =
    [
        new Dictionary<Item, Item[]>(), // ROUGH
        new Dictionary<Item, Item[]>(), // COARSE
        new Dictionary<Item, Item[]>(), // ONETOONE
        new Dictionary<Item, Item[]>(), // FINE
        new Dictionary<Item, Item[]>()  // VERYFINE
    ];

    private int iCurrentState = 0;
    private bool bActive = false; // Server parameter to reject multiple activation at once
    private GameObject mapPropsContainer = null; // Assigned when necessary
    private RoundManager roundManager = null;    // ^

    public void AddConversion(SCP914Setting setting, Item itemInput, Item[] itemOutputs) {
        int iSetting = (int)setting;
        arItemMappings[iSetting].Add(itemInput, itemOutputs);
    }

    private Dictionary<Item, Item[]> GetItemMapping() {
        return arItemMappings[iCurrentState];
    }

    private Vector3 GetRandomNavMeshPositionInCollider(Collider collider) {
        Vector3 vPosition = collider.bounds.center;
        // Since the room can be rotated, and extents don't take into account rotation, we instead make a smallest cube fit for the items to spawn in
        float fExtentsMin = Math.Min(collider.bounds.extents.x, collider.bounds.extents.z);
        vPosition.x += UnityEngine.Random.Range(-fExtentsMin, fExtentsMin);
        vPosition.z += UnityEngine.Random.Range(-fExtentsMin, fExtentsMin);
        vPosition.y -= collider.bounds.extents.y / 2;
        NavMeshHit navHit;
        if (NavMesh.SamplePosition(vPosition, out navHit, 10, -1)) return navHit.position;
        else return vPosition; // Failsafe in case navmesh search fails
    }

    [ServerRpc(RequireOwnership = false)]
    public void TurnStateServerRpc() {
        // Update the state for all clients to the next one in the array
        int iNextState = (iCurrentState + 1) % 5;
        TurnStateClientRpc(iNextState);
    }

    [ClientRpc]
    public void TurnStateClientRpc(int iNewState) {
        iCurrentState = iNewState;
        Vector3 vCurrentRot = SettingKnobPivot.transform.rotation.eulerAngles;
        vCurrentRot.z = SCP914SettingRotations[iCurrentState].Item2;
        SettingKnobPivot.transform.rotation = Quaternion.Euler(vCurrentRot);
        SettingKnobSoundSrc.Play();
    }

    [ServerRpc(RequireOwnership = false)]
    public void ActivateServerRpc() {
        if (bActive) return;
        bActive = true;
        ActivateClientRpc();
        StartCoroutine(ConvertItem());
    }

    [ClientRpc]
    public void ActivateClientRpc() {
        ActivateTrigger.interactable = false;
        SettingKnobTrigger.interactable = false;
        ActivateAudioSrc.Play();
        DoorIn.SetBoolString("open", false);
        DoorOut.SetBoolString("open", false);
    }

    [ClientRpc]
    public void RefineFinishClientRpc() {
        ActivateTrigger.interactable = true;
        SettingKnobTrigger.interactable = true;
        DoorIn.SetBoolString("open", true);
        DoorOut.SetBoolString("open", true);
    }

    [ClientRpc]
    public void SpawnItemsClientRpc(NetworkObjectReference[] arNetworkObjectReferences, int[] arScrapValues, bool bChargeBattery) {
        for (int i = 0; i < arNetworkObjectReferences.Length; i++)
            if (arNetworkObjectReferences[i].TryGet(out NetworkObject networkObject)) {
                GrabbableObject component = networkObject.GetComponent<GrabbableObject>();
                if (component.itemProperties.isScrap) component.scrapValue = arScrapValues[i];
                else component.insertedBattery.charge = bChargeBattery ? 1.0f : 0.0f;
            }
    }

    IEnumerator ConvertItem() {
        RefineAudioSrc.Play();
        yield return new WaitForSeconds(7); // Initial wait before collecting item data so doors can close
        if (mapPropsContainer == null) {
            mapPropsContainer = GameObject.FindGameObjectWithTag("MapPropsContainer");
            if (mapPropsContainer == null) {
                Debug.LogError("SCPCB Failed to find map props container.");
                yield break;
            }
        }
        if (roundManager == null) {
            roundManager = FindObjectOfType<RoundManager>();
            if (roundManager == null) {
                Debug.LogError("SCPCB Failed to find round manager.");
                yield break;
            }
        }

        List<NetworkObjectReference> lNetworkObjectReferences = new List<NetworkObjectReference>();
        List<int> lScrapValues = new List<int>();
        bool bChargeBatteries = (iCurrentState > 1);

        Dictionary<Item, Item[]> dcCurrentMapping = GetItemMapping();
        Debug.Log($"SCPCB Contained item count: {InputStore.lContainedItems.Count}");
        foreach (GameObject item in InputStore.lContainedItems) {
            GrabbableObject grabbable = item.GetComponent<GrabbableObject>();
            if (grabbable == null) continue;
            Vector3 vPosition = GetRandomNavMeshPositionInCollider(colliderOutput);
            Item[] itemOutputs;

            GameObject gameObjectCreated = null;
            NetworkObject networkObject = null;
            Item itemCreated = null;
            if (dcCurrentMapping.TryGetValue(grabbable.itemProperties, out itemOutputs)) {
                Item itemOutput = (itemOutputs == null) ? null : itemOutputs[roundManager.AnomalyRandom.Next(itemOutputs.Length)]; // An output may just be null
                                                                                                                                   // No matter what we destroy the input object
                Destroy(grabbable.gameObject);
                if (itemOutput != null) {
                    Debug.Log("SCPCB Conversion found");
                    gameObjectCreated = Instantiate(itemOutput.spawnPrefab, vPosition, Quaternion.identity, mapPropsContainer.transform);
                    networkObject = gameObjectCreated.GetComponent<NetworkObject>();
                    itemCreated = gameObjectCreated.GetComponent<Item>();
                }
            } else {
                // No conversion mapping found, just create a new item copy
                gameObjectCreated = Instantiate(grabbable.itemProperties.spawnPrefab, vPosition, Quaternion.identity, mapPropsContainer.transform);
                Destroy(grabbable.gameObject);
                networkObject = gameObjectCreated.GetComponent<NetworkObject>();
                itemCreated = gameObjectCreated.GetComponent<Item>();
            }
            // Post processing for items created
            GrabbableObject grabbableCreated = gameObjectCreated.GetComponent<GrabbableObject>();
            if (itemCreated.isScrap) {
                // Generate scrap value
                grabbableCreated.scrapValue = (int)(roundManager.AnomalyRandom.Next(itemCreated.minValue, itemCreated.maxValue) * roundManager.scrapValueMultiplier);
                lScrapValues.Add(grabbableCreated.scrapValue);
            }
            networkObject.Spawn(destroyWithScene: true);
            lNetworkObjectReferences.Add(networkObject);
        }
        SpawnItemsClientRpc(lNetworkObjectReferences.ToArray(), lScrapValues.ToArray(), bChargeBatteries);
        InputStore.lContainedItems.Clear(); // Empty list for next runthrough
        yield return new WaitForSeconds(7); // 14 seconds (7 * 2) is the duration of the refining SFX (at the part where the bell dings is when we open the doors)
        RefineFinishClientRpc();
        bActive = false;
    }
}