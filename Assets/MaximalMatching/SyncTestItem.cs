
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

public class SyncTestItem : UdonSharpBehaviour
{
    [UdonSynced] public string syncString;

    void Start()
    {
        syncString = gameObject.name;
    }
}
