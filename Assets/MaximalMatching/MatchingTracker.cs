#define NO_LOCAL_TEST_PLAYERIDS

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

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
    private float takeOwnershipAttemptCooldown = -1;
    private float releaseOwnershipAttemptCooldown = -1;

    private float broadcastCooldown = -1;

    public bool started = false;

    void Start()
    {
        playerStates = PlayerStateRoot.GetComponentsInChildren<MatchingTrackerPlayerState>(includeInactive: true);
        Log($"Start MatchingTracker");
        started = true;

        // the more players in the instnace, the longer it'll take for the
        // initial sync; save some effort and retries by backing off trying to
        // take ownership based on player count (20 seconds for a full
        // instnace). XXX the playercount might not actually be initialized yet in
        // start(); it'll still be fine if it isn't though.
        takeOwnershipAttemptCooldown = VRCPlayerApi.GetPlayerCount() / 4f;

        SlowUpdate();
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
    }

    public void ClearLocalMatching()
    {
        Log($"Clearing local matching state.");
        localMatchingKey = new string[LOCAL_STATE_SIZE];
        localMatchingState = new bool[LOCAL_STATE_SIZE];
        localStatePopulation = 0;
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
                byte[] playerList = DeserializeFrame(playerStates[sidx].matchingState);
                int[] matchedPlayers = deserializeBytes(playerList);

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

    private float debugStateCooldown = -1;

    // crash watchdog
    public float lastUpdate;

    // periodically try to initialize. While it'd be nice to run this in Start()
    // I'm not sure if Networking.IsMaster will return true in Start() even if you are the master
    // so redo it just to be sure.
    private float lastInitializeCheck = 0;
    private void InitializePlayerStates()
    {
        if (!Networking.IsMaster) return;
        if ((lastInitializeCheck -= Time.deltaTime) > 0) return;
        lastInitializeCheck = 10f;
        foreach (var playerState in playerStates)
        {
            if (!playerState.IsInitialized()) playerState.Initialize();
        }
    }

    public void SlowUpdate()
    {
        SendCustomEventDelayedSeconds(nameof(SlowUpdate), 1);
        lastUpdate = Time.time;
        if (Networking.LocalPlayer == null) return;
        InitializePlayerStates();
        MaintainLocalOwnership();
        BroadcastLocalState();
        DebugState();
    }

    private void DebugState()
    {
        // skip update if debug text is off
        if (!DebugLogText.gameObject.activeInHierarchy) return;

        if ((debugStateCooldown -= Time.deltaTime) > 0) return;
        debugStateCooldown = 1f;
        string s = $"{System.DateTime.Now} localPid={Networking.LocalPlayer.playerId} master?={Networking.IsMaster} initCheck={lastInitializeCheck}\n" +
            $"broadcast={broadcastCooldown} releaseAttempt={releaseOwnershipAttemptCooldown} takeAttempt={takeOwnershipAttemptCooldown} " +
            $"ownershipAttempts={ownershipAttempts}\nplayerState=";
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

    private float lastTakeOwnershipAttempt;

    // maintain ownership of exactly one of the MatchingTrackerPlayerState gameobjects
    private void MaintainLocalOwnership()
    {
        var localPlayerId = Networking.LocalPlayer.playerId;
        if (localPlayerState == null)
        {
            if ((takeOwnershipAttemptCooldown -= Time.deltaTime) < 0)
            {
                // exponentially backoff the next attempt to take ownership, capped at 30 seconds.
                ownershipAttempts++;
                takeOwnershipAttemptCooldown = 
                    Mathf.Min(30f, UnityEngine.Random.Range(1f, Mathf.Pow(2, ownershipAttempts)));

                // start scanning at a hash of our display name to distribute players across the sync objects,
                // with linear probing on collision.
                // note that there will still be contention as the loading factor (number of players vs
                // total number of sync objects) increases. For an 80 person instance to actually converge,
                // you'll probably need to have around 100 sync gameobjects total.
                var playerName = GetDisplayName(Networking.LocalPlayer);
                int playerStateCount = playerStates.Length;
                var end = Mathf.Abs(playerName.GetHashCode()) % playerStateCount;

                Log($"{playerName} doesn't own MatchingTrackerPlayerState, attempt {ownershipAttempts} scanning at {end}," +
                    $" cooldown {takeOwnershipAttemptCooldown}");

                // first scan if we got ownership from our last attempt
                for (int i = (end + 1) % playerStateCount; i != end; i = (i + 1) % playerStateCount)
                {
                    MatchingTrackerPlayerState playerState = playerStates[i];
                    // skip uninitialized states
                    if (!playerState.IsInitialized()) continue;

                    var owner = playerState.GetExplicitOwner();
                    if (owner != null && owner.playerId == localPlayerId)
                    {
                        Log($"{playerName} found ownership of {playerState.name} after {Time.time - lastTakeOwnershipAttempt} seconds," +
                            $" setting localPlayerState");
                        localPlayerState = playerState;
                        return;
                    }
                }

                // else, scan for an unowned one and attempt.
                for (int i = (end + 1) % playerStateCount; i != end; i = (i + 1) % playerStateCount)
                {
                    MatchingTrackerPlayerState playerState = playerStates[i];
                    // skip uninitialized states
                    if (!playerState.IsInitialized()) continue;

                    var owner = playerState.GetExplicitOwner();
                    if (owner == null)
                    {
                        Log($"{playerName} taking ownership {playerState.gameObject.name}, cooldown {takeOwnershipAttemptCooldown}");
                        playerState.gameObject.SetActive(true);
                        playerState.TakeExplicitOwnership();
                        lastTakeOwnershipAttempt = Time.time;
                        return;
                    }
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
            }
            else
            {
                // make sure we don't have ownership of more than one
                if ((releaseOwnershipAttemptCooldown -= Time.deltaTime) < 0)
                {
                    releaseOwnershipAttemptCooldown = UnityEngine.Random.Range(1f, 2f);
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

    // 80 * 10 bits player ids. zeroes at the end will encode to zeros and signal end of list.
    // need to fit at 100 bytes. 105 bytes * 8 / 7 = 120 chars.
    private const int maxPacketCharSize = 120;
    private const int maxDataByteSize = 105;
    private void BroadcastLocalState()
    {
        if (localPlayerState == null) return;
        if ((broadcastCooldown -= Time.deltaTime) > 0) return;
        broadcastCooldown = UnityEngine.Random.Range(1f, 2f);

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
        byte[] buf = serializeBytes(matchCount, matchedPlayerIds);
        //Log($"serialized player ids: {System.Convert.ToBase64String(buf)}");
        localPlayerState.matchingState = new string(SerializeFrame(buf));
        localPlayerState.RequestSerialization();
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        byte[] serializeBytes(int matchCount, int[] matchedPlayerIds)
    {
        byte[] buf = new byte[maxDataByteSize];
        // serialize 4 10bit player ids into 5 bytes
        // TODO could go from 7 10bit player ids into 10 chars directly.
        for (int j = 0, k = 0; j < matchCount; j += 4, k += 5)
        {
            int a = matchedPlayerIds[j];
            int b = matchedPlayerIds[j+1];
            int c = matchedPlayerIds[j+2];
            int d = matchedPlayerIds[j+3];
            // first 8 of 0
            buf[k] = (byte)((a >> 2) & 255);
            // last 2 of 0, first 6 of 1
            buf[k + 1] = (byte)(((a & 3) << 6) + ((b >> 4) & 63));
            // last 4 of 1, first 4 of 2
            buf[k + 2] = (byte)(((b & 15) << 4) + ((c >> 6) & 15));
            // last 6 of 2, first 2 of 3
            buf[k + 3] = (byte)(((c & 63) << 2) + ((d >> 8) & 3));
            // last 8 of 3
            buf[k + 4] = (byte)(d & 255);
        }

        return buf;
    }

    // from https://github.com/hiinaspace/just-mahjong/

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        char[] SerializeFrame(byte[] buf)
    {
        var frame = new char[maxPacketCharSize];
        int n = 0;
        for (int i = 0; i < maxDataByteSize;)
        {
            // pack 7 bytes into 56 bits;
            ulong pack = buf[i++];
            pack = (pack << 8) + buf[i++];
            pack = (pack << 8) + buf[i++];

            pack = (pack << 8) + buf[i++];
            pack = (pack << 8) + buf[i++];
            pack = (pack << 8) + buf[i++];
            pack = (pack << 8) + buf[i++];
            //DebugLong("packed: ", pack);

            // unpack into 8 7bit asciis
            frame[n++] = (char)((pack >> 49) & (ulong)127);
            frame[n++] = (char)((pack >> 42) & (ulong)127);
            frame[n++] = (char)((pack >> 35) & (ulong)127);
            frame[n++] = (char)((pack >> 28) & (ulong)127);

            frame[n++] = (char)((pack >> 21) & (ulong)127);
            frame[n++] = (char)((pack >> 14) & (ulong)127);
            frame[n++] = (char)((pack >> 7) & (ulong)127);
            frame[n++] = (char)(pack & (ulong)127);
            //DebugChars("chars: ", chars, n - 8);
        }
        return frame;
    }

    public
#if !COMPILER_UDONSHARP
        static
#endif
        byte[] DeserializeFrame(string s)
    {
        var packet = new byte[maxDataByteSize];
        
        if (s.Length < maxPacketCharSize) return packet;

        var frame = new char[maxPacketCharSize];
        s.CopyTo(0, frame, 0, maxPacketCharSize);

        int n = 0;
        for (int i = 0; i < maxDataByteSize;)
        {
            //DebugChars("deser: ", chars, n);
            // pack 8 asciis into 56 bits;
            ulong pack = frame[n++];
            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];

            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];
            pack = (pack << 7) + frame[n++];
            //DebugLong("unpacked: ", pack);

            // unpack into 7 bytes
            packet[i++] = (byte)((pack >> 48) & (ulong)255);
            packet[i++] = (byte)((pack >> 40) & (ulong)255);
            packet[i++] = (byte)((pack >> 32) & (ulong)255);
            packet[i++] = (byte)((pack >> 24) & (ulong)255);

            packet[i++] = (byte)((pack >> 16) & (ulong)255);
            packet[i++] = (byte)((pack >> 8) & (ulong)255);
            packet[i++] = (byte)((pack >> 0) & (ulong)255);
        }
        return packet;
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
