using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tests
{
    public class AutoMatcherTest
    {
        private static bool O = true, _ = false;
        [Test]
        public void AutoMatcherTest3Players()
        {
            var ret = AutoMatcher.CalculateMatching(
                new int[] { 1, 2, 3 },
                new bool[] { O, _, _,
                             _, O, _,
                             _, _, O },
                3);

            int[] playerIdsByMatchingOrdinal = (int[])ret[0];
            int[] matching = (int[])ret[1];
            int matchCount = (int)ret[2];

            Assert.That(playerIdsByMatchingOrdinal, Is.EqualTo(new int[] { 1, 2, 3 }));
            Assert.That(matching, 
                Is.EqualTo(new int[] { 0, 1  })
                .Or.EqualTo(new int[] { 1, 2 })
                .Or.EqualTo(new int[] { 0, 2 })
                .Or.EqualTo(new int[] { 1, 0 })
                .Or.EqualTo(new int[] { 2, 1 })
                .Or.EqualTo(new int[] { 2, 0 })
                );
            Assert.That(matchCount, Is.EqualTo(1));
        }

        [Test]
        public void AutoMatcherTest2Players()
        {
            var ret = AutoMatcher.CalculateMatching(
                new int[] { 1, 2},
                new bool[] { O, _,
                             _, O },
                2);

            int[] playerIdsByMatchingOrdinal = (int[])ret[0];
            int[] matching = (int[])ret[1];
            int matchCount = (int)ret[2];

            Assert.That(playerIdsByMatchingOrdinal, Is.EqualTo(new int[] { 1, 2 }));
            Assert.That(matching, 
                Is.EqualTo(new int[] { 0, 1 })
                .Or.EqualTo(new int[] { 1, 0 })
                );
            Assert.That(matchCount, Is.EqualTo(1));
        }


        [Test]
        public void AutoMatcherTest2PlayersGap()
        {
            var ret = AutoMatcher.CalculateMatching(
                new int[] { 1, 0, 2 },
                // ordinal 1 is a gap, 1 and 2 should be matchable
                new bool[] { O, O, _ ,
                             O, O, O,
                             _, O, O},
                3);

            int[] playerIdsByMatchingOrdinal = (int[])ret[0];
            int[] matching = (int[])ret[1];
            int matchCount = (int)ret[2];
            string log = (string)ret[4];
            Debug.Log(log);

            Assert.That(playerIdsByMatchingOrdinal, Is.EqualTo(new int[] { 1, 2, 0 }));
            Assert.That(matchCount, Is.EqualTo(1));
            Assert.That(matching, 
                Is.EqualTo(new int[] { 0, 1 })
                .Or.EqualTo(new int[] { 1, 0 })
                );
            Assert.That(matchCount, Is.EqualTo(1));
        }

        [Test]
        public void AutoMatcherTestRandom()
        {
            for (int n = 0; n < 100; n++)
            {
                var playerIdsByGlobalOrdinal = new int[80];
                for (int i = 0; i < 80; i++)
                {
                    // simulate players not having sync objects yet
                    if (UnityEngine.Random.value > 0.9)
                    {
                        playerIdsByGlobalOrdinal[i] = i + 1;
                    }
                }
                // shuffle for differing gaps
                int swap;
                for (int i = 79; i >= 1; --i)
                {
                    var j = UnityEngine.Random.Range(0, i + 1); // range max is exclusive
                    swap = playerIdsByGlobalOrdinal[j];
                    playerIdsByGlobalOrdinal[j] = playerIdsByGlobalOrdinal[i];
                    playerIdsByGlobalOrdinal[i] = swap;
                }

                var global = new bool[80 * 80];
                // random matching state
                for (int i = 0; i < 80; i++)
                {
                    for (int j = 0; j < 80; j++)
                    {
                        global[i * 80 + j] = j == i ? true : UnityEngine.Random.value > 0.5;
                    }
                }

                var ret = AutoMatcher.CalculateMatching(
                    playerIdsByGlobalOrdinal,
                    global,
                    80);
                // just exercise, no good way to test yet
            }
        }

    }
}
