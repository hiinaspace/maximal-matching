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
                new int[] { 1, 2, 3 },
                new bool[] { O, _, _,
                             _, O, _,
                             _, _, O },
                3);

            int[] eligiblePlayerOrdinals = (int[])ret[0];
            int[] matching = (int[])ret[1];
            int matchCount = (int)ret[2];

            Assert.That(eligiblePlayerOrdinals, Is.EqualTo(new int[] { 0, 1, 2 }));
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
                new int[] { 1, 2},
                new bool[] { O, _,
                             _, O },
                2);

            int[] eligiblePlayerOrdinals = (int[])ret[0];
            int[] matching = (int[])ret[1];
            int matchCount = (int)ret[2];

            Assert.That(eligiblePlayerOrdinals, Is.EqualTo(new int[] { 0, 1 }));
            Assert.That(matching, 
                Is.EqualTo(new int[] { 0, 1 })
                .Or.EqualTo(new int[] { 1, 0 })
                );
            Assert.That(matchCount, Is.EqualTo(1));
        }

    }
}
