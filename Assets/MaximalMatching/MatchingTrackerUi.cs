
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// least bad player-attached menu. click both triggers in VR, or press E on desktop.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MatchingTrackerUi : UdonSharpBehaviour
{
    public GameObject ToggleRoot;
    public GameObject CanvasRoot;
    public MatchingTracker MatchingTracker;

    private UnityEngine.UI.Toggle[] toggles;
    private bool[] lastSeenToggle;
    private string[] activePlayerLastUpdate;
    private UnityEngine.UI.Text[] texts;
    private VRC_Pickup pickup;
    private BoxCollider collider;
    private float lastDrop;
    private Vector3 lastDropPlayerPosition;

    private float updateCooldown = 0f;

    void Start()
    {
        toggles = ToggleRoot.GetComponentsInChildren<UnityEngine.UI.Toggle>(includeInactive: true);
        lastSeenToggle = new bool[toggles.Length];
        activePlayerLastUpdate = new string[toggles.Length];
        texts = ToggleRoot.GetComponentsInChildren<UnityEngine.UI.Text>(includeInactive: true);
        pickup = (VRC_Pickup)GetComponent(typeof(VRC_Pickup));
        collider = GetComponent<BoxCollider>();

        UpdateCanvas();
    }

    public override void OnDrop()
    {
        lastDrop = Time.time;
        lastDropPlayerPosition = Networking.LocalPlayer.GetPosition();
    }

    private float lastLeftTrigger, lastRightTrigger;

    public override void InputUse(bool triggerDown, UdonInputEventArgs args)
    {
        if (!triggerDown) return;
        if (!Networking.LocalPlayer.IsUserInVR()) return;
        if (args.handType == HandType.LEFT)
        {
            lastLeftTrigger = Time.time;
        }
        else
        {
            lastRightTrigger = Time.time;
        }
        // if both triggers pressed about the same time
        if (Mathf.Abs(lastLeftTrigger - lastRightTrigger) < 0.1f)
        {
            ToggleCanvas();
        }
    }

    public void ToggleCanvas()
    {
        // put canvas in front of head (need Y offset to center)
        CanvasRoot.SetActive(!CanvasRoot.activeSelf);
        pickup.enabled = !pickup.enabled;

        var head = Networking.LocalPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        // try to center in front of head
        transform.position = head.position + head.rotation * new Vector3(0, -0.25f, 0.5f);
        transform.rotation = head.rotation;
    }

    // XXX can't eliminate update loop entirely for desktop player press E detection
    private void LateUpdate()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player == null) return;
        if (!player.IsUserInVR() && Input.GetKeyDown(KeyCode.E))
        {
            ToggleCanvas();
        }
    }

    public void UpdateCanvas()
    {
        SendCustomEventDelayedSeconds(nameof(UpdateCanvas), 1.03f);

        VRCPlayerApi[] players = MatchingTracker.GetOrderedPlayers();
        var playerCount = players.Length;
        int i;
        for (i = 0; i < playerCount; i++)
        {
            VRCPlayerApi p = players[i];
            // skip ourselves
            if (Networking.LocalPlayer == p)
            {
                toggles[i].gameObject.SetActive(false);
                activePlayerLastUpdate[i] = null;
                continue;
            }
            toggles[i].gameObject.SetActive(true);
            var wasMatchedWith = MatchingTracker.GetLocallyMatchedWith(p);
            if (wasMatchedWith)
            {
                texts[i].text = MatchingTracker.GetDisplayName(p);
                var seconds = Time.time - MatchingTracker.GetLastMatchedWith(p);
                var minutes = seconds / 60f;
                var hours = minutes / 60f;
                texts[i].text = $"{MatchingTracker.GetDisplayName(p)} " +
                    (hours > 1 ? $"({Mathf.FloorToInt(hours):D2}:{Mathf.FloorToInt(minutes) % 60:D2} ago)" :
                    minutes > 1 ? $"({Mathf.FloorToInt(minutes):D2} minutes ago)" :
                    $"({Mathf.FloorToInt(seconds):D2} seconds ago)");
            }
            else
            {
                texts[i].text = MatchingTracker.GetDisplayName(p);
            }
            if (activePlayerLastUpdate[i] == MatchingTracker.GetDisplayName(p))
            {
                // if player changed state in ui (doesn't match our internal state)
                if (toggles[i].isOn != lastSeenToggle[i])
                {
                    MatchingTracker.SetLocallyMatchedWith(p, toggles[i].isOn);
                }
                else
                {
                    // set UI from tracker state
                    toggles[i].isOn = wasMatchedWith;
                }
                lastSeenToggle[i] = toggles[i].isOn;

            }
            else
            {
                // wasn't the same player before
                activePlayerLastUpdate[i] = MatchingTracker.GetDisplayName(p);
                // set the UI state ignoring what it was
                toggles[i].isOn = wasMatchedWith;
                lastSeenToggle[i] = wasMatchedWith;
            }
        }
        for (; i < toggles.Length; i++)
        {
            toggles[i].gameObject.SetActive(false);
            activePlayerLastUpdate[i] = null;
        }
    }
}
