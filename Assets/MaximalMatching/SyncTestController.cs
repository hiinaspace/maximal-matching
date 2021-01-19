
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class SyncTestController : UdonSharpBehaviour
{
    public UnityEngine.UI.Text debug;

    private SyncTestItem[] items;
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
    // for higher latency but more overhead before the messages spam.
    private int enabledCursor = 0, maxEnabled = 20;
    void Start()
    {
        items = GetComponentsInChildren<SyncTestItem>(includeInactive: true);
    }

    private void Update()
    {
        var s = $"{items.Length} items, owner? {Networking.IsOwner(gameObject)} last update: {System.DateTime.Now}, A {enabledCursor}\n";
        var i = 0;
        foreach (var item in items)
        {
            s += $"[{i++}{(item.gameObject.activeSelf ? "A" : "D")}] {item.syncString}, ";
        }
        debug.text = s;
        if (Networking.IsOwner(gameObject))
        {
            items[enabledCursor].gameObject.SetActive(false);
            items[(enabledCursor + maxEnabled) % items.Length].gameObject.SetActive(true);
            enabledCursor = (enabledCursor + 1) % items.Length;
        } else
        {
            foreach (var item in items)
            {
                item.gameObject.SetActive(true);
            }
        }
    }

    public void ChangeItems()
    {
        int n = UnityEngine.Random.Range(0, 10);
        foreach (var item in items)
        {
            item.syncString = $"012345789012345789012345789012345789 {n}";
            n++;
        }
    }
}
