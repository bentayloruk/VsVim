﻿using System.Linq;
using NUnit.Framework;
using Vim;
using Vim.UnitTest;

namespace VimCore.UnitTest
{
    [TestFixture]
    public class CountCaptureTest
    {
        private CountResult.Complete Process(string input)
        {
            var res = CountCapture.Process(KeyInputUtil.CharToKeyInput(input[0]));
            foreach (var cur in input.Skip(1))
            {
                Assert.IsTrue(res.IsNeedMore);
                var i = KeyInputUtil.CharToKeyInput(cur);
                res = res.AsNeedMore().item.Invoke(i);
            }

            Assert.IsTrue(res.IsComplete);
            return (CountResult.Complete)res;
        }

        [Test]
        public void Simple1()
        {
            var res = Process("A");

            Assert.AreEqual(1, res.Item1);
            Assert.AreEqual(VimKey.UpperA, res.Item2.Key);
            Assert.AreEqual(KeyModifiers.None, res.Item2.KeyModifiers);
        }


        [Test]
        public void Simple2()
        {
            var res = Process("1A");
            Assert.AreEqual(1, res.Item1);
            Assert.AreEqual('A', res.Item2.Char);
        }

        [Test]
        public void Simple3()
        {
            var res = Process("23B");
            Assert.AreEqual(23, res.Item1);
            Assert.AreEqual('B', res.item2.Char);
        }





    }
}
