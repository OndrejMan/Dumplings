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
        public void RejectsMainnetAshigaruPoolLookAlikeF8d1354b()
        {
            // Block 667449 (January 2021): five 0.25 BTC outputs, two inputs of 0.25 BTC. Both parents are also spent
            // together by the consolidation d160500d, so one entity made it; the 0.25 BTC pool started in June 2025.
            var tx = F8d1354b();

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, 667449, NoStoredTx0Candidates, NoBlockTx0Candidates));
            Assert.True(Scanner.IsSamouraiCoinJoin(tx, Constants.SamouraiPoolFirstBlocks[Money.Coins(0.25m).Satoshi], NoStoredTx0Candidates, NoBlockTx0Candidates));
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

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, 673321, NoStoredTx0Candidates, NoBlockTx0Candidates));
            Assert.True(Scanner.IsSamouraiCoinJoin(tx, 673322, NoStoredTx0Candidates, NoBlockTx0Candidates));
        }

        [Fact]
        public void AcceptsMainnetRoundWithP2wshOutput04236db9()
        {
            // Block 749348: three remixes and two premixes of the 0.01 BTC pool; one output pays a P2WSH script.
            Assert.True(Scanner.IsSamouraiCoinJoin(_04236db9(), 749348, NoStoredTx0Candidates, NoBlockTx0Candidates));
        }

        [Fact]
        public void RejectsRoundWithP2wshInput()
        {
            // Only an output may be P2WSH: the coordinator takes a single-key signature for every input.
            var p2wsh = In(RandomUtils.GetUInt256().ToString(), 0, Pool, "0020ae1d8a2b8c1395fe28d73300b864bc7c505ebbbb7d5423822d38426c94ff776c");
            var tx = Tx(
                new[] { p2wsh, In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 6_050) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height, NoStoredTx0Candidates, NoBlockTx0Candidates));
        }

        [Fact]
        public void RejectsRoundWithTaprootOutput()
        {
            var outputs = Enumerable.Repeat(Pool, 4).Select(x => Out(x)).ToList();
            outputs.Add(new VerboseOutputInfo(Money.Satoshis(Pool), new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));
            var tx = Tx(new[] { In(Pool), In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 6_050) }, outputs.ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height, NoStoredTx0Candidates, NoBlockTx0Candidates));
        }

        [Fact]
        public void RejectsMainnetLookAlike4a49f53d()
        {
            // Block 815011 (November 2023): four outputs of 0.5 BTC rounds of December 2022 and a fresh coin of
            // 0.50008369 BTC, put together by a wallet and not by the coordinator: sequence 0xfffffffd and no BIP69
            // order (also version 2 and locktime 815008). The parent 22991a07… of the fresh coin has the outputs of a
            // tx0 but not the OP_RETURN the coordinator requires of one.
            var tx = _4a49f53d();
            var asTheCoordinatorBuildsIt = Tx(
                tx.Inputs.Select(x => new VerboseInputInfo(x.OutPoint, x.PrevOutput, uint.MaxValue)).ToArray(),
                tx.Outputs.ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, 815011, NoStoredTx0Candidates, NoBlockTx0Candidates));
            Assert.True(Scanner.IsSamouraiCoinJoin(asTheCoordinatorBuildsIt, 815011, NoStoredTx0Candidates, NoBlockTx0Candidates));
            // No coinjoin of another kind either: the scanner files it as a post-mix transaction of the rounds it spends.
            Assert.False(Scanner.IsOtherCoinJoin(tx, _ => false));
        }

        [Fact]
        public void RejectsRoundWithNonFinalSequence()
        {
            // One input signals replace-by-fee, as wallets do; the coordinator leaves every sequence final.
            var inputs = new[] { In(Pool), In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 6_050) };
            inputs[0] = new VerboseInputInfo(inputs[0].OutPoint, inputs[0].PrevOutput, 0xfffffffd);
            var tx = Tx(inputs, Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());

            Assert.False(Scanner.IsSamouraiCoinJoin(tx, Height, NoStoredTx0Candidates, NoBlockTx0Candidates));
        }

        [Fact]
        public void RejectsRoundNotInBip69Order()
        {
            var round = Tx(
                new[] { In(Pool), In(Pool), In(Pool + 10_000), In(Pool + 10_000), In(Pool + 6_050) },
                Enumerable.Repeat(Pool, 5).Select(x => Out(x)).ToArray());
            var inputs = round.Inputs.ToArray();
            var outputs = round.Outputs.ToArray();

            Assert.True(Scanner.IsSamouraiCoinJoin(round, Height, NoStoredTx0Candidates, NoBlockTx0Candidates));
            Assert.False(Scanner.IsSamouraiCoinJoin(Unordered(inputs.Reverse().ToArray(), outputs), Height, NoStoredTx0Candidates, NoBlockTx0Candidates));
            Assert.False(Scanner.IsSamouraiCoinJoin(Unordered(inputs, outputs.Reverse().ToArray()), Height, NoStoredTx0Candidates, NoBlockTx0Candidates));
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

        private static VerboseTransactionInfo _04236db9() => new(
            null,
            uint256.Parse("04236db9e38102a5bb2373bb0c1c8eaaad3bc62ece72ad74a8a0e33af3b3fc0a"),
            new[]
            {
                In("34512bfda2be0d792e6ffff7dba3634244eb340c2cbc98404ee5b42318589da0", 0, 1000000, "001402200a91788f1a1199c816254d08d32788a3b46a"),
                In("5eca6a4c02d3a440c314383ad4fd664d968dad4390f043bb3d7d07cdf811ba1c", 28, 1000302, "0014bf6279d7a69a639a907e741c322c4b8671d90c77"),
                In("9a7db61976ce8ff8c763b5af99b28d4f3e2cddd110c13796d273109f24c9e420", 3, 1000000, "0014f54ddd0a3a89fa6fab4ab811159b40a9e9a264de"),
                In("dcb00a211048a8ebeb569f3678789e261f1d2fb9fcafb5f4155ae3a10c71d347", 4, 1002117, "001484f0c908be866aa5e2d4f9e10c1406cf66695ecf"),
                In("ff65f303a1950d690a8610784de51b367a63953aea25a62ddcd94af16f0805dc", 4, 1000000, "0014e674df6929f978d86bbff4dfe468abf5a58ed922"),
            },
            new[]
            {
                Out(1000000, "001461352f7f20dec67ff8af0750ebdacd18ea5270b6"),
                Out(1000000, "0014998b8cb0fec39a16c23738ac35bcac78001cc885"),
                Out(1000000, "0014d669b5630dc3a38e829b5e622fe44d57d58dce62"),
                Out(1000000, "0014f2c45664dcce1bdaefda262e6c9bb2f8afef276e"),
                Out(1000000, "0020ae1d8a2b8c1395fe28d73300b864bc7c505ebbbb7d5423822d38426c94ff776c"),
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

        private static VerboseTransactionInfo _4a49f53d() => new(
            null,
            uint256.Parse("4a49f53d798e64fcca6c5e31baab359e4fcf37e4dd118fff3af1cd0220d2904a"),
            new[]
            {
                In("0a749eb05e067c5b3476c538e9cdc782a7a64a97ff04209589298bc9a155b70b", 4, 50000000, "001479ce42a277cde3d280f5efd4be2f8c8971573674", 0xfffffffd),
                In("b6ee5f5ae4b39d8c0e7e9d67b61521026144f0702a11f327d04a09f2a62c3523", 0, 50000000, "001436b85780dc97bfeb2d51c9319db7692921c93834", 0xfffffffd),
                In("50e20874dada489f025c80cd141f1e6a83373f5329d1b27e6813bb67ff8eb462", 2, 50000000, "00144c37386839ab102fb97ebf939fc400fec6fdfec0", 0xfffffffd),
                In("22991a0798e7e2aadfeda8e05bf9735bce0e4d10dbbed785a41fca9eca00149b", 0, 50008369, "0014eaf24f77e3056d2975589d45eca11208445b331f", 0xfffffffd),
                In("23de5290e2d6e6480a67c855c96bfb06403f660f85f908a4ec1921530d1d5519", 1, 50000000, "0014850d76c2a98f9af115b632cb26f60ac3b6548913", 0xfffffffd),
            },
            new[]
            {
                Out(50000000, "001495854ad6095ef1fc88f92b8c6e01b8de5833d95c"),
                Out(50000000, "00141248dde1d3537c4273acfc6ee8f2a3caedbccc59"),
                Out(50000000, "00148e75172bb537c422b11e47bac995fb8af7cf4911"),
                Out(50000000, "0014b40d8274fc980c6e7f089df3cc6f2228ea9c9f79"),
                Out(50000000, "0014df0fffbbc937518c571e8270ad70ada863ff24bf"),
            });

        /// <summary>A tx0 candidate of the same block; the detector reads only its txid.</summary>
        private static VerboseTransactionInfo Parent(uint256 txid)
            => new(null, txid, Array.Empty<VerboseInputInfo>(), Array.Empty<VerboseOutputInfo>());

        /// <summary>A transaction in the BIP69 order the coordinator gives a round, sorted here by the displayed hex.</summary>
        private static VerboseTransactionInfo Tx(VerboseInputInfo[] inputs, VerboseOutputInfo[] outputs)
            => Unordered(
                inputs.OrderBy(x => x.OutPoint.Hash.ToString(), StringComparer.Ordinal).ThenBy(x => x.OutPoint.N).ToArray(),
                outputs.OrderBy(x => x.Value.Satoshi).ThenBy(x => x.ScriptPubKey.ToHex(), StringComparer.Ordinal).ToArray());

        private static VerboseTransactionInfo Unordered(VerboseInputInfo[] inputs, VerboseOutputInfo[] outputs)
            => new(null, RandomUtils.GetUInt256(), inputs, outputs);

        private static VerboseInputInfo In(long value)
            => new(new OutPoint(RandomUtils.GetUInt256(), 0), Out(value), uint.MaxValue);

        private static VerboseInputInfo In(string parent, uint n, long value, string scriptHex, uint sequence = uint.MaxValue)
            => new(new OutPoint(uint256.Parse(parent), n), Out(value, scriptHex), sequence);

        private static VerboseOutputInfo Out(long value)
            => new(Money.Satoshis(value), new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.Segwit));

        private static VerboseOutputInfo Out(long value, string scriptHex)
            => new(Money.Satoshis(value), Script.FromHex(scriptHex));
    }
}
