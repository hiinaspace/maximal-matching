
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// Visual timer for the countdown, moved into the local player's current private room
// if matched.
public class PrivateRoomTimer : UdonSharpBehaviour
{
    // root/parent of a bunch of places to teleport the player to after the countdown.
    public Transform teleportPointRoot;
    private Transform[] teleportPoints;

    public UnityEngine.UI.Text visual;

    private bool localPlayerInRoom;
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
    }

    public void StartCountdown(float countdownSecs)
    {
        countdown = countdownSecs;
        countdownActive = true;
    }

    void Update()
    {
        if (countdownActive)
        {
            if ((countdown -= Time.deltaTime) < 0)
            {
                Debug.Log($"[PrivateRoomTimer] countdown over");
                countdownActive = false;
                visual.text = "Wait warmly for the next round...";
            } else
            {
                visual.text = $"{Mathf.RoundToInt(countdown)} seconds remaining...";
            }
        }
    }

    public void TeleportOut()
    {
        // only teleport if the player is still in the room.
        if (localPlayerInRoom)
        {
            Debug.Log($"[PrivateRoomTimer] teleporting player out");
            // choose a random teleport point.
            var teleportPoint = teleportPoints[UnityEngine.Random.Range(0, teleportPoints.Length)];
            // avoid lerping the player to the teleport (apparently it does by default)
            Networking.LocalPlayer.TeleportTo(teleportPoint.position, teleportPoint.rotation,
                VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, lerpOnRemote: false);
        }
        else
        {
            Debug.Log($"[PrivateRoomTimer] didn't teleport because player wasn't in room.");
        }
    }
    public override void OnPlayerTriggerEnter(VRCPlayerApi player)
    {
        if (player == Networking.LocalPlayer) localPlayerInRoom = true;
    }
    public override void OnPlayerTriggerExit(VRCPlayerApi player)
    {
        if (player == Networking.LocalPlayer) localPlayerInRoom = false;
    }
}
