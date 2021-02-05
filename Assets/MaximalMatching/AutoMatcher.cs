
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class AutoMatcher : UdonSharpBehaviour
{
    public UnityEngine.UI.Text DebugLogText;
    public UnityEngine.UI.Text DebugStateText;
    public UnityEngine.UI.Text FullStateDisplay;

    public MatchingTracker MatchingTracker;
    public OccupantTracker LobbyZone;
    public PrivateRoomTimer PrivateRoomTimer;
    public GameObject PrivateZoneRoot;

    // how long in the private room until it teleports you back 
    public float PrivateRoomTime = 15f;

    // how long between the end of the private room time and the next matching.
    // currently needs to be long enough for the players to get teleported back into the lobby
    // from the private rooms.
    // TODO could change the eligible players for matching to include those in the private rooms,
    // thus players in the private rooms will seamlessly get teleported around to the next round.
    public float BetweenRoundTime = 10f;

    // time until the first round starts after players initially enter the zone,
    // so don't have to wait a full round time to start.
    public float TimeUntilFirstRound = 10f;

    // how long to wait for retry after failing to match due to desynced players.
    public float MatchingTrackerQuiescenceWait = 3f;

    // seconds until the next matching round
    private float nextRoundCountdown = 0;

    // base64 serialized:
    // 4 byte serverTimeMillis for checking for a new matching, and the countdown till a new round
    //     
    // 1 byte number of matches (up to 40)
    // ([2 byte player id] [2 byte player id]) per matching
    //
    // since we're only using two bytes, this serialization will break after 65535
    // player enter the same instance. this is probably fine for vrchat.
    //
    // base64 encoding of 210 chars is only ~157 bytes, and we need 165 bytes, so we
    // have to use 7bit char encoding.
    private const int maxSyncedStringSize = 105;
    [UdonSynced] public string matchingState0 = "";
    [UdonSynced] public string matchingState1 = "";
    private string lastSeenState0 = "";
    private int lastSeenMatchingServerTimeMillis = 0;
    private int[] lastSeenMatching = new int[0];
    private int lastSeenMatchCount = 0;

    private OccupantTracker[] privateRooms;

    private float lobbyReadyTime;
    private bool lobbyReady;

    private float lastMatchingAttempt;

    // crash watchdog
    public float lastUpdate;

    private float debugStateCooldown = -1;

    void Start()
    {
        privateRooms = PrivateZoneRoot.GetComponentsInChildren<OccupantTracker>();
        Log($"Start AutoMatcher");
    }

    private void Update()
    {
        if (Networking.LocalPlayer == null) return;
        lastUpdate = Time.time;

        // if we haven't seen a matching yet
        // count down from the first time there were at least 2 people in the lobby
        // if we have seen matching, then count down (server time millis) from last seen by round time + between round time.
        // TODO this is weird and I think I can handle this better, but I'm sleepy. Need to wait less if zero players are matched.
        var timeSinceLobbyReady = Time.time - lobbyReadyTime;
        var timeSinceLastSeenMatching = ((float)Networking.GetServerTimeInMilliseconds() - (float)lastSeenMatchingServerTimeMillis) / 1000.0;
        var timeSinceLastMatchingAttempt = Time.time - lastMatchingAttempt;

        if (LobbyZone.occupancy > 1)
        {
            if (lobbyReady)
            {
                if (Networking.IsMaster)
                {
                    // very first match
                    if (lastSeenState0 == "" && timeSinceLobbyReady > TimeUntilFirstRound)
                    {
                        if (timeSinceLastMatchingAttempt > MatchingTrackerQuiescenceWait)
                        {
                            lastMatchingAttempt = Time.time;
                            Log($"initial countdown finished, trying first matching");
                            WriteMatching(LobbyZone.GetOccupants());
                        }
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

        // if we have done another matching before, wait the full time for the next round
        // TODO could somehow detect if everyone has left the private rooms and shorten, since there's
        // nobody to wait for
        if (Networking.IsMaster && lastSeenState0 != "" && timeSinceLastSeenMatching > (PrivateRoomTime + BetweenRoundTime))
        {
            if (timeSinceLastMatchingAttempt > MatchingTrackerQuiescenceWait)
            {
                lastMatchingAttempt = Time.time;
                Log($"trying another matching");
                WriteMatching(LobbyZone.GetOccupants());
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
        if ((debugStateCooldown -= Time.deltaTime) > 0) return;
        debugStateCooldown = 1f;
        int[] privateRoomOccupancy = new int[privateRooms.Length];
        for (int i = 0; i < privateRooms.Length; i++)
        {
            privateRoomOccupancy[i] = privateRooms[i].occupancy;
        }
        var countdown = lastSeenState0 == "" ?
            (LobbyZone.occupancy > 1 ? $"{TimeUntilFirstRound - timeSinceLobbyReady} seconds" : "(need players)") :
            $"{(PrivateRoomTime + BetweenRoundTime - timeSinceLastSeenMatching)} seconds";

        DebugStateText.text = $"{System.DateTime.Now} localPid={Networking.LocalPlayer.playerId} master?={Networking.IsMaster}\n" +
            $"countdown to next matching: {countdown}\n" +
            $"timeSinceLobbyReady={timeSinceLobbyReady} lobbyReady={lobbyReady}\n" +
            $"timeSinceLastSeenMatching={timeSinceLastSeenMatching} (wait {PrivateRoomTime + BetweenRoundTime} since last successful matching)\n" +
            $"timeSinceLastMatchingAttempt={timeSinceLastMatchingAttempt} (wait {MatchingTrackerQuiescenceWait} seconds after failed matching (on master))\n" +
            $"lobby.occupancy={LobbyZone.occupancy}\n" +
            $"lastSeenServerTimeMillis={lastSeenMatchingServerTimeMillis} millisSinceNow={Networking.GetServerTimeInMilliseconds() - lastSeenMatchingServerTimeMillis}\n" +
            $"lastSeenMatchCount={lastSeenMatchCount} lastSeenMatching={join(lastSeenMatching)}\n" +
            $"privateRoomOccupancy={join(privateRoomOccupancy)}";

        if (!MatchingTracker.started) return;
        var count = LobbyZone.occupancy;
        if (count < 2)
        {
            FullStateDisplay.text = "not enough players in lobby.";
            return;
        }

        VRCPlayerApi[] players = MatchingTracker.GetOrderedPlayers();
        var playerCount = players.Length;

        var global = MatchingTracker.ReadGlobalMatchingState(false);

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
        byte[] buf = DeserializeFrame(matchingState0, matchingState1);
        if (buf.Length < 5) return;
        int n = 0;
        int time = 0;
        time |= (int)buf[n++] << 24;
        time |= (int)buf[n++] << 16;
        time |= (int)buf[n++] << 8;
        time |= (int)buf[n++];
        lastSeenMatchingServerTimeMillis = time;
        int matchCount = buf[n++];
        lastSeenMatchCount = matchCount;

        int[] matching = new int[matchCount * 2];
        for (int i = 0; i < matchCount; i++)
        {
            int player1 = 0;
            player1 |= (int)buf[n++] << 8;
            player1 |= (int)buf[n++];
            matching[i * 2] = player1;

            int player2 = 0;
            player2 |= (int)buf[n++] << 8;
            player2 |= (int)buf[n++];
            matching[i * 2 + 1] = player2;
        }
        lastSeenMatching = matching;

        Log($"Deserialized new matching at {lastSeenMatchingServerTimeMillis}, with {matchCount}\n" +
            $"matchings: [{join(matching)}]");

        // check if local player is in a private room, in case they're interfering.
        foreach (var room in privateRooms)
        {
            if (room.localPlayerOccupying)
            {
                Log($"found player in private room {room}, teleporting out.");
                PrivateRoomTimer.TeleportOut();
            }
        }

        if (matchCount == 0) return; // nothing to do
        
        VRCPlayerApi[] players = MatchingTracker.GetOrderedPlayers();
        int myPlayerId = Networking.LocalPlayer.playerId;

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

                // record
                MatchingTracker.SetLocallyMatchedWith(otherPlayer, true);

                Vector3 adjust = matching[i * 2] == myPlayerId ? Vector3.forward : Vector3.back;
                // look at the center of the room
                Quaternion rotation = Quaternion.LookRotation(adjust * -1);
                Networking.LocalPlayer.TeleportTo(adjust + p.transform.position, rotation);
                PrivateRoomTimer.currentRoom = p;
                PrivateRoomTimer.StartCountdown(PrivateRoomTime);
                // teleport timer to location too as visual.
                PrivateRoomTimer.transform.position = p.transform.position;
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

    private void WriteMatching(VRCPlayerApi[] eligiblePlayers)
    {
        var global = MatchingTracker.ReadGlobalMatchingState(true);
        if (global == null)
        {
            Log($"player states are out of sync, can't calculate a stable matching.");
        }

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

        var matchingObject = CalculateMatching(eligiblePlayerIds, orderedPlayerIds, global, 80);

        int[] eligiblePlayerOrdinals = (int[])matchingObject[0];
        int[] matching = (int[])matchingObject[1];
        int matchCount = (int)matchingObject[2];

        SerializeMatching(eligiblePlayerOrdinals, matching, matchCount, players);
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        object[] CalculateMatching(int[] eligiblePlayerIds, int[] orderedPlayerIds, bool[] global, int globalDim)
    {
        var eligibleCount = eligiblePlayerIds.Length;
        Log($"{eligibleCount} players eligible for matching.");

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

        Log($"eligible player ordinals for matching: {join(eligiblePlayerOrdinals)}");

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
        Log($"matching ugraph:\n{mkugraph(ugraph, eligibleCount)}");

        // get closest even matching
        int[] matching = new int[(int)(eligibleCount / 2) * 2];
        int matchCount = GreedyRandomMatching(ugraph, eligibleCount, matching);

        Log($"calculated {matchCount} matchings: {join(matching)}");

        // such is udon
        return new object[] { eligiblePlayerOrdinals, matching, matchCount, originalUgraph };
    }

    // pick a random eligible pair until you can't anymore. not guaranteed to be maximal.
    // https://en.wikipedia.org/wiki/Blossom_algorithm is the maximal way to do this. soon(tm)
    public 
#if !COMPILER_UDONSHARP
        static
#endif
        int GreedyRandomMatching(bool[] ugraph, int count, int[] matching)
    {
        Log($"random matching {count} players on ugraph: {mkugraph(ugraph, count)}");
        int midx = 0;
        int[] matchable = new int[count];
        int matchableCount;
        while ((matchableCount = hasMatching(ugraph, count, matchable)) >= 1)
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
            for (int i = 0; i < count; i++)
            {
                // zero out columns
                ugraph[i * count + chosen2] = false;
                ugraph[i * count + chosen1] = false;
                // zero out rows
                ugraph[chosen1 * count + i] = false;
                ugraph[chosen2 * count + i] = false;
            }

            Log($"after matching {chosen1} and {chosen2}, ugraph: {mkugraph(ugraph, count)}");
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
        int hasMatching(bool[] ugraph, int count, int[] matchable)
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
        Log($"found {n} matchable players in {mkugraph(ugraph, count)}");
        return n;
    }
    private void SerializeMatching(int[] eligiblePlayerOrdinals, int[] matching, int matchCount,VRCPlayerApi[] players)
    {
        int n = 0;
        byte[] buf = new byte[maxDataByteSize];
        // this is actually some arbitrary value, not even necessarily positive, but it is
        // apparently consistent across the instance.
        var time = Networking.GetServerTimeInMilliseconds();
        buf[n++] = (byte)((time >> 24) & 0xFF);
        buf[n++] = (byte)((time >> 16) & 0xFF);
        buf[n++] = (byte)((time >> 8) & 0xFF);
        buf[n++] = (byte)(time & 0xFF);
        buf[n++] = (byte)matchCount;
        for (int i = 0; i < matchCount; i++)
        {
            // turn the matches into playerIds. If it can't fit into a char, we crash. oh well.
            char playerId1 = (char)players[eligiblePlayerOrdinals[matching[i * 2]]].playerId;
            buf[n++] = (byte)((playerId1 >> 8) & 0xFF);
            buf[n++] = (byte)(playerId1 & 0xFF);

            char playerId2 = (char)players[eligiblePlayerOrdinals[matching[i * 2 + 1]]].playerId;
            buf[n++] = (byte)((playerId2 >> 8) & 0xFF);
            buf[n++] = (byte)(playerId2 & 0xFF);
        }
        var frame = SerializeFrame(buf);
        matchingState0 = new string(frame, 0, maxSyncedStringSize);
        matchingState1 = new string(frame, maxSyncedStringSize, maxSyncedStringSize);
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        void Log(string text)
    {
        Debug.Log($"[MaximalMatching] [MatchingTracker] {text}");
#if COMPILER_UDONSHARP
        if (DebugLogText.text.Split('\n').Length > 30)
        {
            // trim
            DebugLogText.text = DebugLogText.text.Substring(DebugLogText.text.IndexOf('\n') + 1);
        }
        DebugLogText.text += $"{System.DateTime.Now}: {text}\n";
#endif
    }

    // from https://github.com/hiinaspace/just-mahjong/
    private const int maxPacketCharSize = maxSyncedStringSize * 2;

    private char[] SerializeFrame(byte[] buf)
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

    private const int maxDataByteSize = 182;

    private byte[] DeserializeFrame(string s0, string s1)
    {
        var frame = new char[maxPacketCharSize];
        s0.CopyTo(0, frame, 0, maxSyncedStringSize);
        s1.CopyTo(0, frame, s0.Length, maxSyncedStringSize);

        var packet = new byte[maxDataByteSize];
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
}
