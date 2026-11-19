using Dumplings.Rpc;
using Dumplings.Scanning;
using NBitcoin;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Dumplings.Tests
{
    public class SamouraiCoinJoinDetectionTests
    {
        private const long Pool = 5_000_000; // The 0.05 BTC pool.
        private const ulong Height = 600_000; // Every pool before 0.001 BTC exists here.

        // No file exists at this path, so the store is empty and never touches the disk.
        private static readonly Tx0CandidateStore NoStoredTx0Candidates = new(Path.Combine(Path.GetTempPath(), "no-tx0-candidates-" + Guid.NewGuid()));
        private static readonly VerboseTransactionInfo[] NoBlockTx0Candidates = Array.Empty<VerboseTransactionInfo>();

        [Fact]
        public void AcceptsRoundWithRemix()
        {
            // Two remixes and three premixes, the shape SCDH describes; tx0 candidates do not matter.
            var tx = Tx(
                new[] { In(Pool), In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 6_050) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.True(Scanner.IsSamouraiCoinJoin(tx, Height, NoStoredTx0Candidates, NoBlockTx0Candidates));
        }

        [Fact]
        public void AcceptsMainnetFirstRoundA554db79()
        {
            // Block 572030, the first round of the 0.05 BTC pool: five premixes, no remix. Its five parents are tx0s with
            // an OP_RETURN (checked on mempool.space) and the funding transactions of coinjoin-analysis name it.
            var tx = A554db79();
            var tx0s = tx.Inputs.Select(x => Parent(x.OutPoint.Hash)).ToArray();

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, 572030, NoStoredTx0Candidates, NoBlockTx0Candidates)); // Before: missed, then looked up as a tx0.
            Assert.True(Scanner.IsSamouraiCoinJoin(tx, 572030, NoStoredTx0Candidates, tx0s));
        }

        [Fact]
        public void RejectsRoundWithoutRemixWhenOneParentIsNoTx0Candidate()
        {
            var tx = A554db79();
            var tx0s = tx.Inputs.Skip(1).Select(x => Parent(x.OutPoint.Hash)).ToArray();

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, 572030, NoStoredTx0Candidates, tx0s));
        }

        [Fact]
        public void RejectsInputAbovePremixMargin()
        {
            var tx = Tx(
                new[] { In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 110_001) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height, NoStoredTx0Candidates, NoBlockTx0Candidates));
        }

        [Fact]
        public void RejectsUnequalInputAndOutputCount()
        {
            var tx = Tx(
                new[] { In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 10_000) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height, NoStoredTx0Candidates, NoBlockTx0Candidates));
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

        /// <summary>A tx0 candidate of the same block; the detector reads only its txid.</summary>
        private static VerboseTransactionInfo Parent(uint256 txid)
            => new(null, txid, Array.Empty<VerboseInputInfo>(), Array.Empty<VerboseOutputInfo>());

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
