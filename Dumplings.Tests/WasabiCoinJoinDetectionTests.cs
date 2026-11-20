using Dumplings.Rpc;
using Dumplings.Scanning;
using NBitcoin;
using System;
using System.Linq;
using Xunit;

namespace Dumplings.Tests
{
    public class WasabiCoinJoinDetectionTests
    {
        private const ulong Height = 700_000; // After Constants.FirstWasabiNoCoordAddressBlock: only the shape identifies a round.
        private static readonly long V = Money.Coins(0.1m).Satoshi; // Wasabi 1 base denomination.

        private static readonly Func<uint256, bool> NoKnownCoinJoins = _ => false;

        [Fact]
        public void LeavesJoinMarketShapedCoinJoinToOther()
        {
            // Taker and nine makers at 0.1 BTC, one change each: the Wasabi 1 shape, but exactly the JoinMarket one.
            var tx = Tx(Participants(10), Changes(10));

            Assert.True(Scanner.IsOtherCoinJoin(tx, NoKnownCoinJoins));
            Assert.False(Scanner.IsWasabiCoinJoin(tx, Height, isNativeSegwitOnly: true, NoKnownCoinJoins));
        }

        [Fact]
        public void KeepsRoundWithChangeForEveryParticipantAndCoordinatorOutput()
        {
            // The same participants plus a coordinator output: n equal outputs, n + 1 unique ones.
            var tx = Tx(Participants(10), Changes(10).Append(Out(31_000)).ToArray());

            Assert.False(Scanner.IsOtherCoinJoin(tx, NoKnownCoinJoins));
            Assert.True(Scanner.IsWasabiCoinJoin(tx, Height, isNativeSegwitOnly: true, NoKnownCoinJoins));
        }

        [Fact]
        public void KeepsRoundWithTwoMixingLevels()
        {
            // Three participants also mix at the 0.2 BTC level, so there are two groups of equal outputs.
            var outputs = Changes(9)
                .Append(Out(31_000))
                .Concat(Enumerable.Range(0, 3).Select(_ => Out(2 * V)))
                .ToArray();
            var tx = Tx(Participants(10), outputs);

            Assert.False(Scanner.IsOtherCoinJoin(tx, NoKnownCoinJoins));
            Assert.True(Scanner.IsWasabiCoinJoin(tx, Height, isNativeSegwitOnly: true, NoKnownCoinJoins));
        }

        /// <summary>
        /// Inputs of <paramref name="count"/> participants. Participant i puts in a base denomination, its change of
        /// <see cref="Changes"/> and 2,000 sat for fees.
        /// </summary>
        private static VerboseInputInfo[] Participants(int count)
            => Enumerable.Range(0, count).Select(i => In(V + Change(i) + 2_000)).ToArray();

        /// <summary>
        /// The equal outputs of 10 participants and the first <paramref name="count"/> of their distinct changes.
        /// </summary>
        private static VerboseOutputInfo[] Changes(int count)
            => Enumerable.Range(0, 10).Select(_ => Out(V))
                .Concat(Enumerable.Range(0, count).Select(i => Out(Change(i))))
                .ToArray();

        private static long Change(int i) => 1_000_000 + (i * 137_000);

        private static VerboseTransactionInfo Tx(VerboseInputInfo[] inputs, VerboseOutputInfo[] outputs)
            => new(null, RandomUtils.GetUInt256(), inputs, outputs);

        private static VerboseInputInfo In(long value)
            => new(new OutPoint(RandomUtils.GetUInt256(), 0), Out(value), uint.MaxValue);

        private static VerboseOutputInfo Out(long value)
            => new(Money.Satoshis(value), new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));
    }
}
