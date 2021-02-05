
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

    // udon behaviors will start running before the local client receives the
    // correct ownership information from the client, Networking.GetOwner() will return
    // something nonsensical (I think the local player?).
    // This will cause a newly joined player to try to take ownership of the first
    // playerState gameObject, which once the network settles, means that everyone else
    // has to shuffle around for a new gameobject.
    // to work around this, treat the player state as uninitialized until the
    // instance master change the ownerId to a non-initial state; thus new
    // players should actually get the correct ownerId (and
    // Networking.GetOwner) before trying to take over a gameobject.
    public bool IsInitialized()
    {
        return ownerId >= 0;
    }

    public void Initialize()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        // actual player ids start at 1 so this is still "initialized" but not owned.
        ownerId = 0;
    }

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

    // hash of all the live playerIds at the time of writing matchingState.
    // used to detect whether the matchingState (by ordinal) is still valid for
    // the master's view of the world.
    [UdonSynced] public int playerIdSetHash;
}
