
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// Teleports local player to the point after the countdown 
// ends. AutoMatcher uses this to boot players out of the rooms
// after the matching.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PrivateRoomTimer : UdonSharpBehaviour
{
    // root/parent of a bunch of places to teleport the player to after the countdown.
    public Transform teleportPointRoot;
    private Transform[] teleportPoints;

    public UnityEngine.UI.Text visual;

    private bool localPlayerStillHere;
    public float countdown;
    public bool countdownActive = false;

    void Start()
    {
        teleportPoints = new Transform[teleportPointRoot.childCount];
        // XXX only way to get direct children.
        int i = 0;
        foreach (Transform point in teleportPointRoot)
        {
            teleportPoints[i++] = point;
        }
        SlowUpdate();
    }

    public void StartCountdown(float countdownSecs)
    {
        countdown = countdownSecs;
        countdownActive = true;
    }
    public void SlowUpdate()
    {
        SendCustomEventDelayedSeconds(nameof(SlowUpdate), 1f);
        if (countdownActive)
        {
            if ((countdown -= 1) < 0)
            {
                countdownActive = false;
                // only teleport if the player is still in the room.
                if (localPlayerStillHere)
                {
                    Debug.Log($"[PrivateRoomTimer] countdown over, teleporting player out");
                    TeleportOut();
                }
                else
                {
                    Debug.Log($"[PrivateRoomTimer] didn't teleport because player wasn't in room.");
                }
            }
        }
        visual.text = countdownActive ? 
            $"{Mathf.FloorToInt(countdown / 60f):00}:{Mathf.FloorToInt(countdown) % 60:00} remaining..." :
            "";
    }

    public void TeleportOut()
    {
        // choose a random teleport point.
        var teleportPoint = teleportPoints[UnityEngine.Random.Range(0, teleportPoints.Length)];
        // avoid lerping the player to the teleport (apparently it does by default)
        Networking.LocalPlayer.TeleportTo(teleportPoint.position, teleportPoint.rotation,
            VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, lerpOnRemote: false);
    }
    public override void OnPlayerTriggerEnter(VRCPlayerApi player)
    {
        if (player == Networking.LocalPlayer) localPlayerStillHere = true;
    }
    public override void OnPlayerTriggerExit(VRCPlayerApi player)
    {
        if (player == Networking.LocalPlayer) localPlayerStillHere = false;
    }
}
