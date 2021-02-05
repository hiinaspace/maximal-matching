
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Tracks a set of VRCPlayerApi objects that are currently in the collider on
/// this gameobject, as well as the last time any player entered or left.
/// </summary>
public class OccupantTracker : UdonSharpBehaviour
{
    // last change unity timestamps
    public float lastLeave, lastJoin;

    public bool localPlayerOccupying;

    // hashset by player id
    // alternatively could use a bitset by ordinal in the VRCPlayerApi.GetPlayers return, but i'm not sure how 
    // stable that is between calls.
    const int SIZE = 80 * 4;
    // player ids start at 1, so the default 0 for empty is fine
    private int[] playerIdKeys = new int[SIZE];
    // default false, which also works
    private bool[] playerInSet = new bool[SIZE];

    public int occupancy = 0;

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        bool lookup(int key, int[] keys, bool[] values)
    {
        return values[quadraticProbe(key, keys)];
    }

    // quadratic probe since we're just using the player id as a key;
    // linear probe would get clumped up.
    private 
#if !COMPILER_UDONSHARP
        static
#endif
        int quadraticProbe(int key, int[] keys)
    {
        int i = key % SIZE;
        // key 0 == empty
        // there should always be an empty slot at 1/4th occupancy
        for (int j = 0, k = keys[i]; k != key && k > 0; ++j, i = (key + j * j) % SIZE, k = keys[i]); 
        return i;
    }

    public
#if !COMPILER_UDONSHARP
        static
#endif
        bool set(int key, bool value, int[] keys, bool[] values)
    {
        var i = quadraticProbe(key, keys);
        var newKey = keys[i] == 0;
        keys[i] = key;
        values[i] = value;
        return newKey;
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        bool remove(int key, int[] keys, bool[] values)
    {
        var i = quadraticProbe(key, keys);
        var present = keys[i] > 0;
        keys[i] = 0;
        values[i] = false;
        return present;
    }

    public VRCPlayerApi[] GetOccupants() {

        VRCPlayerApi[] allPlayers = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
        VRCPlayerApi.GetPlayers(allPlayers);
        VRCPlayerApi[] occupants = new VRCPlayerApi[occupancy];
        int n = 0;
        foreach (var player in allPlayers)
        {
            // XXX apparently GetPlayers can sometimes have null in it.
            if (player == null) continue;
            if (lookup(player.playerId, playerIdKeys, playerInSet))
            {
                occupants[n++] = player;
            }
        }
        return occupants;
    }

    public override void OnPlayerTriggerEnter(VRCPlayerApi player)
    {
        if (player == null) return;
        lastJoin = Time.time;
        if (set(player.playerId, true, playerIdKeys, playerInSet))
        {
            occupancy++;
        }
        if (player == Networking.LocalPlayer) localPlayerOccupying = true;
    }
    public override void OnPlayerTriggerExit(VRCPlayerApi player)
    {
        if (player == null) return;
        lastLeave = Time.time;
        if (remove(player.playerId, playerIdKeys, playerInSet))
        {
            // just in case
            occupancy = Mathf.Max(0, occupancy - 1);
        }
        if (player == Networking.LocalPlayer) localPlayerOccupying = false;
    }

    // if a player leaves while in the zone, they don't trigger exit.
    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        if (player == null) return;
        if (remove(player.playerId, playerIdKeys, playerInSet))
        {
            // just in case
            occupancy = Mathf.Max(0, occupancy - 1);
        }
    }
}
