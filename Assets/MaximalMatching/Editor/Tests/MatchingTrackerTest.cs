using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tests
{
    public class MatchingTrackerTest
    {
        [Test]
        public void stringHashMap()
        {
            // exercise with random values
            for (int i = 0; i < 100; ++i)
            {
                string[] keys = new string[320];
                bool[] values = new bool[320];
                float[] times = new float[320];
                var realMap = new Dictionary<string, bool>();
                for (int j = 0; j < 80; j++)
                {
                    var key = RandomString(10);
                    realMap.Add(key, true);
                    MatchingTracker.set(key, true, keys, values, times);
                }
                foreach (var thing in realMap)
                {
                    Assert.That(MatchingTracker.lookup(thing.Key, keys, values), Is.True);
                }
            }
        }

        // thank you stackoverflow
        private static System.Random rand = new System.Random();
        public static string RandomString(int length)
        {
            var chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var stringChars = new char[8];
            var random = new Random();

            for (int i = 0; i < stringChars.Length; i++)
            {
                stringChars[i] = chars[rand.Next(chars.Length)];
            }

            return new string(stringChars);
        }
    }
}
