using Dumplings.Rpc;
using Dumplings.Scanning;
using NBitcoin;
using System.Linq;
using Xunit;

namespace Dumplings.Tests
{
    public class SamouraiCoinJoinDetectionTests
    {
        private const long Pool = 5_000_000; // The 0.05 BTC pool.
        private const ulong Height = 600_000; // Every pool before 0.001 BTC exists here.

        [Fact]
        public void AcceptsRoundWithRemix()
        {
            // Two remixes and three premixes, the shape SCDH describes.
            var tx = Tx(
                new[] { In(Pool), In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 6_050) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.True(Scanner.IsSamouraiCoinJoin(tx, Height));
        }

        [Fact]
        public void RejectsRoundWithoutRemix()
        {
            var tx = Tx(
                Enumerable.Repeat(Pool + 10_000, 5).Select(x => In(x)).ToArray(),
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height));
        }

        [Fact]
        public void RejectsInputAbovePremixMargin()
        {
            var tx = Tx(
                new[] { In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 110_001) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height));
        }

        [Fact]
        public void RejectsUnequalInputAndOutputCount()
        {
            var tx = Tx(
                new[] { In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height));
        }

        private static VerboseTransactionInfo Tx(VerboseInputInfo[] inputs, VerboseOutputInfo[] outputs)
            => new(null, RandomUtils.GetUInt256(), inputs, outputs);

        private static VerboseInputInfo In(long value)
            => new(new OutPoint(RandomUtils.GetUInt256(), 0), Out(value), uint.MaxValue);

        private static VerboseOutputInfo Out(long value)
            => new(Money.Satoshis(value), new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));
    }
}
