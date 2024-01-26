using GameNetcodeStuff;
using System;
using System.Collections.Generic;
using System.Text;
using Unity.Netcode;
using UnityEngine.AI;
using UnityEngine;
using UnityEngine.Yoga;
using DunGen;
using System.Linq;
using System.Collections;

namespace SCPCBDunGen
{
    public class SCPDoorMover : NetworkBehaviour
    {
        public NavMeshObstacle navObstacle;
        public Animator doors;
        public List<AudioClip> doorAudioClips;
        public AudioClip doorAudioClipFast;
        public AudioSource doorSFXSource;

        public InteractTrigger ButtonA;
        public InteractTrigger ButtonB;

        bool bDoorOpen = false;
        bool bDoorWaiting = false; // In the middle of opening or closing, server only parameter

        private List<EnemyAICollisionDetect> EnemiesInCollider = new List<EnemyAICollisionDetect>();

        private void OnTriggerEnter(Collider other) {
            if (NetworkManager.Singleton == null || !IsServer || !other.CompareTag("Enemy")) return;
            EnemyAICollisionDetect collisionDetect = other.GetComponent<EnemyAICollisionDetect>();
            if (collisionDetect == null) return;

            SCPCBDunGen.Instance.mls.LogInfo($"Enemy entered trigger: {collisionDetect.mainScript.enemyType.name}");
            EnemiesInCollider.Add(collisionDetect);
        }

        private void OnTriggerExit(Collider other) {
            if (NetworkManager.Singleton == null || !IsServer || !other.CompareTag("Enemy")) return;
            EnemyAICollisionDetect collisionDetect = other.GetComponent<EnemyAICollisionDetect>();
            if (collisionDetect == null) return;

            if (!EnemiesInCollider.Remove(collisionDetect)) {
                SCPCBDunGen.Instance.mls.LogWarning("Enemy left door trigger but somehow wasn't detected in trigger entry.");
            }
        }

        private void Update() {
            if (bDoorOpen) return; // Door is already open, enemies never close doors so exit early
            if (bDoorWaiting) return; // Already in the middle of something
            if (EnemiesInCollider.Count == 0) return; // No enemies, nothing to open the door

            SCPCBDunGen.Instance.mls.LogInfo("Enemy attempting to open door...");

            float fLowestMult = float.MaxValue;

            foreach (EnemyAICollisionDetect enemy in EnemiesInCollider) {
                EnemyAI enemyAI = enemy.mainScript;
                if (enemyAI.isEnemyDead) continue; // Skip dead enemies

                SCPCBDunGen.Instance.mls.LogInfo($"Enemy {enemyAI.enemyType.name} with open mult {enemyAI.openDoorSpeedMultiplier}");

                fLowestMult = Math.Min(fLowestMult, enemyAI.openDoorSpeedMultiplier);
            }

            SCPCBDunGen.Instance.mls.LogInfo($"Lowest multiplier is {fLowestMult}.");

            if (fLowestMult != float.MaxValue) {
                // Something is at the door that wants to open it
                SCPCBDunGen.Instance.mls.LogInfo("Door being opened.");
                if (fLowestMult < 1.0f) {
                    // This enemy wants the door open fast, use the faster animation
                    OpenDoorFastServerRpc();
                } else {
                    ToggleDoorServerRpc();
                }
            }
        }

        [ServerRpc]
        public void OpenDoorFastServerRpc() {
            SCPCBDunGen.Instance.mls.LogInfo("Opening door fast [SERVER].");
            bDoorWaiting = true;
            bDoorOpen = true;
            navObstacle.enabled = false;
            OpenDoorFastClientRpc();
            StartCoroutine(DoorToggleButtonUsable());
        }

        [ClientRpc]
        public void OpenDoorFastClientRpc() {
            SCPCBDunGen.Instance.mls.LogInfo("Opening door fast [CLIENT].");
            bDoorWaiting = true;
            bDoorOpen = true;
            ButtonA.interactable = false;
            ButtonB.interactable = false;
            doorSFXSource.PlayOneShot(doorAudioClipFast);
            doors.SetTrigger("openfast");
        }

        IEnumerator DoorToggleButtonUsable() {
            // If the door was opened normally, this lines up with the animation + 1 second of buffer that can be interrupted by enemies
            // Also doubles as an extended buffer if the door was opened quickly so the player can't cheese enemies by constantly closing the door
            yield return new WaitForSeconds(1.0f);
            bDoorWaiting = false;
            yield return new WaitForSeconds(1.0f);
            EnableDoorButtonClientRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        public void ToggleDoorServerRpc() {
            string sNewStateLog = bDoorOpen ? "closing" : "opening";
            SCPCBDunGen.Instance.mls.LogInfo($"Door is {sNewStateLog}.");
            if (bDoorWaiting) return;
            bDoorWaiting = true;
            bDoorOpen = !bDoorOpen; // Flip door state
            navObstacle.enabled = bDoorOpen;
            ToggleDoorClientRpc(bDoorOpen);
            StartCoroutine(DoorToggleButtonUsable());
        }

        [ClientRpc]
        public void ToggleDoorClientRpc(bool _bDoorOpen) {
            bDoorOpen = _bDoorOpen;
            ButtonA.interactable = false;
            ButtonB.interactable = false;
            doorSFXSource.PlayOneShot(doorAudioClips[UnityEngine.Random.Range(0, doorAudioClips.Count)]);
            doors.SetTrigger(bDoorOpen ? "open" : "close");
        }

        [ClientRpc]
        public void EnableDoorButtonClientRpc() {
            ButtonA.interactable = true;
            ButtonB.interactable = true;
        }
    }
}
