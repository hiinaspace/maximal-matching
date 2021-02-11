
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class AutoMatcher : UdonSharpBehaviour
{
    public UnityEngine.UI.Text DebugLogText;
    public UnityEngine.UI.Text DebugStateText;
    public UnityEngine.UI.Text FullStateDisplay;
    public UnityEngine.UI.Text CountdownText;
    public GameObject StartMatchingButton;

    public MatchingTracker MatchingTracker;
    public PrivateRoomTimer PrivateRoomTimer;
    public GameObject PrivateZoneRoot;

    // how long between each matching 
    public int MatchingDurationSeconds = 30;

    // whether matching is calculation is enabled. instance master toggles this on once.
    public bool matchingEnabled = false;

    // base64 serialized:
    // 2 byte matching duration in seconds
    // 4 byte serverTimeMillis of last matching (for checking countdowns)
    //     
    // 1 byte number of matches (up to 40)
    // ([2 byte player id] [2 byte player id]) per matching
    //
    // since we're only using two bytes, this serialization will break after 65535
    // player enter the same instance. this is probably fine for vrchat.
    //
    // base64 encoding of 210 chars is only ~157 bytes, and we need 169 bytes, so we
    // have to use 7bit char encoding.
    private const int maxSyncedStringSize = 105;
    [UdonSynced] public string matchingState0 = "";
    [UdonSynced] public string matchingState1 = "";
    private string lastSeenState0 = "";
    private int lastSeenMatchingServerTimeMillis = int.MinValue;
    private int[] lastSeenMatching = new int[0];
    private int lastSeenMatchCount = 0;

    private Transform[] privateRooms;

    // crash watchdog
    public float lastUpdate;

    private float debugStateCooldown = -1;

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
    }

    public void StartMatching()
    {
        if (Networking.IsOwner(gameObject))
        {
            matchingEnabled = true;
        }
    }

    private void Update()
    {
        if (Networking.LocalPlayer == null) return;
        lastUpdate = Time.time;

        // for the purposes of this gameobject. Should be the same as Networking.IsMaster, but just in case.
        bool isMaster = Networking.IsOwner(gameObject);

        // only show button if necessary
        StartMatchingButton.SetActive(!matchingEnabled && isMaster);

        int secondsSinceLastMatching = (Networking.GetServerTimeInMilliseconds() - lastSeenMatchingServerTimeMillis) / 1000;
        // if we're good to match, and time limit is up or we're doing the very first matching
        if (isMaster && matchingEnabled && 
            (secondsSinceLastMatching > MatchingDurationSeconds || lastSeenMatchingServerTimeMillis == int.MinValue))
        {
            Log($"ready for new matching");
            WriteMatching();
        }

        if (matchingState0 != lastSeenState0)
        {
            // got a new matching
            // note this also runs on the master on the frame the new matching is written, updating the `seen` variables.
            lastSeenState0 = matchingState0;
            ActOnMatching();
        }

        UpdateCountdownDisplay(secondsSinceLastMatching);
        DebugState(secondsSinceLastMatching);
    }

    private void UpdateCountdownDisplay(int secondsSinceLastMatching)
    {
        if (lastSeenState0 == "")
        {
            var master = Networking.GetOwner(gameObject);
            CountdownText.text = $"Waiting for instance master ({(master == null ? "unknown" : master.displayName)}) to start";
        }
        else
        {
            int seconds = MatchingDurationSeconds - secondsSinceLastMatching;
            int minutes = seconds / 60;
            CountdownText.text =
                $"Next matching in {minutes:00}:{seconds:00}";
        }
    }

    private void DebugState(int secondsSinceLastMatching)
    {
        // skip update if debug text is off
        if (!DebugLogText.gameObject.activeInHierarchy) return;

        if ((debugStateCooldown -= Time.deltaTime) > 0) return;
        debugStateCooldown = 1f;

        var master = Networking.GetOwner(gameObject);

        int seconds = MatchingDurationSeconds - secondsSinceLastMatching;
        int minutes = seconds / 60;
        var countdown = lastSeenState0 == "" ?
            $"Waiting for instance master ({(master == null ? "unknown" : master.displayName)}) to start" :
            $"Next matching in {minutes:00}:{seconds:00}";

        DebugStateText.text = $"{System.DateTime.Now} localPid={Networking.LocalPlayer.playerId} master?={Networking.IsMaster}\n" +
            $"countdown: {countdown}\n" +
            $"timeSinceLastSeenMatching={secondsSinceLastMatching}\n" +
            $"lastSeenServerTimeMillis={lastSeenMatchingServerTimeMillis} millisSinceNow={Networking.GetServerTimeInMilliseconds() - lastSeenMatchingServerTimeMillis}\n" +
            $"lastSeenMatchCount={lastSeenMatchCount} lastSeenMatching={join(lastSeenMatching)}\n";

        if (!MatchingTracker.started) return;

        VRCPlayerApi[] players = new VRCPlayerApi[80];
        var global = MatchingTracker.ReadGlobalMatchingState(players);

        int[] playerIdsbyGlobalOrdinal = new int[players.Length];
        int eligibleCount = 0;
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null)
            {
                playerIdsbyGlobalOrdinal[i] = players[i].playerId;
                eligibleCount++;
            }
        }

        if (eligibleCount < 2)
        {
            FullStateDisplay.text = "not enough initialized/matchable players for a matching yet.";
        }

        var matchingObject = CalculateMatching(playerIdsbyGlobalOrdinal, global, 80);
        int[] playerIdsByMatchingOrdinal = (int[])matchingObject[0];
        int[] matching = (int[])matchingObject[1];
        int matchCount = (int)matchingObject[2];
        bool[] originalUgraph = (bool[])matchingObject[3];
        var s = $"current potential matching:\n";
        s += $"playerIdsByMatchingOrdinal={join(playerIdsByMatchingOrdinal)}\n";
        s += $"matchCount={matchCount}\n";
        s += $"matching={join(matching)}\n";
        s += $"originalUgraph:\n\n";
        string[] names = new string[80];

        for (int i = 0; i < eligibleCount; i++)
        {
            var id = playerIdsByMatchingOrdinal[i];
            var player = VRCPlayerApi.GetPlayerById(id);
            // should always be non null
            if (player != null)
            {
                names[i] = player.displayName.PadRight(15).Substring(0, 15);
                s += $"{player.displayName.PadLeft(15).Substring(0, 15)} ";
            }
            else
            {
                names[i] = "                "; // 16 spaces
                s += "                "; // 16 spaces
            }

            for (int j = 0; j < i; j++) s += " ";

            for (int j = i + 1; j < eligibleCount; j++)
            {
                s += originalUgraph[i * eligibleCount + j] ? "O" : ".";
            }
            s += "\n";
        }
        for (int i = 0; i < 15; i++)
        {
            s += "\n                "; // 16 spaces
            for (int j = 0; j < eligibleCount; j++)
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

        int newMatchingDuration = 0;
        newMatchingDuration |= (int)buf[n++] << 8;
        newMatchingDuration |= (int)buf[n++];

        MatchingDurationSeconds = newMatchingDuration;

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

        VRCPlayerApi[] players = MatchingTracker.GetActivePlayers();
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
                // avoid lerping (apparently on by default)
                Networking.LocalPlayer.TeleportTo(adjust + p.transform.position, rotation,
                    VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint, lerpOnRemote: false);

                // teleport timer to location as visual
                PrivateRoomTimer.StartCountdown((float)MatchingDurationSeconds);
                PrivateRoomTimer.transform.position = p.transform.position;
                return;
            }
        }

        Log($"Local player id={myPlayerId} was not in the matching, teleporting out if they were in a room previously");
        // if the player was previously in a room, the privateroomtimer is there with them and will teleport them out.
        PrivateRoomTimer.TeleportOut();
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

    private void WriteMatching()
    {
        VRCPlayerApi[] players = new VRCPlayerApi[80];
        var global = MatchingTracker.ReadGlobalMatchingState(players);

        // XXX this can have gaps in it since not all players necessarily own a sync object yet
        int[] playerIdsByGlobalOrdinal = new int[80];
        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] != null) playerIdsByGlobalOrdinal[i] = players[i].playerId;
        }

        var matchingObject = CalculateMatching(playerIdsByGlobalOrdinal, global, 80);

        int[] playerIdsByMatchingOrdinal = (int[])matchingObject[0];
        int[] matching = (int[])matchingObject[1];
        int matchCount = (int)matchingObject[2];
        // globalugraph = [3]
        string log = (string)matchingObject[4];
        Log(log);

        SerializeState(playerIdsByMatchingOrdinal, matching, matchCount);
    }

    public
