# udon maximal matching

TODO
add remote matched with to player UI
cache global mapping bool[] by ownership order (for display)

add 'enable matching' bool to player state and UI and check in automatcher
add 'start first matching' button for master


## Design

This project implements a system for automatically matching (a.k.a. pairing)
two players in a vrchat world instance that haven't previously been matched
together and teleporting both players to a private zone in the world, so that they
can have a 1-on-1 conversation, i.e. speed dating.

The system is relatively robust to player turnover (leaves/joins) and should work
for a full 80 person instance.

### MatchingTracker

The MatchingTracker prefab lets each player in the world instance keep track of
whether they've been matched/paired with each other player in the world, where
'matched' is an arbitrary external function of the two players. Each player
additionally broadcasts their matching through a synced Udon Behavior variable.
Thus, the full directed graph of player vertices and 'has been matched before'
edges is available to other UdonBehaviors, e.g. for display, or automatic matching.

The MatchingTracker behavior keeps track of the matching state locally with a
hash map from player `displayName` to the boolean match state, with public
functions to mark a pairing as matched, or to clear the local state if desired.
Keeping track of matching state by `displayName` makes the tracking robust
to players leaving (or crashing) and rejoining later.

To broadcast the local matching state, the prefab uses 80 gameobjects each with
an UdonBehavior with a synced `matchingState` string and a synced `ownerPlayer`
int32. 

The `ownerPlayer` int32 disambiguates implicitly master-owned GameObjects from
explicitly owned GameObjects. A gameobject will be owned by the instance master
upon initialization or if the previous owner leaves the instance.  When
explicitly taking ownership, a player both calls
`Networking.SetOwner(gameObject)` and sets the `ownerPlayer =
Networking.LocalPlayer.playerId`, and other behaviors check that
`Networking.GetOwner(gameObject).playerId == ownerPlayer` holds. If
`ownerPlayer` doesn't match the Networking owner, then either no player ever
explicitly took ownership, or the owning player left the instance.

The `matchingState` string is a base64-encoded bitmap of whether the owning
player of the gameObject has been matched with the (up to) 79 other players in
the instance in ascending order by playerId. Udon assigns a unique
monotonically increasing int32 player to players as they join the instance,
with the smaller ids being effectively discarded as players leave. Using the
ordinal of the player id rather than the absolute 32-bit value fits the matching
state into 79 bits, instead of 79 * 4 = 316 bytes, which is too big to sync
in a single UdonBehavior.

Upon joining the instance, a player iterates through the 80 gameObjects to find
an unowned (including `ownerPlayer` check) gameObject and attempts to take
ownership, with a random backoff in case of a race between more than one player
joining in short succession. The local matching state hashmap is then
serialized to the ordinal bitmap periodically, including when players leaving
the map shift the playerId ordinals of the bitmap.

To work around an Udon limitation where the logs are spammed with 'Death run
detected, dropping events' messages if there are more than about ~20 active
gameObjects with UdonBehaviors with synced variables active in the scene, the
parent MatchingTracker behavior cycles through the 80 gameObjects such that
only ~20 are actually active in the scene in any given frame. Udon will still
eventually sync the data by picking up the active gameObjects.

The MatchingTrackerUi prefab displays the full player matching graph as a
canvas UI, for debugging or as a checklist, as well as functions for a player
to locally clear their matching state if desired.

### AutoMatcher

The AutoMatcher prefab implements the automatic part of the system.

The behavior waits for players to enter the 'Lobby' collider, and begins a
short countdown once there are enough players to match. Once the countdown
finishes, the prefab calculates the [Maximal Cardinality Matching][0] (or at
least a greedy matching) from the global MatchingTracker state (filtered to
players currently within the Lobby), and broadcasts that matching as a synced
string. The behavior then teleports the local player into a currently
unoccupied 'private zone' according to the matching, and marks each player as
having been matched with each other. The PrivateRoomTimer behavior then
teleports the players back to spawn after a delay.

Players outside the lobby will not be automatically matched/teleported, so they
can take breaks from the matching if afk or otherwise. Additionally, players
can wait in the lobby for other unmatched players to enter without needing to
coordinate entry times.

Since the MatchingTracker tracks each player's local perception of whether they
have been matched with other players by displayName, the "has been matched"
graph is directed. The AutoMatcher will only match players who both locally
indicate they haven't been matched with each other. That way, a player who
rejoins the instance with a clean slate will still avoid being matched with
players they were matched with before (in the same instance), because those
players will remember having been matched. This works whether the player had to
rejoin unintentionally (e.g. because of a crash) or are purposefully trying to
get rematched with people by continually rejoining. If both players agree they
want to be rematched they can both clear their matching state locally.

[0]: https://en.wikipedia.org/wiki/Maximum_cardinality_matching

### OccupancyTracker

A simple behavior to track player occupancy using a hash set and vrchat's
player trigger enter/leave callbacks, used by the AutoMatcher for lobby and
room occupancy.

## Prereqs

You'll need VRCSDK3 and UdonSharp in your project.
