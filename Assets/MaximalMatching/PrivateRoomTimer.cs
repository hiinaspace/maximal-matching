
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// Teleports local player to the point after the countdown 
// ends. AutoMatcher uses this to boot players out of the rooms
// after the matching.
public class PrivateRoomTimer : UdonSharpBehaviour
{
    public Transform teleportPoint;
    public UnityEngine.UI.Text visual;
    public OccupantTracker currentRoom;
    public float countdown;
    public bool countdownActive = false;

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
                countdownActive = false;
                // only teleport if the player is still in the room.
                if (currentRoom != null && currentRoom.localPlayerOccupying)
                {
                    Debug.Log($"[PrivateRoomTimer] countdown over, teleporting player out");
                    // perturb the teleport point a bit so everyone isn't on top of eachother.
                    Networking.LocalPlayer.TeleportTo(
                        teleportPoint.position + new Vector3(
                            UnityEngine.Random.Range(-3,3), 0, UnityEngine.Random.Range(-3,3)),
                        teleportPoint.rotation);
                } else
                {
                    Debug.Log($"[PrivateRoomTimer] didn't teleport because player wasn't in room {currentRoom}");
                }
            }
        }
        visual.text = countdownActive ? $"{Mathf.RoundToInt(countdown)} seconds remaining..." : "";
    }
}
