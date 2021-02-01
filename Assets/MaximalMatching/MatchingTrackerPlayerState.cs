
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

// player-owned behavior of synced data. Since there are 80 of these,
// MatchingTracker has to cycle the gameObjects on and off to avoid the Udon
// death run log spam; thus there's no Update() logic here.
public class MatchingTrackerPlayerState : UdonSharpBehaviour
{
    // disambiguates implicitly master-owned gameobjects from explicit ownership,
    // which correctly drops off when a player leaves.
    [UdonSynced] public int ownerId = -1;

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
    }

    // bitmap of "has been matched with" state by player ordinal.
    [UdonSynced] public string matchingState = "AAAAAAAAAAAAAA==";
}