#if !COMPILER_UDONSHARP
        static
#endif
        object[] CalculateMatching(int[] playerIdsByGlobalOrdinal, bool[] global, int globalDim)
    {
        // stick logs in local variable instead of spamming them in the console.
        string[] log = new string[] { "" };

        var eligibleCount = 0;
        // the matchings returned by this function are indexed into the smaller ugraph array rather than
        // the full 80x80 array. XXX this is kind of gross, could improve but don't want to change too much.
        int[] playerIdsByMatchingOrdinal = new int[playerIdsByGlobalOrdinal.Length];
        // sentinel add one
        int[] globalOrdinalByMatchingOrdinal = new int[playerIdsByGlobalOrdinal.Length];
        for (int i = 0; i < playerIdsByGlobalOrdinal.Length; i++)
        {
            var pid = playerIdsByGlobalOrdinal[i];
            // only pids above 0 are valid
            if (pid > 0)
            {
                var matchingOrdinal = eligibleCount++;
                globalOrdinalByMatchingOrdinal[matchingOrdinal] = i;
                playerIdsByMatchingOrdinal[matchingOrdinal] = pid;
            }
        }
        log[0] += $"{eligibleCount} players eligible for matching. ";

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
            // XXX reverse sentinel mapping
            int p1 = globalOrdinalByMatchingOrdinal[i];
            // only need top triangle of the matrix
            for (int j = i + 1; j < eligibleCount; j++)
            {
                // XXX reverse sentinel mapping
                int p2 = globalOrdinalByMatchingOrdinal[j];
                // small graph is eligible for match
                ugraph[i * eligibleCount + j] =
                    // if both player says they haven't been matched
                    !global[p1 * globalDim + p2] && !global[p2 * globalDim + p1];

                // for debugging
                originalUgraph[i * eligibleCount + j] = ugraph[i * eligibleCount + j];
            }
        }
        log[0] += ($"matching ugraph:\n{mkugraph(ugraph, eligibleCount)}. ");

        // get closest even matching
        int[] matching = new int[(int)(eligibleCount / 2) * 2];
        int matchCount = GreedyRandomMatching(ugraph, eligibleCount, matching, log);

        log[0] += ($"calculated {matchCount} matchings: {join(matching)}.");

        // such is udon
        return new object[] { playerIdsByMatchingOrdinal, matching, matchCount, originalUgraph, log[0]};
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
    private void SerializeState(int[] playerIdsByMatchingOrdinal, int[] matching, int matchCount)
    {
        int n = 0;
        byte[] buf = new byte[maxDataByteSize];

        buf[n++] = (byte)((MatchingDurationSeconds >> 8) & 0xFF);
        buf[n++] = (byte)(MatchingDurationSeconds & 0xFF);

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
            // turn the matches into playerIds. If it can't fit into 16 bits, we crash. oh well.
            char playerId1 = (char)playerIdsByMatchingOrdinal[matching[i * 2]];
            buf[n++] = (byte)((playerId1 >> 8) & 0xFF);
            buf[n++] = (byte)(playerId1 & 0xFF);

            char playerId2 = (char)playerIdsByMatchingOrdinal[matching[i * 2 + 1]];
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
