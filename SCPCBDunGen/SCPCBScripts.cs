using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;
using GameNetcodeStuff;
using Unity.Netcode;
using System.Runtime.CompilerServices;

public class SCPDoorMover : NetworkBehaviour
{
    public Animator DoorA;
    public Animator DoorB;
    public AudioSource DoorAudio;
    public AudioClip[] OpenSFX;
    public AudioClip BargeSFX;

    [ServerRpc]
    public void RpcToggleDoorState() {
        if (DoorA == null || DoorB == null) {
            Debug.LogError("Door parameters are null.");
            return;
        }
        bool bTargetState = !DoorA.GetBool("IsOpen");
        DoorA.SetBool("IsOpen", bTargetState);
        DoorB.SetBool("IsOpen", bTargetState);
        DoorAudio.PlayOneShot(OpenSFX[UnityEngine.Random.RandomRangeInt(0, OpenSFX.Length)]);
    }

    private void OnTriggerStay(Collider other) {
        if (DoorA.GetBool("IsOpen")) return;
        EnemyAI enemyAI = other.GetComponentInChildren<EnemyAI>();
        switch (enemyAI) { // Determine type of enemy, open the door if it's valid, or barge it open if it's a Jester in pursuit
            case JesterAI jesterAI:
                if (jesterAI.currentBehaviourStateIndex == 2) JesterBarge();
                else RpcToggleDoorState();
                return;
            case SpringManAI:
            case FlowermanAI:
            case NutcrackerEnemyAI:
            case MaskedPlayerEnemy:
                Debug.Log("Enemy opened SCP door.");
                RpcToggleDoorState();
                return;
            default: return; // Not an enemy or incompatible enemy
        }
    }

    public void JesterBarge() {
        if (DoorA == null || DoorB == null) {
            Debug.LogError("Door parameters are null.");
            return;
        }
        Debug.Log("Jester barged SCP door.");
        DoorA.SetTrigger("JesterOpen");
        DoorB.SetTrigger("JesterOpen");
        DoorA.SetBool("IsOpen", true);
        DoorB.SetBool("IsOpen", true);
        DoorAudio.PlayOneShot(BargeSFX);
    }
}