using Dumplings.Rpc;
using Dumplings.Scanning;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dumplings.Tests
{
    public class WasabiCoinJoinDetectionTests
    {
        private const ulong Height = 700_000; // After Constants.FirstWasabiNoCoordAddressBlock: the shape and the remixes identify a round.
        private const int ParticipantCount = 10;
        private const long Fees = 5_000; // Paid by every participant: 2,000 sat to the miners and 3,000 sat to the coordinator.
        private const long CoordinatorFee = ParticipantCount * 3_000;
        private static readonly long V = Money.Coins(0.1m).Satoshi; // Wasabi 1 base denomination.

        private static readonly uint256 RoundA = RandomUtils.GetUInt256();
        private static readonly uint256 RoundB = RandomUtils.GetUInt256();
        private static readonly Func<uint256, bool> KnownRounds = new HashSet<uint256> { RoundA, RoundB }.Contains;
        private static readonly Func<uint256, bool> NoKnownCoinJoins = _ => false;

        [Fact]
        public void KeepsRoundThatRemixes()
        {
            // Every participant has a change and the coordinator takes its output; two participants remix earlier rounds.
            var tx = Tx(Inputs(withChange: 10, RoundA, RoundB), Outputs(changes: 10, coordinator: true));

            Assert.False(Scanner.IsOtherCoinJoin(tx, NoKnownCoinJoins));
            Assert.True(Scanner.IsWasabiCoinJoin(tx, Height, isNativeSegwitOnly: true, KnownRounds));
        }

        [Fact]
        public void KeepsRemixingRoundOfJoinMarketShape()
        {
            // One participant has no change, so with the coordinator output there are as many other outputs as equal ones:
            // exactly the JoinMarket shape.
            var tx = Tx(Inputs(withChange: 9, RoundA, RoundB), Outputs(changes: 9, coordinator: true));

            Assert.True(Scanner.IsOtherCoinJoin(tx, NoKnownCoinJoins));
            Assert.True(Scanner.IsWasabiCoinJoin(tx, Height, isNativeSegwitOnly: true, KnownRounds));
        }

        [Fact]
        public void CountsInputsFromTheSameEarlierRound()
        {
            var tx = Tx(Inputs(withChange: 10, RoundA, RoundA), Outputs(changes: 10, coordinator: true));

            Assert.True(Scanner.IsWasabiCoinJoin(tx, Height, isNativeSegwitOnly: true, KnownRounds));
        }

        [Fact]
        public void LeavesJoinMarketCoinJoinToOther()
        {
            // Taker and nine makers at 0.1 BTC, one change each: the Wasabi 1 shape, but no coin of the Wasabi 1 pool.
            var tx = Tx(Inputs(withChange: 10), Outputs(changes: 10, coordinator: false));

            Assert.True(Scanner.IsOtherCoinJoin(tx, NoKnownCoinJoins));
            Assert.False(Scanner.IsWasabiCoinJoin(tx, Height, isNativeSegwitOnly: true, KnownRounds));
        }

        [Fact]
        public void LeavesJoinMarketCoinJoinWithOneWasabiCoinToOther()
        {
            // A maker brings one coin of a Wasabi 1 round; a single such input does not make a round.
            var tx = Tx(Inputs(withChange: 10, RoundA), Outputs(changes: 10, coordinator: false));

            Assert.True(Scanner.IsOtherCoinJoin(tx, NoKnownCoinJoins));
            Assert.False(Scanner.IsWasabiCoinJoin(tx, Height, isNativeSegwitOnly: true, KnownRounds));
        }

        [Fact]
        public void RejectsRoundShapeWithoutRemix()
        {
            // The outputs of a round, but no input from the Wasabi 1 pool.
            var tx = Tx(Inputs(withChange: 10), Outputs(changes: 10, coordinator: true));

            Assert.False(Scanner.IsOtherCoinJoin(tx, NoKnownCoinJoins));
            Assert.False(Scanner.IsWasabiCoinJoin(tx, Height, isNativeSegwitOnly: true, KnownRounds));
        }

        [Fact]
        public void KeepsRoundWithCoordinatorAddressWithoutRemix()
        {
            // Before Constants.FirstWasabiNoCoordAddressBlock the coordinator address identifies a round. The first rounds
            // have nothing to remix; found this way, they are what later rounds remix.
            var outputs = Outputs(changes: 10, coordinator: false)
                .Append(new VerboseOutputInfo(Money.Satoshis(CoordinatorFee), Constants.WasabiCoordScripts.First()))
                .ToArray();
            var tx = Tx(Inputs(withChange: 10), outputs);

            Assert.True(Scanner.IsWasabiCoinJoin(tx, Constants.FirstWasabiNoCoordAddressBlock - 1, isNativeSegwitOnly: true, NoKnownCoinJoins));
        }

        /// <summary>
        /// Inputs of the ten participants. The first <paramref name="withChange"/> put in a base denomination, their change
        /// of <see cref="Outputs"/> and the fees, the others a base denomination and the fees. The first participants spend
        /// <paramref name="earlierRounds"/>, one each; the others spend unrelated transactions.
        /// </summary>
        private static VerboseInputInfo[] Inputs(int withChange, params uint256[] earlierRounds)
            => Enumerable.Range(0, ParticipantCount)
                .Select(i => In(
                    V + (i < withChange ? Change(i) : 0) + Fees,
                    new OutPoint(i < earlierRounds.Length ? earlierRounds[i] : RandomUtils.GetUInt256(), i)))
                .ToArray();

        /// <summary>
        /// The equal outputs of the ten participants, the first <paramref name="changes"/> of their distinct changes and,
        /// if asked for, the coordinator output.
        /// </summary>
        private static VerboseOutputInfo[] Outputs(int changes, bool coordinator)
            => Enumerable.Range(0, ParticipantCount).Select(_ => Out(V))
                .Concat(Enumerable.Range(0, changes).Select(i => Out(Change(i))))
                .Concat(coordinator ? new[] { Out(CoordinatorFee) } : Array.Empty<VerboseOutputInfo>())
                .ToArray();

        private static long Change(int i) => 1_000_000 + (i * 137_000);

        private static VerboseTransactionInfo Tx(VerboseInputInfo[] inputs, VerboseOutputInfo[] outputs)
            => new(null, RandomUtils.GetUInt256(), inputs, outputs);

        private static VerboseInputInfo In(long value, OutPoint outPoint)
            => new(outPoint, Out(value), uint.MaxValue);

        private static VerboseOutputInfo Out(long value)
            => new(Money.Satoshis(value), new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));
    }
}
