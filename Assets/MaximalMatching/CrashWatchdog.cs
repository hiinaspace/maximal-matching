﻿
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;


// visual indication if one of the other scripts stops running update()
// presumably because of a crash
public class CrashWatchdog : UdonSharpBehaviour
{
    public GameObject visualOnCrash;
    public AutoMatcher AutoMatcher;
    public MatchingTracker MatchingTracker;

    void Update()
    {
        visualOnCrash.SetActive(Time.time - AutoMatcher.lastUpdate > 5 || Time.time - MatchingTracker.lastUpdate > 5);
    }
}
