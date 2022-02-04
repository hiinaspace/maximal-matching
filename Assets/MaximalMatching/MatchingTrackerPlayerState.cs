
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

// player-owned behavior of synced data. Since there are 80 of these,
// MatchingTracker has to cycle the gameObjects on and off to avoid the Udon
// death run log spam; thus there's no Update() logic here.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class MatchingTrackerPlayerState : UdonSharpBehaviour
{
    // player ids start at 1, so this starts unowned
    [UdonSynced] public int ownerId = 0;

    public VRCPlayerApi GetExplicitOwner()
    {
        var networkingOwner = Networking.GetOwner(gameObject);
        return (networkingOwner.playerId == ownerId) ? networkingOwner : null;
    }

    public void TakeExplicitOwnership()
    {
        var player = Networking.LocalPlayer;
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        ownerId = player.playerId;
        RequestSerialization();
    }

    // player ids that have been matched with the owner.
    [UdonSynced] public int[] matchingState = new int[80];
    public override void OnPostSerialization(SerializationResult result)
    {
        if (result.success == false)
        {
            Debug.LogError($"serialization of player state failed: {result}");
        }
    }
}
