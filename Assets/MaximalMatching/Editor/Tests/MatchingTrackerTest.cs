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
            int LOCAL_STATE_SIZE = 2048;
            // exercise with random values
            for (int i = 0; i < 100; ++i)
            {
                string[] keys = new string[LOCAL_STATE_SIZE];
                bool[] values = new bool[LOCAL_STATE_SIZE];
                float[] times = new float[LOCAL_STATE_SIZE];
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


        [Test]
        public void PlayersToBytes()
        {
            for (int i = 0; i < 80; i++)
            {
                int[] playerIds = new int[80];
                for (int j = 0; j < i; j++)
                {
                    playerIds[j] = UnityEngine.Random.Range(1, 1023);
                }

                //Debug.Log($"playerIds: {string.Join(",", playerIds)}");
                var bytes = MatchingTrackerPlayerState.serializeBytes(i, playerIds);
                var frame = MatchingTrackerPlayerState.SerializeFrame(bytes);
                var deframe = MatchingTrackerPlayerState.DeserializeFrame(new string(frame));
                Assert.That(deframe, Is.EqualTo(bytes));
                var deser = MatchingTrackerPlayerState.deserializeBytes(deframe);
                Assert.That(deser, Is.EqualTo(playerIds));
            }

        }


    }
}
