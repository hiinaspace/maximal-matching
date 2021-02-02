
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class AutoMatcher : UdonSharpBehaviour
{
    public UnityEngine.UI.Text DebugLogText;
    public UnityEngine.UI.Text DebugStateText;

    public MatchingTracker MatchingTracker;
    public OccupantTracker LobbyZone;
    public PrivateRoomTimer PrivateRoomTimer;
    public GameObject PrivateZoneRoot;

    // how long in the private room until it teleports you back 
    public float PrivateRoomTime = 15f;

    // how long to wait after the first 2 people who entered the lobby to start;
    public float LobbyStartWait = 10f;
    // how long to wait after the last person who entered the lobby for the lobby to start
    public float LobbyQuiescenceWait = 3f;

    // how long to wait after somebody leaves the instance (for the player ordinals to shift and be stable)
    public float MatchingTrackerQuiescenceWait = 10f;

    // how long to wait after failing to generate any matchings with the current set of players
    public float RetryWait = 10f;

    // base64 serialized:
    // 4 byte serverTimeMillis for checking for a new matching, and checking if
    //     any players left since the matching was published which would screw up the ordinals
    // 1 byte number of matches (up to 40)
    // ([1 byte player ordinal] [1 byte player ordinal] [1 byte room]) per matching
    private const int maxSyncedStringSize = 105;
    [UdonSynced] public string matchingState0 = "";
    [UdonSynced] public string matchingState1 = "";
    private string lastSeenState0 = "";
    private int lastSeenServerTimeMillis = 0;
    private int[] lastSeenMatching = new int[0];
    private int lastSeenMatchCount = 0;
    private int[] lastSeenRoomAssignment = new int[0];

    private bool lobbyReady;
    private float lobbyReadyTime;

    private float lastPlayerJoinTime, lastPlayerLeaveTime;
    // make sure nobody left in between the matching calculation and receipt
    private int lastPlayerLeaveServerTimeMillis = int.MinValue;

    // avoid querying the lobby zone every single frame
    private float LobbyCheckWait = 2f;
    private float lastLobbyCheck = 0;

    private OccupantTracker[] privateRooms;

    // crash watchdog
    public float lastUpdate;


    void Start()
    {
        privateRooms = PrivateZoneRoot.GetComponentsInChildren<OccupantTracker>();
        Log($"Start AutoMatcher");
    }

    private void Update()
    {
        lastUpdate = Time.time;
        var lobbyStartCountdown = Mathf.Max(0, LobbyStartWait - (Time.time - lobbyReadyTime));
        var lobbyQuiescenceCountdown = Mathf.Max(0, LobbyQuiescenceWait - (Time.time - LobbyZone.lastJoin));
        var playerLeaveCountdown = Mathf.Max(0, MatchingTrackerQuiescenceWait - (Time.time - lastPlayerLeaveTime));

        if ((lastLobbyCheck -= Time.deltaTime) < 0)
        {
            lastLobbyCheck = LobbyCheckWait;
            if (LobbyZone.occupancy > 1)
            {
                if (lobbyReady)
                {
                    if (lobbyStartCountdown <= 0 && lobbyQuiescenceCountdown <= 0 && playerLeaveCountdown <= 0)
                    {
                        if (Networking.IsMaster)
                        {
                            Log($"countdowns finished, calculating matching");
                            WriteMatching(LobbyZone.GetOccupants());
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
        }

        if (matchingState0 != lastSeenState0)
        {
            // got a new matching
            // note this also runs on the master on the frame the new matching is written.
            lastSeenState0 = matchingState0;
            ActOnMatching();
        }

        // debug state
        int[] privateRoomOccupancy = new int[privateRooms.Length];
        for (int i = 0; i < privateRooms.Length; i++)
        {
            privateRoomOccupancy[i] = privateRooms[i].occupancy;
        }
        DebugStateText.text = $"{System.DateTime.Now} local master?={Networking.IsMaster}\n" +
            $"lastLobbyCheck={lastLobbyCheck} lobbyReady={lobbyReady}\n" +
            $"lobbyStartCountdown={lobbyStartCountdown} (since lobby became ready)\n" +
            $"lobbyQuiescenceCountdown={lobbyQuiescenceCountdown} (since last player entered lobby)\n" +
            $"playerLeaveCountdown={playerLeaveCountdown} (since last player left instance)\n" +
            $"lobby.occupancy={LobbyZone.occupancy}\n" +
            $"matchingState0={matchingState0}\n" +
            $"lastSeenState0={lastSeenState0}\n" +
            $"lastSeenServerTimeMillis={lastSeenServerTimeMillis} currentServerTimeMillis={Networking.GetServerTimeInMilliseconds()}\n" +
            $"lastSeenMatchCount={lastSeenMatchCount} lastSeenMatching={join(lastSeenMatching)}\n" +
            $"lastSeenRoomAssignment={join(lastSeenRoomAssignment)}\n" +
            $"privateRoomOccupancy={join(privateRoomOccupancy)}";
    }

    // XXX string.Join doesn't work in udon
    private string join(int[] a)
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
        byte[] buf = System.Convert.FromBase64String(matchingState0 + matchingState1);
        if (buf.Length < 5) return;
        int n = 0;
        int time = 0;
        time |= (int)buf[n++] << 24;
        time |= (int)buf[n++] << 16;
        time |= (int)buf[n++] << 8;
        time |= (int)buf[n++];
        lastSeenServerTimeMillis = time;
        int matchCount = buf[n++];
        lastSeenMatchCount = matchCount;

        int[] matching = new int[matchCount * 2];
        int[] roomAssignment = new int[matchCount];
        for (int i = 0; i < matchCount; i++)
        {
            matching[i*2] = buf[n++];
            matching[i*2+1] = buf[n++];
            roomAssignment[i] = buf[n++];
        }
        lastSeenMatching = matching;

        Log($"Deserialized new matching at {lastSeenServerTimeMillis}, with {matchCount}\n" +
            $"matchings: [{join(matching)}] rooms: [{join(roomAssignment)}]");

        if (matchCount == 0) return; // nothing to do

        // XXX if a player leaves in this time then it's likely a few players will get teleported anyway
        // try to save at least some of them.
        if (lastPlayerLeaveServerTimeMillis > lastSeenServerTimeMillis)
        {
            Log($"Uhoh, a player left at {lastPlayerLeaveServerTimeMillis} but matching calculated at {time}, discarding matching");
            return;
        }

        if (!LobbyZone.localPlayerOccupying)
        {
            Log($"got new matching at {time} but local player not in the lobby zone, ignoring");
            return;
        }
        
        // once more have to get ordinals.
        var playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);
        MatchingTracker.SortPlayersByPlayerId(players, playerCount);

        int myOrdinal = 0;
        int myPlayerId = Networking.LocalPlayer.playerId;
        for (int i = 0; i < playerCount; i++)
        {
            if (players[i].playerId == myPlayerId)
            {
                myOrdinal = i;
                break;
            }
        }

        for (int i = 0; i < matchCount; i++)
        {
            if (matching[i*2] == myOrdinal || matching[i*2+1] == myOrdinal)
            {
                // we're matched, teleport to the ith unoccupied room
                // divided by 2 since there are two people per match
                var room = roomAssignment[i];
                Log($"found local player id={myPlayerId} ordinal={myOrdinal} at matching {i}, teleporting to room {room}");
                var p = privateRooms[room];

                // record
                MatchingTracker.SetLocallyMatchedWith(
                    players[matching[i*2] == myOrdinal ? matching[i*2 + 1] : matching[i*2]], true);

                // TODO could have better teleport locations whether you're first/second in the matching
                Networking.LocalPlayer.TeleportTo(p.transform.position, p.transform.rotation);
                PrivateRoomTimer.currentRoom = p;
                PrivateRoomTimer.StartCountdown(PrivateRoomTime);
                // teleport timer to location too as visual.
                PrivateRoomTimer.transform.position = p.transform.position;
                return;
                
            }
        }

        Log($"Local player id={myPlayerId} ordinal={myOrdinal} in the lobby, but not in the matching, womp womp");
    }

    private string mkugraph(bool[] ugraph, int eligibleCount)
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

    private void WriteMatching(VRCPlayerApi[] eligiblePlayers)
    {
        var eligibleCount = eligiblePlayers.Length;
        // have to get the full player list for ordinals.
        var playerCount = VRCPlayerApi.GetPlayerCount();
        VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
        VRCPlayerApi.GetPlayers(players);
        MatchingTracker.SortPlayersByPlayerId(players, playerCount);

        // N^2 recovery of the ordinals of the eligible players.
        int[] eligiblePlayerOrdinals = new int[eligibleCount];
        for (int i = 0; i < eligibleCount; i++)
        {
            var pid = eligiblePlayers[i].playerId;
            for (int j = 0; j < players.Length; j++)
            {
                VRCPlayerApi player = players[j];
                if (player.playerId == pid)
                {
                    eligiblePlayerOrdinals[i] = j;
                    break;
                }
            }
        }

        Log($"eligible player ordinals for matching: {join(eligiblePlayerOrdinals)}");

        var global = MatchingTracker.ReadGlobalMatchingState();

        // fold the global state as an undirected graph of just the eligible
        // players, i.e. if either player indicates they were matched (by their
        // local perception) with the other before, then don't match them. as
        // detailed in the README, this avoids some small griefing where a
        // player forces people to rematch with them by clearing their local
        // state/leaving and rejoining the instance.
        var ugraph = new bool[eligibleCount * eligibleCount];
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
                    !global[p1 * 80 + p2] && !global[p2 * 80 + p1];
            }
        }
        Log($"matching ugraph:\n{mkugraph(ugraph, eligibleCount)}");

        int[] matching = new int[eligibleCount];
        int matchCount = GreedyRandomMatching(ugraph, eligibleCount, matching);

        // calculate which rooms are currently unoccupied (on the master)
        // so players can consistently teleport to the same room.
        // XXX it's possible for players to clog up the rooms if they somehow avoid the teleport,
        // but the countdown timer should get them out in time.
        int[] rooms = new int[matchCount];
        int r = 0;
        for (int i = 0; i < privateRooms.Length; i++)
        {
            if (privateRooms[i].occupancy == 0)
            {
                if (r >= matchCount) break;
                rooms[r++] = i;
            }
        }

        Log($"calculated {matchCount} matchings: {join(matching)}, rooms: {join(rooms)}");

        // serialize
        int n = 0;
        byte[] buf = new byte[4 + 1 + matchCount * 3];
        // this is actually some arbitrary value, not even necessarily positive, but it is
        // apparently consistent across the instance.
        var time = Networking.GetServerTimeInMilliseconds();
        buf[n++] = (byte)((time >> 24) & 0xFF);
        buf[n++] = (byte)((time >> 16) & 0xFF);
        buf[n++] = (byte)((time >> 8)  & 0xFF);
        buf[n++] = (byte)(time         & 0xFF);
        buf[n++] = (byte)matchCount;
        for (int i = 0; i < matchCount; i++)
        {
            // turn the matches back into full player ordinals
            buf[n++] = (byte)eligiblePlayerOrdinals[matching[i*2]];
            buf[n++] = (byte)eligiblePlayerOrdinals[matching[i*2+1]];
            buf[n++] = (byte)rooms[i];
        }
        var frame = System.Convert.ToBase64String(buf);
        matchingState0 = frame.Substring(0, Mathf.Min(frame.Length, maxSyncedStringSize));
        matchingState1 = frame.Length > maxSyncedStringSize ? frame.Substring(maxSyncedStringSize) : "";
    }

    // pick a random eligible pair until you can't anymore. not guaranteed to be maximal.
    // https://en.wikipedia.org/wiki/Blossom_algorithm is the maximal way to do this. soon(tm)
    private int GreedyRandomMatching(bool[] ugraph, int count, int[] matching)
    {
        Log($"random matching {count} players.");
        int midx = 0;
        int[] matchable = new int[count];
        int matchableCount;
        while ((matchableCount = hasMatching(ugraph, count, matchable)) > 0)
        {
            int chosen1 = matchable[UnityEngine.Random.Range(0, matchableCount)];
            Log($"{matchableCount} matchable players remaining, chose {chosen1} first.");
            int chosen2 = -1;
            for (int j = chosen1 + 1; j < count; j++)
            {
                if (ugraph[chosen1 * count + j])
                {
                    chosen2 = j;
                    break;
                }
            }
            Log($"matched {chosen1} with {chosen2}.");
            for (int i = 0; i < count; i++)
            {
                // zero out column
                ugraph[i * count + chosen2] = false;
                // zero out row
                ugraph[chosen1 * count * i] = false;
            }
            matching[midx++] = chosen1;
            matching[midx++] = chosen2;
        }

        return midx / 2;
    }

    // ordinals that have at least one matching
    private int hasMatching(bool[] ugraph, int count, int[] matchable)
    {
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                if (ugraph[i * count + j])
                {
                    matchable[n++] = i;
                    break;
                }
            }
        }
        return n;
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        lastPlayerJoinTime = Time.time;
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        lastPlayerLeaveTime = Time.time;
        lastPlayerLeaveServerTimeMillis = Networking.GetServerTimeInMilliseconds();
    }
    public void Log(string text)
    {
        if (DebugLogText.text.Split('\n').Length > 30)
        {
            // trim
            DebugLogText.text = DebugLogText.text.Substring(DebugLogText.text.IndexOf('\n') + 1);
        }
        DebugLogText.text += $"{System.DateTime.Now}: {text}\n";
        Debug.Log($"[MaximalMatching] [MatchingTracker] {text}");
    }
}
