using Dumplings.Rpc;
using Dumplings.Scanning;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Dumplings.Tests
{
    public class SamouraiCoinJoinDetectionTests
    {
        private const long Pool = 5_000_000; // The 0.05 BTC pool.
        private const ulong Height = 600_000; // Every pool before 0.001 BTC exists here.

        private static readonly Func<uint256, bool> NoTx0Candidates = _ => false;

        [Fact]
        public void AcceptsRoundWithRemix()
        {
            // Two remixes and three premixes, the shape SCDH describes; tx0 candidates do not matter.
            var tx = Tx(
                new[] { In(Pool), In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 6_050) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.True(Scanner.IsSamouraiCoinJoin(tx, Height, NoTx0Candidates));
        }

        [Fact]
        public void AcceptsMainnetFirstRoundA554db79()
        {
            // Block 572030, the first round of the 0.05 BTC pool: five premixes, no remix. Its five parents are tx0s with
            // an OP_RETURN (checked on mempool.space) and the funding transactions of coinjoin-analysis name it.
            var tx = A554db79();
            var tx0s = new HashSet<uint256>(tx.Inputs.Select(x => x.OutPoint.Hash));

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, 572030, NoTx0Candidates)); // Before: missed, then looked up as a tx0.
            Assert.True(Scanner.IsSamouraiCoinJoin(tx, 572030, tx0s.Contains));
        }

        [Fact]
        public void RejectsRoundWithoutRemixWhenOneParentIsNoTx0Candidate()
        {
            var tx = A554db79();
            var tx0s = new HashSet<uint256>(tx.Inputs.Select(x => x.OutPoint.Hash).Skip(1));

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, 572030, tx0s.Contains));
        }

        [Fact]
        public void RejectsMainnetAshigaruPoolLookAlikeF8d1354b()
        {
            // Block 667449 (January 2021): five 0.25 BTC outputs, two inputs of 0.25 BTC. Both parents are also spent
            // together by the consolidation d160500d, so one entity made it; the 0.25 BTC pool started in June 2025.
            var tx = F8d1354b();

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, 667449, NoTx0Candidates));
            Assert.True(Scanner.IsSamouraiCoinJoin(tx, Constants.SamouraiPoolFirstBlocks[Money.Coins(0.25m).Satoshi], NoTx0Candidates));
        }

        [Theory]
        [InlineData(5_000_000, 572030)]
        [InlineData(1_000_000, 577445)]
        [InlineData(50_000_000, 588252)]
        [InlineData(100_000, 673322)]
        [InlineData(2_500_000, 899205)]
        [InlineData(25_000_000, 899973)]
        public void PoolStartsAtItsFirstCoinJoin(long pool, ulong firstBlock)
        {
            Assert.False(Constants.IsSamouraiPool(Money.Satoshis(pool), firstBlock - 1));
            Assert.True(Constants.IsSamouraiPool(Money.Satoshis(pool), firstBlock));
        }

        [Fact]
        public void EveryPoolHasAStart()
        {
            Assert.Equal(Constants.SamouraiPools.Select(x => x.Satoshi).OrderBy(x => x), Constants.SamouraiPoolFirstBlocks.Keys.OrderBy(x => x));
        }

        [Fact]
        public void RejectsRoundOfPoolBeforeItsStart()
        {
            const long pool = 100_000; // 0.001 BTC, from block 673322.
            var tx = Tx(
                new[] { In(pool), In(pool + 9_690), In(pool + 9_690), In(pool + 9_690), In(pool + 9_690) },
                Enumerable.Repeat(pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, 673321, NoTx0Candidates));
            Assert.True(Scanner.IsSamouraiCoinJoin(tx, 673322, NoTx0Candidates));
        }

        [Fact]
        public void RejectsInputAbovePremixMargin()
        {
            var tx = Tx(
                new[] { In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 110_001) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height, NoTx0Candidates));
        }

        [Fact]
        public void RejectsUnequalInputAndOutputCount()
        {
            var tx = Tx(
                new[] { In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height, NoTx0Candidates));
        }

        private static VerboseTransactionInfo A554db79() => new(
            null,
            uint256.Parse("a554db794560458c102bab0af99773883df13bc66ad287c29610ad9bac138926"),
            new[]
            {
                In("34d98365d293d94f5fb924bc7f8f186a329073a749b6d4413b437a11acd0b8f2", 3, 5010000, "001414e39064ee30b84b1ad363d79f824627a3c9e6a5"),
                In("53a9cc6fd781f481ab1b57e538d68921ed2bd03ee2e8e10c2ac6524aebe8e84c", 2, 5010000, "001416a9510ac6a3e11592eef92b1f4c7c7ef9d0bf0f"),
                In("c2e818385adec386955e29e61c82adb40ed45ae5f6efde2ebafab5796d20540a", 2, 5006050, "0014b0eabbaa34c13599186c0b1f5de285038fc61bc4"),
                In("e02c1f780ba965536c6a7899a0723596aaad7b9244ec62b40ddeea693c34c282", 2, 5010000, "00147d23501ee9f0d0104ecda34adc150232c9009f42"),
                In("efca887f51658a112186b7e90b91a667a659fd4ad273deb0c401c1425bd06669", 2, 5010000, "00140a6ce7120890ce562941af2668b2fbd29421264f"),
            },
            new[]
            {
                Out(5000000, "00143a2f8ea263589feca4a6565dcf8e70934881079d"),
                Out(5000000, "00144db31c0c838dee6f4d5f2036c3b2bec56bb81b37"),
                Out(5000000, "001457ccff6ef2ca1f6e65867b0a3ece7a5ab5448495"),
                Out(5000000, "0014e057ab160749692a13b7c5569709e846091126dd"),
                Out(5000000, "0014e44a7bf68a5d22bdf4fb4e7b3fe6984a411a3756"),
            });

        private static VerboseTransactionInfo F8d1354b() => new(
            null,
            uint256.Parse("f8d1354b8ae9fc7a8d8a01014a2b804f0c5c6f3633e7f6c531409488a10101d1"),
            new[]
            {
                In("4096bd57e6282fc125fbe2ed6311801d213a77b4fed9394fca478eb8592d7c32", 5, 25000000, "0014728290a615bb5a358b3d97cd99e556762a30387c"),
                In("4096bd57e6282fc125fbe2ed6311801d213a77b4fed9394fca478eb8592d7c32", 9, 25000000, "0014fa92ba8ee7515326a99872c9733afc5bd16a6294"),
                In("8e329bc08fb043b069104631c355a5f96eaf7f6bd8f7e75fddb0f6a75865ae20", 2, 25000543, "001423f728bfbf90c1b169823f5dd79678fdda4419ff"),
                In("8e329bc08fb043b069104631c355a5f96eaf7f6bd8f7e75fddb0f6a75865ae20", 3, 25000543, "001471c37435f03014f7db8d398d437bf20c90abacfd"),
                In("8e329bc08fb043b069104631c355a5f96eaf7f6bd8f7e75fddb0f6a75865ae20", 4, 25000543, "0014e3871682d581359997ddf6a0905592b7bfa00dd1"),
            },
            new[]
            {
                Out(25000000, "0014170c0bbba99b304766f42480f532b10b54aa7543"),
                Out(25000000, "0014230c88d0cd58b728523ebe4fcdb5bfe5467274e4"),
                Out(25000000, "001434f430dc5790d676b6beb3be0a0f2eff9953a13e"),
                Out(25000000, "0014c0885d714a1e8651647f6c441ff5ce31825756c9"),
                Out(25000000, "0014f7851b3e9549b964c16392bc68af2bc9150f6f3e"),
            });

        private static VerboseTransactionInfo Tx(VerboseInputInfo[] inputs, VerboseOutputInfo[] outputs)
            => new(null, RandomUtils.GetUInt256(), inputs, outputs);

        private static VerboseInputInfo In(long value)
            => new(new OutPoint(RandomUtils.GetUInt256(), 0), Out(value), uint.MaxValue);

        private static VerboseInputInfo In(string parent, uint n, long value, string scriptHex)
            => new(new OutPoint(uint256.Parse(parent), n), Out(value, scriptHex), uint.MaxValue);

        private static VerboseOutputInfo Out(long value)
            => new(Money.Satoshis(value), new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));

        private static VerboseOutputInfo Out(long value, string scriptHex)
            => new(Money.Satoshis(value), Script.FromHex(scriptHex));
    }
}
