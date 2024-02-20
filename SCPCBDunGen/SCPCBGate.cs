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
    public class SCPDoorGate : NetworkBehaviour
    {
        public NavMeshObstacle navObstacle;
        public Animator doors;
        public List<AudioClip> doorAudioClips;
        public AudioClip doorAudioClipFast;
        public AudioSource doorSFXSource;

        public InteractTrigger Button;

        bool bDoorOpen = false;
        bool bDoorWaiting = false; // In the middle of opening or closing, server only parameter

        IEnumerator DoorToggleButtonUsable() {
            // If the door was opened normally, this lines up with the animation + 1 second of buffer
            yield return new WaitForSeconds(1.0f);
            bDoorWaiting = false;
            yield return new WaitForSeconds(1.0f);
            EnableDoorButtonClientRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        public void ToggleDoorServerRpc() {
            if (bDoorWaiting) return;
            // If true the door is opening, otherwise it's closing
            bool bDoorOpening = !bDoorOpen;
            string sNewStateLog = bDoorOpening ? "opening" : "closing";
            SCPCBDunGen.Instance.mls.LogInfo($"Door is {sNewStateLog}.");
            bDoorWaiting = true;
            bDoorOpen = bDoorOpening;
            navObstacle.enabled = !bDoorOpening; // Nav obstacle state should be opposite of what the door state is (opening == disabled, closing == enabled)
            ToggleDoorClientRpc(bDoorOpen);
            StartCoroutine(DoorToggleButtonUsable());
        }

        [ClientRpc]
        public void ToggleDoorClientRpc(bool _bDoorOpen) {
            bDoorOpen = _bDoorOpen;
            Button.interactable = false;
            doorSFXSource.PlayOneShot(doorAudioClips[UnityEngine.Random.Range(0, doorAudioClips.Count)]);
            doors.SetTrigger(bDoorOpen ? "open" : "close");
        }

        [ClientRpc]
        public void EnableDoorButtonClientRpc() {
            Button.interactable = true;
        }
    }
}
