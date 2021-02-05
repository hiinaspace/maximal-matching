
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
    // cleared every so often.
    const int LOCAL_STATE_SIZE = 80 * 4;
    private string[] localMatchingKey = new string[LOCAL_STATE_SIZE];
    private bool[] localMatchingState = new bool[LOCAL_STATE_SIZE];
    const int MAX_POPULATION = LOCAL_STATE_SIZE / 2;
    private int localStatePopulation = 0;

    private MatchingTrackerPlayerState localPlayerState = null;

    // If you have more than ~20 gameobjects with UdonBehaviors with synced
    // variables enabled in the scene, some internal udon networking thing will
    // near continually spam the console with "Deferred event [number] because
    // we are over BPS" and "Death run detected, killed [number] events". The
    // actual synced changes still seem to get through eventually, but the the
    // log spam is annoying.
    //
    // However, if you manually ensure that only ~20 gameobjects with synced
    // behaviors are active in the scene at a given time by flipping them on
    // and off, you can still (eventually) sync all the objects, while avoiding
    // any spam; presumably, the internal udon code only sees the ~20 objects
    // active, instead of trying to pack everything in the scene into a single
    // update (and trigger the messages). You can adjust the max enabled down
    // for higher latency but more overhead for other udon behaviors before the
    // messages show up.
    const int MAX_ACTIVE_GAMEOBJECTS = 10;
    private int enabledCursor = 0;

    // only attempt to take ownership every few seconds.
    private float takeOwnershipAttemptCooldown = -1;
    private float releaseOwnershipAttemptCooldown = -1;

    private float broadcastCooldown = -1;

    public bool started = false;

    void Start()
    {
        playerStates = PlayerStateRoot.GetComponentsInChildren<MatchingTrackerPlayerState>(includeInactive: true);
        Log($"Start MatchingTracker");
        started = true;
    }

    public bool GetLocallyMatchedWith(VRCPlayerApi other)
    {
        return lookup(other.displayName, localMatchingKey, localMatchingState);
    }

    public void SetLocallyMatchedWith(VRCPlayerApi other, bool wasMatchedWith)
    {
        if (set(other.displayName, wasMatchedWith, localMatchingKey, localMatchingState))
        {
            localStatePopulation++;
        }
        Log($"set matched with '{other.displayName}' to {wasMatchedWith}, population {localStatePopulation}");
        if (localStatePopulation > MAX_POPULATION)
        {
            RebuildLocalState();
        }
    }

    public void ClearLocalMatching()
    {
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
        bool set(string key, bool value, string[] keys, bool[] values)
    {
        var i = linearProbe(key, keys);
        var newKey = keys[i] == null;
        keys[i] = key;
        values[i] = value;
        return newKey;
    }

    // rebuild the hash table with only entries for current players
    // could instead keep track of insert time and keep track of the last 160 players or so, maybe later
    private void RebuildLocalState()
    {
        var playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);
        SortPlayersByPlayerId(players, playerCount);

        Log($"rebuilding local state to {playerCount} entries from {localStatePopulation} population");
        localStatePopulation = playerCount;
        string[] newMatchingKey = new string[LOCAL_STATE_SIZE];
        bool[] newMatchingState = new bool[LOCAL_STATE_SIZE];
        foreach (var player in players)
        {
            // copy to new map
            set(player.displayName, GetLocallyMatchedWith(player), newMatchingKey, newMatchingState);
        }
        localMatchingKey = newMatchingKey;
        localMatchingState = newMatchingState;
    }

    // deserializes all the player matching states into a (flattened) bool array indexable by ordinal.
    public bool[] ReadGlobalMatchingState()
    {
        var playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);
        SortPlayersByPlayerId(players, playerCount);

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
                Log($"player {player.displayName} id={pid} doesn't own a sync object yet");
            }
            byte[] bitmap = sidx == -1 ? new byte[10] :
                System.Convert.FromBase64String(playerStates[sidx].matchingState);

            for (int j = 0; j < playerCount; j++)
            {
                bool wasMatchedWith = ((bitmap[j / 8] >> (7 - j % 8)) & 1) == 1;
                globalState[n * 80 + j] = wasMatchedWith;
            }

            n++;
        }
        return globalState;
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

    private void Update()
    {
        lastUpdate = Time.time;
        if (Networking.LocalPlayer == null) return;
        InitializePlayerStates();
        JuggleActiveGameobjects();
        MaintainLocalOwnership();
        BroadcastLocalState();
        DebugState();
    }

    private void DebugState()
    {
        if ((debugStateCooldown -= Time.deltaTime) > 0) return;
        debugStateCooldown = 1f;
        string s = $"{System.DateTime.Now} localPid={Networking.LocalPlayer.playerId} master?={Networking.IsMaster} initCheck={lastInitializeCheck}\n" +
            $"broadcast={broadcastCooldown} releaseAttempt={releaseOwnershipAttemptCooldown} takeAttempt={takeOwnershipAttemptCooldown}\nplayerState=";
        for (int i = 0; i < playerStates.Length; i++)
        {
            MatchingTrackerPlayerState playerState = playerStates[i];
            var o = playerState.GetExplicitOwner();
            s += $"[{i}]=[{(o == null ? "" : o.displayName)}]:{playerState.ownerId}:{playerState.matchingState} ";
            if ((i % 4) == 0) s += "\n";
        }
        s += $"\nlocalPop={localStatePopulation} localPlayerid={Networking.LocalPlayer.playerId}";
        DebugStateText.text = s;

        DisplayFullState();

    }

    private void DisplayFullState()
    {
        var globalState = ReadGlobalMatchingState();

        var playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);
        SortPlayersByPlayerId(players, playerCount);
        string[] names = new string[playerCount];
        string s = "";

        for (int i = 0; i < playerCount; i++)
        {
            names[i] = players[i].displayName.PadRight(15).Substring(0, 15);
            s += $"{players[i].displayName.PadLeft(15).Substring(0, 15)} ";
            for (int j = 0; j < playerCount; j++)
            {
                s += i == j ? "\\" : globalState[i * 80 + j] ? "O" : ".";
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
    private void MaintainLocalOwnership()
    {
        var localPlayerId = Networking.LocalPlayer.playerId;
        if (localPlayerState == null)
        {
            if ((takeOwnershipAttemptCooldown -= Time.deltaTime) < 0)
            {
                // try again after a bit.
                takeOwnershipAttemptCooldown = UnityEngine.Random.Range(1f, 2f);
                Log($"no owned MatchingTrackerPlayerState, scanning for an unowned one");
                foreach (var playerState in playerStates)
                {
                    // skip uninitialized states
                    if (!playerState.IsInitialized()) continue;

                    var owner = playerState.GetExplicitOwner();
                    if (owner == null)
                    {
                        Log($"taking ownership {playerState.gameObject.name}, cooldown {takeOwnershipAttemptCooldown}");
                        playerState.gameObject.SetActive(true);
                        playerState.TakeExplicitOwnership();
                        break;
                    }
                    else if (owner.playerId == localPlayerId)
                    {
                        Log($"found ownership of {playerState.name}, setting localPlayerState");
                        localPlayerState = playerState;
                        break;
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

    private void BroadcastLocalState()
    {
        if (localPlayerState == null) return;
        if ((broadcastCooldown -= Time.deltaTime) > 0) return;
        broadcastCooldown = UnityEngine.Random.Range(1f, 2f);

        var playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);
        SortPlayersByPlayerId(players, playerCount);
        // 80 bits (79 other players at max)
        byte[] matchingBitmap = new byte[10];

        for (int i = 0; i < playerCount; i++)
        {
            var player = players[i];
            bool matchedWithPlayer = GetLocallyMatchedWith(player);
            if (matchedWithPlayer)
            {
                matchingBitmap[i / 8] |= (byte)(1 << (7 - i % 8));
            }
        }
        localPlayerState.matchingState = System.Convert.ToBase64String(matchingBitmap);
    }

    public void SortPlayersByPlayerId(VRCPlayerApi[] players, int playerCount)
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

    private void JuggleActiveGameobjects()
    {
        // just rotate one per frame.
        // I'm hoping that there's enough frame jitter that we don't get into a state where
        // two players will never have the same set of gameobjects active at the same time, thus
        // some states never sync. If that's the case will need to randomize more.

        // since the "death run" problem only occurs on owned gameobjects, keep any
        // remotely-owned gameobjects alive as well; if we become master and a bunch of gameobjects
        // get dumped on us, we'll hopefully disable them pretty quick.
        // also keep the local player state enabled locally, so we can push updates to it.
        var toDisable = playerStates[enabledCursor];
        if (toDisable != localPlayerState && Networking.IsOwner(toDisable.gameObject))
        {
            toDisable.gameObject.SetActive(false);
        }
        playerStates[(enabledCursor + MAX_ACTIVE_GAMEOBJECTS) % playerStates.Length].gameObject.SetActive(true);
        enabledCursor = (enabledCursor + 1) % playerStates.Length;
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        void Log(string text)
    {
#if COMPILER_UDONSHARP
        if (DebugLogText.text.Split('\n').Length > 30)
        {
            // trim
            DebugLogText.text = DebugLogText.text.Substring(DebugLogText.text.IndexOf('\n') + 1);
        }
        DebugLogText.text += $"{System.DateTime.Now}: {text}\n";
#endif
        Debug.Log($"[MaximalMatching] [MatchingTracker] {text}");
    }

}
