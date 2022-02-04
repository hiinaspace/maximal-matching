
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class AutoMatcher : UdonSharpBehaviour
{
    public UnityEngine.UI.Text DebugLogText;
    public UnityEngine.UI.Text DebugStateText;
    public UnityEngine.UI.Text FullStateDisplay;
    public UnityEngine.UI.Text CountdownText;

    public UnityEngine.UI.Slider MatchingDurationSlider;
    public UnityEngine.UI.Slider BreakDurationSlider;
    public UnityEngine.UI.Text MatchingDurationText;
    public UnityEngine.UI.Text BreakDurationText;
    public UnityEngine.UI.Text MasterIndicator;

    public UnityEngine.UI.Toggle VariantToggle;

    public MatchingTracker MatchingTracker;
    public OccupantTracker LobbyZone;
    public PrivateRoomTimer PrivateRoomTimer;
    public GameObject PrivateZoneRoot;

    [UdonSynced]
    public bool variantsEnabled = false;

    // if enabled, variants replace every nth regular round.
    private const int variantFrequency = 6;

    // for 5x rounds at default 5 minutes. could make adjustable but it's confusing enough as is.
    private const float LightningMatchingDuration = 60f;
    // unfortunately still need a bit of time to teleport players back before next matching
    private const float LightningBreakDuration = 5f;

    private const int GroupMatchingSize = 6;

    // counter for variant round switching (since lighting round needs advance matchEpoch, can't reuse)
    //  roundEpoch % variantFrequency == 0 detects next variant round.
    [UdonSynced]
    public int roundEpoch = 0;
    private int lastSeenRoundEpoch = 0;

    // matching variant enum, effects how `matching` array is generated and interpreted,
    // as well as countdown display.
    private const int REGULAR = 0;
    private const int LIGHTNING = 1;
    private const int GROUP = 2;
    private const int RECESS = 3;

    [UdonSynced]
    public int gameVariant = REGULAR;

    // how long in the private room until it teleports you back 
    [UdonSynced]
    public float MatchingDuration = 15f;

    // how long between the end of the private room time and the next matching.
    // needs to be long enough for all players to get teleported back into the lobby
    // from the private rooms, or there will be no one to match
    [UdonSynced]
    public float BreakDuration = 10f;

    // time until the first round starts after players initially enter the zone,
    // so don't have to wait a full round time to start.
    public float TimeUntilFirstRound = 10f;

    // simple counter so we know when we get an actual new match
    [UdonSynced]
    public int matchEpoch;
    private int lastSeenMatchEpoch;

    [UdonSynced]
    public int matchCount;
    // player matchings as a flat int of player ids, i.e. match, idx 0 and 1, 2 and 3, etc.
    [UdonSynced]
    public int[] matching;
    [UdonSynced]
    public int matchingServerTimeMillis;

    private int[] lastSeenMatching = new int[0];
    private int lastSeenMatchCount = 0;

    private int lastSeenMatchingServerTimeMillis = 0;
    private int lastSeenRoundTime = 0;

    private Transform[] privateRooms;

    private float lobbyReadyTime;
    private bool lobbyReady;

    // crash watchdog
    public float lastUpdate;

    void Start()
    {
        privateRooms = new Transform[PrivateZoneRoot.transform.childCount];
        // XXX no good way to get direct children of a transform except this apparently.
        int i = 0;
        foreach (Transform room in PrivateZoneRoot.transform)
        {
            privateRooms[i++] = room;

        }
        Log($"Start AutoMatcher");
        SlowUpdate();
    }

    public void SlowUpdate()
    {
        SendCustomEventDelayedSeconds(nameof(SlowUpdate), .99f);

        if (Networking.LocalPlayer == null) return;
        lastUpdate = Time.time;

        // if we haven't seen a matching yet
        // count down from the first time there were at least 2 people in the lobby
        // if we have seen matching, then count down (server time millis) from last seen by round time + between round time.
        // TODO this is weird and I think I can handle this better, but I'm sleepy. Need to wait less if zero players are matched.
        var timeSinceLobbyReady = Time.time - lobbyReadyTime;
        var timeSinceLastRound = ((float)Networking.GetServerTimeInMilliseconds() - (float)lastSeenRoundTime) / 1000.0f;
        var timeSinceLastMatching = ((float)Networking.GetServerTimeInMilliseconds() - (float)lastSeenMatchingServerTimeMillis) / 1000.0f;

        if (LobbyZone.occupancy > 1)
        {
            if (lobbyReady)
            {
                if (Networking.IsMaster)
                {
                    // very first match
                    if (matchEpoch == 0 && timeSinceLobbyReady > TimeUntilFirstRound)
                    {
                        Log($"initial countdown finished, trying first matching");
                        DoMatching(LobbyZone.GetOccupants());
                    }
                }
            }
            else
            {
                lobbyReadyTime = Time.time;
                lobbyReady = true;
                Log($"lobby became ready, has >1 players in it at {lobbyReadyTime}");
            }
        }
        else
        {
            if (lobbyReady)
            {
                Log($"lobby was ready, but players left");
            }
            lobbyReady = false;
        }

        if (Networking.IsMaster)
        {
            // if we have done another matching before, wait the full time for the next round
            if (matchEpoch != 0 && timeSinceLastRound > (MatchingDuration + BreakDuration))
            {
                Log($"ready for new matching");
                DoMatching(LobbyZone.GetOccupants());
            } else
            {
                // for lightning, send a new lightning match without advancing round epoch/countdown
                if (gameVariant == LIGHTNING && timeSinceLastMatching > (LightningMatchingDuration + LightningBreakDuration))
                {
                    Log($"ready for new lightning matching");
                    DoLightningMatching();
                }
            }
        }


        UpdateUi();

        UpdateCountdownDisplay(timeSinceLobbyReady, timeSinceLastRound, timeSinceLastMatching);
        DebugState(timeSinceLobbyReady, timeSinceLastRound);
    }

    public override void OnDeserialization()
    {
        if (matchEpoch != lastSeenMatchEpoch)
        {
            // got a new matching
            // note this also runs on the master on the frame the new matching is written, updating the `seen` variables.
            lastSeenMatchEpoch = matchEpoch;
            ActOnMatching();
        }
    }

    private float lastUiBroadcast = -1;

    public void UpdateUi()
    {
        if (Networking.IsMaster) 
        {
            if (MatchingDurationSlider.value != MatchingDuration)
            {
                float slide = MatchingDurationSlider.value;
                // use precise seconds below 2 minutes, otherwise round to 30 second intervals
                MatchingDuration = slide > 120 ?  Mathf.Round(slide / 30f) * 30 : slide;

                MatchingDurationText.text = MatchingDuration > 120 ?
                    $"{Mathf.RoundToInt(MatchingDuration / 30f) / 2f} minutes" :
                    $"{Mathf.Floor(MatchingDuration)} seconds";
                if ((Time.time - lastUiBroadcast) > 2f)
                {
                    lastUiBroadcast = Time.time;
                    RequestSerialization();
                }
            }
            if (BreakDurationSlider.value != BreakDuration)
            {
                BreakDuration = BreakDurationSlider.value;
                BreakDurationText.text = $"{Mathf.RoundToInt(BreakDuration)} seconds";
                if ((Time.time - lastUiBroadcast) > 2f)
                {
                    lastUiBroadcast = Time.time;
                    RequestSerialization();
                }
            }
            if (variantsEnabled != VariantToggle.enabled)
            {
                variantsEnabled = VariantToggle.enabled;
                RequestSerialization();
            }
        }
        else
        {
            if (MatchingDuration != MatchingDurationSlider.value)
            {
                MatchingDurationSlider.value = MatchingDuration;
                MatchingDurationText.text = MatchingDuration > 120 ?
                    $"{Mathf.Floor(MatchingDuration / 60f)} minutes" :
                    $"{Mathf.Floor(MatchingDuration)} seconds";
            }
            if (BreakDuration != BreakDurationSlider.value)
            {
                BreakDurationSlider.value = BreakDuration;
                BreakDurationText.text = $"{Mathf.RoundToInt(BreakDuration)} seconds";
            }
            VariantToggle.enabled = variantsEnabled;
        }
        MasterIndicator.text = $"(Only master {Networking.GetOwner(gameObject).displayName} can change)";
    }

    private void UpdateCountdownDisplay(float timeSinceLobbyReady, float timeSinceLastRound, float timeSinceLastMatching)
    {
        string text;
        if (matchEpoch == 0)
        {
            text = LobbyZone.occupancy > 1 ?
                $"First matching in {TimeUntilFirstRound - timeSinceLobbyReady:##} seconds" :
                "Waiting for players in the Matching Room";
        }
        else
        {
            float seconds = MatchingDuration + BreakDuration - timeSinceLastRound;
            float minutes = Mathf.Floor(seconds / 60.0f);

            if (variantsEnabled)
            {
                var roundsTilVariant = variantFrequency - roundEpoch % variantFrequency;
                var nextVariant = roundEpoch / variantFrequency / 3;
                var variantName = nextVariant == 0 ? "Lightning Matching" : nextVariant == 1 ? "Group Matching" : "Recess";

                text =
                    $"Next matching in {minutes:00}:{seconds % 60:00}\n" +
                    $"({variantName} " + (roundsTilVariant > 1 ? $"in {roundsTilVariant} rounds" : "next round") + ")";
            }
            else
            {
                if (gameVariant == LIGHTNING)
                {
                    float lightningSeconds = LightningMatchingDuration + LightningBreakDuration - timeSinceLastMatching;
                    float lightningMinutes = Mathf.Floor(lightningSeconds / 60.0f);
                    text =
                        $"Lightning Rounds for {minutes:00}:{seconds % 60:00}\n" +
                        $"(Next match in {lightningMinutes:00}:{lightningSeconds % 60:00})";
                }
                else
                {
                    text =
                        $"Next matching in {minutes:00}:{seconds % 60:00}";
                }
            }
        }
        CountdownText.text = text;
    }

    private void DebugState(float timeSinceLobbyReady, double timeSinceLastRound)
    {
        // skip update if debug text is off
        if (!DebugLogText.gameObject.activeInHierarchy) return;

        var countdown = matchEpoch == 0 ?
            (LobbyZone.occupancy > 1 ? $"{TimeUntilFirstRound - timeSinceLobbyReady} seconds to initial round" : "(need players)") :
            $"{(MatchingDuration + BreakDuration - timeSinceLastRound)} seconds";

        DebugStateText.text = $"{System.DateTime.Now} localPid={Networking.LocalPlayer.playerId} master?={Networking.IsMaster}\n" +
            $"countdown to next matching: {countdown}\n" +
            $"timeSinceLobbyReady={timeSinceLobbyReady} lobbyReady={lobbyReady}\n" +
            $"timeSinceLastRound={timeSinceLastRound} (wait {MatchingDuration + BreakDuration} since last successful matching)\n" +
            $"lobby.occupancy={LobbyZone.occupancy}\n" +
            $"lobby.localPlayerOccupying={LobbyZone.localPlayerOccupying}\n" +
            $"lastSeenServerTimeMillis={lastSeenMatchingServerTimeMillis} millisSinceNow={Networking.GetServerTimeInMilliseconds() - lastSeenMatchingServerTimeMillis}\n" +
            $"lastSeenMatchCount={lastSeenMatchCount} lastSeenMatching={join(lastSeenMatching)}\n" +
            $"gameVariant={gameVariant}";

        if (!MatchingTracker.started) return;
        var count = LobbyZone.occupancy;
        if (count < 2)
        {
            FullStateDisplay.text = "not enough players in lobby.";
            return;
        }

        VRCPlayerApi[] players = MatchingTracker.GetOrderedPlayers();
        var playerCount = players.Length;

        var global = MatchingTracker.ReadGlobalMatchingState();

        // TODO optimize
        var eligiblePlayers = LobbyZone.GetOccupants();
        int[] eligiblePlayerIds = new int[eligiblePlayers.Length];
        for (int i = 0; i < eligiblePlayers.Length; i++)
        {
            eligiblePlayerIds[i] = eligiblePlayers[i].playerId;
        }
        int[] orderedPlayerIds = new int[players.Length];
        for (int i = 0; i < players.Length; i++)
        {
            orderedPlayerIds[i] = players[i].playerId;
        }

        var matchingObject = CalculateMatching(eligiblePlayerIds, orderedPlayerIds, global, 80);
        int[] eligiblePlayerOrdinals = (int[])matchingObject[0];
        int[] matching = (int[])matchingObject[1];
        int matchCount = (int)matchingObject[2];
        bool[] originalUgraph = (bool[])matchingObject[3];
        var s = $"current potential matching:\n";
        s += $"eligiblePlayerOrdinals={join(eligiblePlayerOrdinals)}\n";
        s += $"matchCount={matchCount}\n";
        s += $"matching={join(matching)}\n";
        s += $"originalUgraph:\n\n";
        string[] names = new string[playerCount];

        for (int i = 0; i < count; i++)
        {
            var ordinal = eligiblePlayerOrdinals[i];
            names[i] = players[ordinal].displayName.PadRight(15).Substring(0, 15);
            s += $"{players[ordinal].displayName.PadLeft(15).Substring(0, 15)} ";

            for (int j = 0; j < i; j++) s += " ";

            for (int j = i + 1; j < count; j++)
            {
                s += originalUgraph[i * count + j] ? "O" : ".";
            }
            s += "\n";
        }
        for (int i = 0; i < 15; i++)
        {
            s += "\n                "; // 16 spaces
            for (int j = 0; j < count; j++)
            {
                s += names[j][i];
            }
        }

        FullStateDisplay.text = s;
    }

    // XXX string.Join doesn't work in udon
    private 
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

    private void ActOnMatching()
    {
        lastSeenMatchingServerTimeMillis = matchingServerTimeMillis;
        lastSeenMatchCount = matchCount;
        lastSeenMatching = matching;
        // XXX update separate round time if it changed. messy but I'm sleepy
        if (roundEpoch != lastSeenRoundEpoch)
        {
            lastSeenRoundEpoch = roundEpoch;
            lastSeenRoundTime = matchingServerTimeMillis;
        }

        Log($"Deserialized new matching epoch {matchEpoch} with {matchCount}\n" +
            $"matchings: [{join(matching)}]");

        if (matchCount == 0) return; // nothing to do
        
        VRCPlayerApi[] players = MatchingTracker.GetOrderedPlayers();
        int myPlayerId = Networking.LocalPlayer.playerId;

        if (gameVariant == GROUP)
        {
            // matching is just a shuffled list of player ids and matchCount is a player count
            for (int i = 0; i < matchCount; i++)
            {
                if (matching[i] == myPlayerId)
                {
                    var roomIdx = i / GroupMatchingSize;
                    // if we're an odd one out 
                    if (matchCount % GroupMatchingSize == 1 && (i == matchCount - 1))
                    {
                        // move to the first room
                        roomIdx = 0;
                    }
                    Log($"Group matching at idx {i} into room {roomIdx}");

                    var p = privateRooms[roomIdx];

                    // 2m circle
                    float angle = (2 * Mathf.PI / GroupMatchingSize) * (i % GroupMatchingSize);
                    Vector3 adjust = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 2;
                    // look at the center of the room
                    Quaternion rotation = Quaternion.LookRotation(adjust * -1);
                    // avoid lerping (apparently on by default)
                    Networking.LocalPlayer.TeleportTo(adjust + p.transform.position, rotation,
                        VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, lerpOnRemote: false);
                    PrivateRoomTimer.StartCountdown(MatchingDuration);
                    // teleport timer to location too as visual.
                    PrivateRoomTimer.transform.position = p.transform.position;
                    PrivateRoomTimer.transform.rotation = rotation;
                    return;
                }
            }
            return;
        }

        for (int i = 0; i < matchCount; i++)
        {
            if (matching[i*2] == myPlayerId || matching[i*2+1] == myPlayerId)
            {
                var other = matching[i * 2] == myPlayerId ? matching[i * 2 + 1] : matching[i * 2];
                VRCPlayerApi otherPlayer = null;
                foreach (var pl in players)
                {
                    if (pl.playerId == other)
                    {
                        otherPlayer = pl;
                        break;
                    }
                }
                if (otherPlayer == null)
                {
                    Log($"found local player id={myPlayerId} matched with {other}, but {other} seems to have left, aborting..");
                    return;
                }

                // we're matched, teleport to the ith unoccupied room
                // divided by 2 since there are two people per match
                Log($"found local player id={myPlayerId} matched with id={other} name={otherPlayer.displayName}, teleporting to room {i}");
                var p = privateRooms[i];

                // record if not in lightning mode
                if (gameVariant == REGULAR)
                {
                    MatchingTracker.SetLocallyMatchedWith(otherPlayer, true);
                }

                Vector3 adjust = matching[i * 2] == myPlayerId ? Vector3.forward : Vector3.back;
                // look at the center of the room
                Quaternion rotation = Quaternion.LookRotation(adjust * -1);
                // avoid lerping (apparently on by default)
                Networking.LocalPlayer.TeleportTo(adjust + p.transform.position, rotation,
                    VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, lerpOnRemote: false);


                var timeSinceLastFullRound = ((float)Networking.GetServerTimeInMilliseconds() - (float)lastSeenRoundTime) / 1000f;
                var timeUntilNextRoundMinusBreak = MatchingDuration - timeSinceLastFullRound;
                var countdown = gameVariant == LIGHTNING ?
                    // shorten last lightning round so people aren't stranded in rooms when round rolls over
                    // XXX very confusing yes, again bad state machine logic for the lightning rounds
                    Mathf.Min(timeUntilNextRoundMinusBreak, LightningMatchingDuration) :
                    MatchingDuration;

                PrivateRoomTimer.StartCountdown(countdown);
                // teleport timer to location too as visual.
                PrivateRoomTimer.transform.position = p.transform.position;
                PrivateRoomTimer.transform.rotation = rotation;
                return;
            }
        }

        Log($"Local player id={myPlayerId} was not in the matching, oh well");
    }

    private 
#if !COMPILER_UDONSHARP
        static
#endif
        string mkugraph(bool[] ugraph, int eligibleCount)
    {
        var s = "";
        for (int i = 0; i < eligibleCount; i++)
        {
            for (int j = i + 1; j < eligibleCount; j++)
            {
                s += ugraph[i * eligibleCount + j] ? "O" : ".";
            }
            s += "|";
        }
        return s;
    }

    // XXX do just a mid-round lightning matching, without advancing roundEpoch
    // the state transitions are unfortunately complicated for what this does, could
    // definitely be done simpler
    private void DoLightningMatching()
    {
        var eligiblePlayers = LobbyZone.GetOccupants();
        int[] eligiblePlayerIds = new int[eligiblePlayers.Length];
        for (int i = 0; i < eligiblePlayers.Length; i++)
        {
            eligiblePlayerIds[i] = eligiblePlayers[i].playerId;
        }

        matchEpoch++;
        matchingServerTimeMillis = Networking.GetServerTimeInMilliseconds();
        // just shuffle the eligible player ids for random matches
        // last player will be odd man out
        Utilities.ShuffleArray(eligiblePlayerIds);
        matching = eligiblePlayerIds;
        matchCount = eligiblePlayerIds.Length / 2;

        RequestSerialization();
        // called on master, since OnDeserialization doesn't seem to run for our own set.
        OnDeserialization();
    }

    private void DoMatching(VRCPlayerApi[] eligiblePlayers)
    {
        var global = MatchingTracker.ReadGlobalMatchingState();
        // have to get the full player list for ordinals.
        VRCPlayerApi[] players = MatchingTracker.GetOrderedPlayers();
        // TODO optimize
        int[] eligiblePlayerIds = new int[eligiblePlayers.Length];
        for (int i = 0; i < eligiblePlayers.Length; i++)
        {
            eligiblePlayerIds[i] = eligiblePlayers[i].playerId;
        }
        int[] orderedPlayerIds = new int[players.Length];
        for (int i = 0; i < players.Length; i++)
        {
            orderedPlayerIds[i] = players[i].playerId;
        }

        roundEpoch++;
        matchEpoch++;
        matchingServerTimeMillis = Networking.GetServerTimeInMilliseconds();

        if (variantsEnabled && (roundEpoch % variantFrequency) == 0)
        {
            gameVariant = 1 + roundEpoch / variantFrequency / 3;
            Log($"doing variant game {gameVariant}");
            switch (gameVariant)
            {
                case LIGHTNING: 
                    // just shuffle the eligible player ids for random matches
                    // last player will be odd man out
                    Utilities.ShuffleArray(eligiblePlayerIds);
                    matching = eligiblePlayerIds;
                    matchCount = eligiblePlayerIds.Length / 2;
                    break;
                case GROUP:
                    // just shuffle the eligible player ids. ActOnMatching interprets appropriately as random rooms
                    Utilities.ShuffleArray(eligiblePlayerIds);
                    matching = eligiblePlayerIds;
                    matchCount = eligiblePlayerIds.Length;
                    break;
                case RECESS:
                    // do nothing, just advance the epochs
                    matchCount = 0;
                    matching = new int[0];
                    break;
            }
        }
        else
        {
            gameVariant = REGULAR;

            var matchingObject = CalculateMatching(eligiblePlayerIds, orderedPlayerIds, global, 80);

            int[] eligiblePlayerOrdinals = (int[])matchingObject[0];
            int[] ordinalMatching = (int[])matchingObject[1];
            int matchCount = (int)matchingObject[2];
            string log = (string)matchingObject[4];

            Log(log);

            int len = matchCount * 2;
            // convert matches from player orderinals to playerIds
            matching = new int[len];
            for (int i = 0; i < len; ++i)
            {
                matching[i] = players[eligiblePlayerOrdinals[ordinalMatching[i]]].playerId;
            }

            this.matchCount = matchCount;
        }

        RequestSerialization();

        // called on master, since OnDeserialization doesn't seem to run for our own set.
        OnDeserialization();
    }

    public
#if !COMPILER_UDONSHARP
        static
#endif
        object[] CalculateMatching(int[] eligiblePlayerIds, int[] orderedPlayerIds, bool[] global, int globalDim)
    {
        // stick logs in local variable instead of spamming them in the console.
        string[] log = new string[] { "" };

        var eligibleCount = eligiblePlayerIds.Length;
        log[0] += $"{eligibleCount} players eligible for matching. ";

        // N^2 recovery of the ordinals of the eligible players.
        int[] eligiblePlayerOrdinals = new int[eligibleCount];
        for (int i = 0; i < eligibleCount; i++)
        {
            var pid = eligiblePlayerIds[i];
            for (int j = 0; j < orderedPlayerIds.Length; j++)
            {
                if (orderedPlayerIds[j] == pid)
                {
                    eligiblePlayerOrdinals[i] = j;
                    break;
                }
            }
        }

        log[0] += $"eligible player ordinals for matching: {join(eligiblePlayerOrdinals)}. ";

        // fold the global state as an undirected graph of just the eligible
        // players, i.e. if either player indicates they were matched (by their
        // local perception) with the other before, then don't match them. as
        // detailed in the README, this avoids some small griefing where a
        // player forces people to rematch with them by clearing their local
        // state/leaving and rejoining the instance.
        var ugraph = new bool[eligibleCount * eligibleCount];
        // since ugraph is mutated in place, keep a copy for debugging
        var originalUgraph = new bool[eligibleCount * eligibleCount];
        for (int i = 0; i < eligibleCount; i++)
        {
            int p1 = eligiblePlayerOrdinals[i];
            // only need top triangle of the matrix
            for (int j = i + 1; j < eligibleCount; j++)
            {
                int p2 = eligiblePlayerOrdinals[j];
                // small graph is eligible for match
                ugraph[i * eligibleCount + j] =
                    // if both player says they haven't been matched
                    !global[p1 * globalDim + p2] && !global[p2 * globalDim + p1];

                originalUgraph[i * eligibleCount + j] = ugraph[i * eligibleCount + j];
            }
        }
        log[0] += ($"matching ugraph:\n{mkugraph(ugraph, eligibleCount)}. ");

        // get closest even matching
        int[] matching = new int[(int)(eligibleCount / 2) * 2];
        int matchCount = GreedyRandomMatching(ugraph, eligibleCount, matching, log);

        log[0] += ($"calculated {matchCount} matchings: {join(matching)}.");

        // such is udon
        return new object[] { eligiblePlayerOrdinals, matching, matchCount, originalUgraph, log[0]};
    }

    // pick a random eligible pair until you can't anymore. not guaranteed to be maximal.
    // https://en.wikipedia.org/wiki/Blossom_algorithm is the maximal way to do this. soon(tm)
    public 
#if !COMPILER_UDONSHARP
        static
#endif
        int GreedyRandomMatching(bool[] ugraph, int count, int[] matching, string[] log)
    {
        log[0] += ($"random matching {count} players on ugraph: {mkugraph(ugraph, count)}\n");
        int midx = 0;
        int[] matchable = new int[count];
        int matchableCount;
        while ((matchableCount = hasMatching(ugraph, count, matchable, log)) >= 1)
        {
            int chosen1 = matchable[UnityEngine.Random.Range(0, matchableCount)];
            log[0] += ($"{matchableCount} matchable players remaining, chose {chosen1} first.\n");
            int chosen2 = -1;
            for (int j = chosen1 + 1; j < count; j++)
            {
                if (ugraph[chosen1 * count + j])
                {
                    chosen2 = j;
                    break;
                }
            }
            for (int i = 0; i < count; i++)
            {
                // zero out columns
                ugraph[i * count + chosen2] = false;
                ugraph[i * count + chosen1] = false;
                // zero out rows
                ugraph[chosen1 * count + i] = false;
                ugraph[chosen2 * count + i] = false;
            }

            log[0] += ($"after matching {chosen1} and {chosen2}, ugraph: {mkugraph(ugraph, count)}\n");
            matching[midx++] = chosen1;
            matching[midx++] = chosen2;
        }

        return midx / 2;
    }

    // ordinals that have at least one matching
    private 
#if !COMPILER_UDONSHARP
        static
#endif
        int hasMatching(bool[] ugraph, int count, int[] matchable, string[] log)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                if (ugraph[i * count + j])
                {
                    // TODO this can't ever select the very last player I think. need to check logic.
                    matchable[n++] = i;
                    break;
                }
            }
        }
        log[0] += ($"found {n} matchable players in {mkugraph(ugraph, count)}");
        return n;
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        void Log(string text)
    {
        Debug.Log($"[MaximalMatching] [AutoMatcher] {text}");
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
    }
}
