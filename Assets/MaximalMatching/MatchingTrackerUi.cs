
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class MatchingTrackerUi : UdonSharpBehaviour
{
    public GameObject ToggleRoot;
    public GameObject CanvasRoot;
    public Transform HeadTracker;
    public MatchingTracker MatchingTracker;
    public UnityEngine.UI.Text title;

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
    }

    public override void OnDrop()
    {
        lastDrop = Time.time;
        lastDropPlayerPosition = Networking.LocalPlayer.GetPosition();
    }

    private void LateUpdate()
    {
        VRCPlayerApi player = Networking.LocalPlayer;
        if (player == null) return;
        // head-attached pickup
        if (player.IsUserInVR())
        {
            // XXX need this because every player is "in desktop mode" for a few frames even if they're in VR.
            pickup.enabled = true;
            if (pickup.IsHeld)
            {
                CanvasRoot.SetActive(true);
            }
            else
            {
                var currentPos = Networking.LocalPlayer.GetPosition();
                // if it's been a while since we dropped it or we moved away from the drop point
                if ((Time.time - lastDrop) > 3 || Vector3.Distance(lastDropPlayerPosition, currentPos) > 1)
                {
                    // move the box collider slightly behind the head again for pickup
                    var attachPoint =
                        HeadTracker.TransformPoint(new Vector3(0, -0.3f, -1f) - collider.center);
                    transform.position = attachPoint;
                    // flip backward so grabbing it with your hand over shoulder turns it to the right position
                    transform.rotation = HeadTracker.rotation * Quaternion.AngleAxis(-90, Vector3.right);
                    // invisible canvas
                    CanvasRoot.SetActive(false);
                }
            }
        }
        else 
        {
            pickup.pickupable = false; // disable so they don't see phantom invisible pickup
            if (Input.GetKeyDown(KeyCode.E))
            {
                // toggle
                CanvasRoot.SetActive(!CanvasRoot.activeSelf);
                // put canvas in front of head (need Y offset to center)
                transform.position = HeadTracker.TransformPoint(new Vector3(0, -0.75f, 1.5f));
                transform.rotation = HeadTracker.rotation;
            }
        }
    }

    private void Update()
    {
        UpdateCanvas();
    }

    private void UpdateCanvas()
    {
        if (!MatchingTracker.started) return;
        if ((updateCooldown -= Time.deltaTime) > 0) return;
        updateCooldown = 1f;

        // show indication if player hasn't got ownership yet
        if (MatchingTracker.localPlayerState == null)
        {
            title.text = "Initializing, please wait warmly\n(If this persists, try rejoining.)";
            return;
        }

        var matchesRemaining = 0;
        for (int i = 0; i < 80; i++)
        {
            MatchingTrackerPlayerState state = MatchingTracker.playerStates[i];
            VRCPlayerApi p = state.GetExplicitOwner();
            if (p == null || Networking.LocalPlayer == p)
            {
                toggles[i].gameObject.SetActive(false);
                activePlayerLastUpdate[i] = null;
                continue;
            }

            toggles[i].gameObject.SetActive(true);
            var wasMatchedWith = MatchingTracker.GetLocallyMatchedWith(p);
            var matchedWithUs = state.matchedWithLocalPlayer;

            if (wasMatchedWith)
            {
                texts[i].text = MatchingTracker.GetDisplayName(p);
                var seconds = Time.time - MatchingTracker.GetLastMatchedWith(p);
                var minutes = seconds / 60f;
                var hours = minutes / 60f;
                texts[i].text = $"{MatchingTracker.GetDisplayName(p)} (matched " +
                    (hours > 1 ? $"{Mathf.FloorToInt(hours):D2}:{Mathf.FloorToInt(minutes):D2} ago)" :
                    minutes > 1 ? $"{Mathf.FloorToInt(minutes):##} minutes ago)" :
                    $"{Mathf.FloorToInt(seconds):##} seconds ago)");
            }
            else if (matchedWithUs)
            {
                texts[i].text = $"{MatchingTracker.GetDisplayName(p)} (matched with you on their end)";
            }
            else if (!state.matchingEnabled)
            {
                texts[i].text = $"{MatchingTracker.GetDisplayName(p)} (taking a break from matching)";
            }
            else
            {
                texts[i].text = MatchingTracker.GetDisplayName(p);
                matchesRemaining++;
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
        title.text = $"Player Checklist ({matchesRemaining} matches remaining)";
    }
}
