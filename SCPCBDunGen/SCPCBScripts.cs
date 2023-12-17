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

public class SCPDoorMover : NetworkBehaviour
{
    public AnimatedObjectTrigger animObjectTrigger;

    private float fEnemyDoorMeter = 0.0f;

    private void OnTriggerStay(Collider other) {
        if (animObjectTrigger == null || NetworkManager.Singleton == null || !IsServer || animObjectTrigger.boolValue || !other.CompareTag("Enemy")) return; // Anim object boolvalue == isOpen

        EnemyAICollisionDetect collisionDetect = other.GetComponent<EnemyAICollisionDetect>();
        if (collisionDetect != null) {
            fEnemyDoorMeter += Time.deltaTime * collisionDetect.mainScript.openDoorSpeedMultiplier;
            if (fEnemyDoorMeter > 1.0f) {
                fEnemyDoorMeter = 0.0f;
                animObjectTrigger.TriggerAnimationNonPlayer(collisionDetect.mainScript.useSecondaryAudiosOnAnimatedObjects, overrideBool: true);
            }
        }
    }
}