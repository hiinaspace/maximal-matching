﻿#define NOLOCAL_TEST_PLAYERIDS

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MatchingTracker : UdonSharpBehaviour
{
    public UnityEngine.UI.Text DebugLogText;
    public UnityEngine.UI.Text DebugStateText;
    public UnityEngine.UI.Text FullStateDisplay;
    public GameObject PlayerStateRoot;

    private MatchingTrackerPlayerState[] playerStates;

    // local hashmap of player to "has been matched with", by hash of the other player's displayName,
    // since the absolute player ids will break after 1024 entries, size to at most 50% load factor.

    const int LOCAL_STATE_SIZE = 2048;
    private string[] localMatchingKey = new string[LOCAL_STATE_SIZE];
    private bool[] localMatchingState = new bool[LOCAL_STATE_SIZE];
    private float[] lastChanged = new float[LOCAL_STATE_SIZE];
    private int localStatePopulation = 0;

    private MatchingTrackerPlayerState localPlayerState = null;

    // since udon has no easy per-player arbitrary synced state,
    // each player needs to own a MatchingTrackerPlayerState gameobject.
    // MaintainLocalOwnership uses exponential backoff to try to avoid contention;
    // experimentally, this worked well up to about a 50% "load factor" of players
    // to number of MatchingTrackerPlayerState gameobjects. For the max 80 players,
    // I think you'd probably want at least 100 (0.8 load factor) total
    private int ownershipAttempts = 0;
    private float nextOwnershipAttempt = -1;
    private float nextReleaseAttempt = -1;

    private float broadcastCooldown = -1;

    public bool started = false;

    void Start()
    {
        playerStates = PlayerStateRoot.GetComponentsInChildren<MatchingTrackerPlayerState>(includeInactive: true);
        Log($"Start MatchingTracker");
        started = true;
        SlowUpdate();
        if (Networking.LocalPlayer == null) return;
        // wait a bit for initial sync
        SendCustomEventDelayedSeconds(nameof(MaintainLocalOwnership), 5f);
        PeriodicallyBroadcast();
    }

    public string GetDisplayName(VRCPlayerApi player)
    {
        return player.displayName
#if LOCAL_TEST_PLAYERIDS
            + $"-{player.playerId}"
#endif
            ;

    }


    public bool GetLocallyMatchedWith(VRCPlayerApi other)
    {
        return lookup(GetDisplayName(other), localMatchingKey, localMatchingState);
    }
    public float GetLastMatchedWith(VRCPlayerApi other)
    {
        var i = linearProbe(GetDisplayName(other), localMatchingKey);
        var k = localMatchingKey[i];
        return k == null ? float.MinValue : lastChanged[i];
    }

    public void SetLocallyMatchedWith(VRCPlayerApi other, bool wasMatchedWith)
    {
        var name = GetDisplayName(other);
        if (set(name, wasMatchedWith, localMatchingKey, localMatchingState, lastChanged))
        {
            localStatePopulation++;
        }
        Log($"set matched with '{name}' to {wasMatchedWith}, population {localStatePopulation}");
        DoBroadcast();
    }

    public void ClearLocalMatching()
    {
        Log($"Clearing local matching state.");
        localMatchingKey = new string[LOCAL_STATE_SIZE];
        localMatchingState = new bool[LOCAL_STATE_SIZE];
        localStatePopulation = 0;
        DoBroadcast();
    }

    public
#if !COMPILER_UDONSHARP
        static
#endif
        bool lookup(string key, string[] keys, bool[] values)
    {
        var i = linearProbe(key, keys);
        var k = keys[i];
        return k == null ? false : values[i];
    }

    private 
#if !COMPILER_UDONSHARP
        static
#endif
        int linearProbe(string key, string[] keys)
    {
        // XXX negative modulus happens sometimes. might be biased but good enough for here.
        var init = Mathf.Abs(key.GetHashCode()) % LOCAL_STATE_SIZE;
        var i = init;
        var k = keys[i];
        while (k != null && k != key)
        {
            i = (i + 1) % LOCAL_STATE_SIZE;
            // I think this won't happen if the population is always less than the size
            if (i == init)
            {
                Log("uhoh wrapped around linear probe");
                return -1;
            }
            k = keys[i];
        }
        return i;
    }

    public
#if !COMPILER_UDONSHARP
        static
#endif
        bool set(string key, bool value, string[] keys, bool[] values, float[] lastUpdate)
    {
        var i = linearProbe(key, keys);
        var newKey = keys[i] == null;
        keys[i] = key;
        values[i] = value;
        lastUpdate[i] = Time.time;
        return newKey;
    }

    // deserializes all the player matching states into a (flattened) bool array indexable by ordinal.
    // if AbortOnDesync and any players aren't synced to the current set of players, returns null;
    // all but the UI display uses that to prevent calculating matchings from desynced states.
    public bool[] ReadGlobalMatchingState()
    {
        VRCPlayerApi[] players = GetOrderedPlayers();
        var playerCount = players.Length;
        var playerOrdinalsById = new int[1024];
        for (int i = 0; i < playerCount; i++)
        {
            // add 1, so that 0 becomes a sentinel value for 'not here'
            playerOrdinalsById[players[i].playerId] = i + 1;
        }

        var len = playerStates.Length;
        int[] explicitOwnerIds = new int[len];
        {
            var i = 0;
            foreach (var stateObject in playerStates)
            {
                var owner = stateObject.GetExplicitOwner();
                explicitOwnerIds[i++] = owner == null ? -1 : owner.playerId;
            }
        }

        bool[] globalState = new bool[80 * 80];
        var n = 0;
        foreach (var player in players)
        {
            var pid = player.playerId;
            // do an N^2 search
            var sidx = -1;
            for (int i = 0; i < len; i++)
            {
                if (explicitOwnerIds[i] == pid)
                {
                    sidx = i;
                    break;
                }
            }
            if (sidx == -1)
            {
                Log($"player {GetDisplayName(player)} id={pid} doesn't own a sync object yet");
                // set 'has matched with' to everyone, so they don't get spurious matches while
                // waiting to take ownership.
                for (int j = 0; j < playerCount; j++)
                {
                    globalState[n * 80 + j] = true;
                }
            } else
            {
                int[] matchedPlayers = playerStates[sidx].matchingState;

                for (int j = 0; j < matchedPlayers.Length; j++)
                {
                    var matchedPlayerId = matchedPlayers[j];
                    // end of the list marker
                    if (matchedPlayerId == 0) break;

                    var matchedPlayerOrdinal = playerOrdinalsById[matchedPlayerId] - 1;
                    // if the player is still in the instance
                    if (matchedPlayerOrdinal >= 0)
                    {
                        globalState[n * 80 + matchedPlayerOrdinal] = true;
                    }
                }
            }

            n++;
        }
        return globalState;
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        int[] deserializeBytes(byte[] bytes)
    {
        // 10 bits per player
        int[] matchedPlayers = new int[80];
        // deserialize 5 bytes int 4 player ids
        for (int j = 0, k = 0; k < 80; j += 5, k += 4)
        {
            int a = bytes[j];
            int b = bytes[j+1];
            int c = bytes[j+2];
            int d = bytes[j+3];
            int e = bytes[j+4];
            matchedPlayers[k] = (a << 2) + ((b >> 6) & 3);
            matchedPlayers[k + 1] = ((b & 63) << 4) + ((c >> 4) & 15);
            matchedPlayers[k + 2] = ((c & 15) << 6) + ((d >> 2) & 63);
            matchedPlayers[k + 3] = ((d & 3) << 8) + e;
            if (matchedPlayers[k + 3] == 0) break;
        }
        //Log($"deserialized matched players: {join(matchedPlayers)}");
        return matchedPlayers;
    }

    // crash watchdog
    public float lastUpdate;

    public void SlowUpdate()
    {
        SendCustomEventDelayedSeconds(nameof(SlowUpdate), 1);
        lastUpdate = Time.time;
        if (Networking.LocalPlayer == null) return;
        DebugState();
    }

    private void DebugState()
    {
        // skip update if debug text is off
        if (!DebugLogText.gameObject.activeInHierarchy) return;
        string s = $"{System.DateTime.Now} {Time.time} localPid={Networking.LocalPlayer.playerId} master?={Networking.IsMaster} \n" +
            $"broadcast={broadcastCooldown} releaseAttempt={nextReleaseAttempt} takeAttempt={nextOwnershipAttempt} " +
            $"ownershipAttempts={ownershipAttempts} Networking.isClogged={Networking.IsClogged}\n" +
            $"playerState=";
        for (int i = 0; i < playerStates.Length; i++)
        {
            if ((i % 4) == 0) s += "\n";
            MatchingTrackerPlayerState playerState = playerStates[i];
            var o = playerState.GetExplicitOwner();
            s += $"[{i}]=[{(o == null ? "" : GetDisplayName(o))}]:{playerState.ownerId} ";
        }
        s += $"\nlocalPop={localStatePopulation} localPlayerid={Networking.LocalPlayer.playerId}";
        DebugStateText.text = s;

        DisplayFullState();
    }

    private void DisplayFullState()
    {
        var globalState = ReadGlobalMatchingState();

        VRCPlayerApi[] players = GetOrderedPlayers();
        var playerCount = players.Length;
        string[] names = new string[playerCount];
        string s = "global matching state\n" +
            "✓ means \"has been matched\" from the row player's local perspective, '.' means \"has not been matched\". \n" +
            "to be auto-matched, both players must not think they've been matched before.\n\n";

        for (int i = 0; i < playerCount; i++)
        {
            names[i] = GetDisplayName(players[i]).PadRight(15).Substring(0, 15);
            s += $"{GetDisplayName(players[i]).PadLeft(15).Substring(0, 15)} ";
            for (int j = 0; j < playerCount; j++)
            {
                s += i == j ? "\\" : globalState[i * 80 + j] ? "✓" : ".";
            }
            s += "\n";
        }
        for (int i = 0; i < 15; i++)
        {
            s += "\n                "; // 16 spaces
            for (int j = 0; j < playerCount; j++)
            {
                s += names[j][i];
            }
        }
        FullStateDisplay.text = s;
    }

    // maintain ownership of exactly one of the MatchingTrackerPlayerState gameobjects
    public void MaintainLocalOwnership()
    {
        var localPlayerId = Networking.LocalPlayer.playerId;
        if (localPlayerState == null)
        {
            // exponentially backoff the next attempt to take ownership, capped at 30 seconds.
            ownershipAttempts++;
            float delay = Mathf.Min(30f, UnityEngine.Random.Range(1f, Mathf.Pow(2, ownershipAttempts)));
            SendCustomEventDelayedSeconds(nameof(MaintainLocalOwnership), delay);
            nextOwnershipAttempt = Time.time + delay;

            // start scanning at a hash of our display name to distribute players across the sync objects,
            // with linear probing on collision.
            // note that there will still be contention as the loading factor (number of players vs
            // total number of sync objects) increases. For an 80 person instance to actually converge,
            // you'll probably need to have around 100 sync gameobjects total.
            var playerName = GetDisplayName(Networking.LocalPlayer);
            int playerStateCount = playerStates.Length;
            var end = Mathf.Abs(playerName.GetHashCode()) % playerStateCount;

            Log($"{playerName} doesn't own MatchingTrackerPlayerState, attempt {ownershipAttempts} scanning at {end}," +
                $" cooldown {Time.time - nextOwnershipAttempt}");

            // first scan if we got ownership from our last attempt
            for (int i = (end + 1) % playerStateCount; i != end; i = (i + 1) % playerStateCount)
            {
                MatchingTrackerPlayerState playerState = playerStates[i];
                var owner = playerState.GetExplicitOwner();
                if (owner != null && owner.playerId == localPlayerId)
                {
                    Log($"{playerName} found ownership of {playerState.name}" +
                        $" setting localPlayerState");
                    localPlayerState = playerState;
                    return;
                }
            }

            // else, scan for an unowned one and attempt.
            for (int i = (end + 1) % playerStateCount; i != end; i = (i + 1) % playerStateCount)
            {
                MatchingTrackerPlayerState playerState = playerStates[i];
                var owner = playerState.GetExplicitOwner();
                if (owner == null)
                {
                    Log($"{playerName} taking ownership {playerState.gameObject.name}, cooldown {Time.time - nextOwnershipAttempt}");
                    playerState.gameObject.SetActive(true);
                    playerState.TakeExplicitOwnership();
                    return;
                }
            }
        }
        else
        {
            var owner = localPlayerState.GetExplicitOwner();
            if (owner == null || owner.playerId != localPlayerId)
            {
                // lost ownership somehow
                Log($"Lost ownership of {localPlayerState.name}, nulling localPlayerState");
                localPlayerState = null;
                SendCustomEventDelayedSeconds(nameof(MaintainLocalOwnership), 0.1f);
            }
            else
            {
                // make sure we don't have ownership of more than one
                float delay = UnityEngine.Random.Range(3f, 10f);
                SendCustomEventDelayedSeconds(nameof(MaintainLocalOwnership), delay);
                nextReleaseAttempt = Time.time + delay;
                bool foundOne = false;
                foreach (var playerState in playerStates)
                {
                    var o = playerState.GetExplicitOwner();
                    if (o != null && o.playerId == localPlayerId)
                    {
                        if (foundOne)
                        {
                            Log($"uhoh, found extra owned {playerState.name}, releasing.");
                            // release explicit ownership
                            playerState.ownerId = -1;
                        }
                        foundOne = true;
                    }
                }
            }
        }
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        string join(int[] a)
    {
        var s = "";
        foreach (var i in a)
        {
            s += $"{i},";
        }
        return s;
    }

    public void PeriodicallyBroadcast()
    {
        broadcastCooldown = UnityEngine.Random.Range(5f, 15f);
        SendCustomEventDelayedSeconds(nameof(PeriodicallyBroadcast), broadcastCooldown);
        DoBroadcast();
    }

    private void DoBroadcast()
    {
        if (localPlayerState == null) return;

        VRCPlayerApi[] players = GetOrderedPlayers();
        var playerCount = players.Length;

        int matchCount = 0;
        int[] matchedPlayerIds = new int[80];

        for (int i = 0; i < playerCount; i++)
        {
            var player = players[i];
            bool matchedWithPlayer = GetLocallyMatchedWith(player);
            if (matchedWithPlayer)
            {
                //Log($"matched with player id {player.playerId}");
                matchedPlayerIds[matchCount++] = player.playerId;
            }
        }
        //Log($"matched {matchCount} player ids: {join(matchedPlayerIds)}");
        localPlayerState.matchingState = matchedPlayerIds;
        localPlayerState.RequestSerialization();
    }

    // get players ordered by playerId, and stripped of the weird null players that
    // apparently occur sometimes.
    public VRCPlayerApi[] GetOrderedPlayers()
    {
        var playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);
        // XXX i think I got an exception indicating players can be null
        // somehow, so check and scream about it
        int nonNullCount = 0;
        for (int i = 0; i < playerCount; i++)
        {
            if (players[i] == null)
            {
                Log("uhoh, got a null player from GetPlayers, thanks vrchat.");
            } else
            {
                nonNullCount++;
            }
        }
        // if we're good
        if (nonNullCount == playerCount)
        {
            sort(players, playerCount);
            return players;
        }

        VRCPlayerApi[] ret = new VRCPlayerApi[nonNullCount];
        var n = 0;
        for (int i = 0; i < playerCount; i++)
        {
            if (players[i] != null)
            {
                ret[n++] = players[i];
            }
        }
        sort(ret, nonNullCount);
        return ret;
    }

    private void sort(VRCPlayerApi[] players, int playerCount)
    {
        int i, j;
        VRCPlayerApi p;
        int key;
        for (i = 1; i < playerCount; i++)
        {
            p = players[i];
            key = p.playerId;
            j = i - 1;
            while (j >= 0 && players[j].playerId > key)
            {
                players[j + 1] = players[j];
                j--;
            }
            players[j + 1] = p;
        }
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        void Log(string text)
    {
#if COMPILER_UDONSHARP
        if (DebugLogText.gameObject.activeInHierarchy)
        {
            if (DebugLogText.text.Split('\n').Length > 30)
            {
                // trim
                DebugLogText.text = DebugLogText.text.Substring(DebugLogText.text.IndexOf('\n') + 1);
            }
            DebugLogText.text += $"{System.DateTime.Now}: {text}\n";
        }
#endif
        Debug.Log($"[MaximalMatching] [MatchingTracker] {text}");
    }
}
