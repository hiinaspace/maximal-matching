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

    // UI toggle for whether local player wants to be matched
    public UnityEngine.UI.Toggle MatchingEnabledToggle;

    public GameObject PlayerStateRoot;

    public MatchingTrackerPlayerState[] playerStates;

    // local hashmap of player to "has been matched with", by hash of the other player's displayName,
    // since the absolute player ids will break after 1024 entries, size to at most 50% load factor.

    public const int LOCAL_STATE_SIZE = 2048;

    private string[] localMatchingKey = new string[LOCAL_STATE_SIZE];
    private bool[] localMatchingState = new bool[LOCAL_STATE_SIZE];
    private float[] lastChanged = new float[LOCAL_STATE_SIZE];
    private int localStatePopulation = 0;

    public MatchingTrackerPlayerState localPlayerState = null;

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
        // update our synced array
        SerializeLocalState();
        Log($"set matched with '{name}' to {wasMatchedWith}, population {localStatePopulation}");
    }

    public void ClearLocalMatching()
    {
        Log($"Clearing local matching state.");
        localMatchingKey = new string[LOCAL_STATE_SIZE];
        localMatchingState = new bool[LOCAL_STATE_SIZE];
        localStatePopulation = 0;
        // update our synced array
        SerializeLocalState();
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

    // called on ui change on the toggle
    public void UpdateMatchingEnabledToggle()
    {
        SerializeLocalState();
    }

    // deserializes all the player matching states into a (flattened) bool
    // array, indexable by owner of the player state array, and add all the
    // matchable (alive and willing) players in the `outPlayers` array.
    public bool[] ReadGlobalMatchingState(VRCPlayerApi[] outPlayers)
    {
        bool[] globalState = new bool[80 * 80];

        // collect ordinals by ids and fill outPlayers
        // ordinal in the sync object array (and the outPlayers array) by player id
        var ordinalById = new int[1024];
        for (int i = 0; i < 80; i++)
        {
            var state = playerStates[i];
            var owner = state.GetExplicitOwner();
            // only include present players that want to be matched still
            if (owner != null && state.matchingEnabled)
            {
                outPlayers[i] = owner;
                // add 1, so that 0 becomes a sentinel value for 'not here'
                ordinalById[owner.playerId] = i + 1;
            }
            else
            {
                // XXX mark their row as matched by everyone to avoid matching
                // on this ordinal. messy I know.
                for (int j = 0; j < 80; j++)
                {
                    globalState[i * 80 + j] = true;
                }
            }
        }

        // now that we know which players are live, fill out the rest of the array
        for (int i = 0; i < 80; i++)
        {
            var state = playerStates[i];
            var matchedIds = state.matchedPlayerIds;
            var owner = state.GetExplicitOwner();
            if (owner != null)
            {
                for (int j = 0; j < 80; j++)
                {
                    var matched = matchedIds[j];
                    if (matched == 0) break; // done with this player
                    var ordinal = ordinalById[matched] - 1;
                    // if matched player owns a sync object
                    if (ordinal >= 0)
                    {
                        // mark as matched
                        globalState[i * 80 + ordinal] = true;
                    }
                }
            }
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
        // skip update if debug text is off
        if (!DebugLogText.gameObject.activeInHierarchy) return;

        if ((debugStateCooldown -= Time.deltaTime) > 0) return;
        debugStateCooldown = 1f;
        string s = $"{System.DateTime.Now} localPid={Networking.LocalPlayer.playerId} master?={Networking.IsMaster} initCheck={lastInitializeCheck}\n" +
            $"broadcast={broadcastCooldown} releaseAttempt={releaseOwnershipAttemptCooldown} takeAttempt={takeOwnershipAttemptCooldown}\n" +
            $"localPlayerState={(localPlayerState == null ? "null" : localPlayerState.gameObject.name)}\n";
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
        VRCPlayerApi[] players = new VRCPlayerApi[80];
        var globalState = ReadGlobalMatchingState(players);

        string[] names = new string[80];
        string s = "global matching state\n" +
            "✓ means \"has been matched\" from the row player's local perspective, '.' means \"has not been matched\". \n" +
            "to be auto-matched, both players need to indicate they haven't been matched before.\n\n";

        for (int i = 0; i < 80; i++)
        {
            if (players[i] != null)
            {
                names[i] = GetDisplayName(players[i]).PadRight(15).Substring(0, 15);
                s += $"{GetDisplayName(players[i]).PadLeft(15).Substring(0, 15)} ";
                for (int j = 0; j < 80; j++)
                {
                    s += i == j ? "\\" : 
                        (players[j] == null ? " " : (globalState[i * 80 + j] ? "✓" : "."));
                }
            }
            else
            {
                s += "                "; // 16 spaces
                s += "                                                                                "; // 80 spaces
            }
            s += "\n";
        }
        for (int i = 0; i < 15; i++)
        {
            s += "\n                "; // 16 spaces
            for (int j = 0; j < 80; j++)
            {
                if (players[j] == null)
                {
                    s += " ";
                }
                else
                {
                    s += names[j][i];
                }
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

    // do this every so often just in case
    private void BroadcastLocalState()
    {
        if (localPlayerState == null) return;
        if ((broadcastCooldown -= Time.deltaTime) > 0) return;
        broadcastCooldown = UnityEngine.Random.Range(3f, 10f);
        SerializeLocalState();
    }

    // set our local player state object from the hash map and UI
    private void SerializeLocalState()
    {
        var localMatchingEnabled = MatchingEnabledToggle.isOn;
        VRCPlayerApi[] players = GetActivePlayers();
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
        localPlayerState.SerializeLocalState(matchedPlayerIds, matchCount, localMatchingEnabled);
        Log($"wrote local state to sync object matchedIds={join(matchedPlayerIds)} count={matchCount} matchingEnabled={localMatchingEnabled}");
    }

    // get players stripped of the weird null players that
    // apparently occur sometimes.
    public VRCPlayerApi[] GetActivePlayers()
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
        return ret;
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
