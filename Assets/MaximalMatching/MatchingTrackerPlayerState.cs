
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
        // XXX networking owner will be null for the first few frames.
        return (networkingOwner != null && networkingOwner.playerId == ownerId) ? networkingOwner : null;
    }

    public void TakeExplicitOwnership()
    {
        var player = Networking.LocalPlayer;
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        ownerId = player.playerId;
    }

    // 7bit encoding of:
    // 1 byte bool `matchingEnabled
    // 80 x 10-bit player ids that have been matched with this object's owner,
    //    "0 terminated" in that once you get a 0 player id (invalid) you can stop reading.
    // XXX: weird udon behavior: if you change the name of this variable from `matchingState` to 
    // just `state`, then udon will fail to serialize this with some sort of outOfBounds exception.
    // so, uh, don't change the variable name.
    [UdonSynced] public string matchingState = "";

    // cache local view of state for efficiency

    // whether this gameobject's owner has matching enabled locally
    public bool matchingEnabled = false;

    // ids of players the owner of this state has been matched with before.
    // XXX is a 80-element array that's "0 terminated" (player id 0 never happens) for
    // some clarity in the serialization procedures.
    public int[] matchedPlayerIds = new int[80];

    // quick access if the owner of this state has been matched with Networking.LocalPlayer
    public bool matchedWithLocalPlayer;

    public float lastDeserialization;

    public void SerializeLocalState(int[] newMatchedPlayerIds, int matchCount, bool newMatchingEnabled)
    {
        matchedPlayerIds = newMatchedPlayerIds;
        // we are the local player
        matchedWithLocalPlayer = false;
        matchingEnabled = newMatchingEnabled;
        byte[] buf = serializeBytes(matchCount, matchedPlayerIds, newMatchingEnabled);
        matchingState = new string(SerializeFrame(buf));
    }

    // called when udon sets the UdonSynced variables from the network.
    public override void OnDeserialization()
    {
        if (matchingState.Length == 0) return;
        lastDeserialization = Time.time;

        //Debug.Log($"OnDeserialization fired for {gameObject.name}");
        byte[] playerList = DeserializeFrame(matchingState);
        int[] newMatchedPlayerIds = new int[80];
        matchingEnabled = deserializeBytes(playerList, newMatchedPlayerIds);
        var localId = Networking.LocalPlayer.playerId;
        matchedPlayerIds = newMatchedPlayerIds;

        matchedWithLocalPlayer = false;
        foreach (var id in matchedPlayerIds)
        {
            if (id == 0) break;
            if (id == localId) matchedWithLocalPlayer = true;
        }
    }

    // 80 * 10 bits player ids. zeroes at the end will encode to zeros and signal end of list.
    // need to fit at 100 bytes. 105 bytes * 8 / 7 = 120 chars.
    private const int maxPacketCharSize = 120;
    private const int maxDataByteSize = 105;

    // XXX could go from 10bit numbers to 7bit chars in a single method. was
    // lazy and ended up splitting them like this.

    // deserialize into `outMatchedPlayers` and return whether matching enabled.
    // XXX someday udonsharp will have out params.
    public 
#if !COMPILER_UDONSHARP
        static
#endif
        bool deserializeBytes(byte[] bytes, int[] outMatchedPlayers)
    {
        bool matchingEnabled = bytes[0] > 0;
        // 10 bits per player
        int[] matchedPlayers = outMatchedPlayers;
        // deserialize 5 bytes int 4 player ids
        for (int j = 1, k = 0; k < 80; j += 5, k += 4)
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
        return matchingEnabled;
    }

    public 
#if !COMPILER_UDONSHARP
        static
#endif
        byte[] serializeBytes(int matchCount, int[] matchedPlayerIds, bool matchingEnabled)
    {
        byte[] buf = new byte[maxDataByteSize];
        buf[0] = matchingEnabled ? (byte)1 : (byte)0;
        // serialize 4 10bit player ids into 5 bytes
        // TODO could go from 7 10bit player ids into 10 chars directly.
        for (int j = 0, k = 1; j < matchCount; j += 4, k += 5)
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
}
