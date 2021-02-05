using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tests
{
    public class OccupantTrackerTest
    {
        [Test]
        public void intHashMap()
        {
            // exercise with random values
            for (int i = 0; i < 100; ++i)
            {
                int[] keys = new int[320];
                bool[] values = new bool[320];
                var realMap = new Dictionary<int, bool>();
                for (int j = 0; j < 80; j++)
                {
                    int key = Random.Range(0, 80);
                    realMap[key] = true;
                    OccupantTracker.set(key, true, keys, values);
                }
                foreach (var thing in realMap)
                {
                    Assert.That(OccupantTracker.lookup(thing.Key, keys, values), Is.True);
                }
                for (int j = 0; j < 80; j++)
                {
                    int key = Random.Range(0, 80);
                    realMap.Remove(key);
                    OccupantTracker.remove(key, keys, values);
                }
                foreach (var thing in realMap)
                {
                    Assert.That(OccupantTracker.lookup(thing.Key, keys, values), Is.True);
                }
            }
        }
    }
}
