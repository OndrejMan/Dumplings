using Dumplings.Rpc;
using Dumplings.Scanning;
using NBitcoin;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Dumplings.Tests
{
    public class Tx0CandidateStoreTests : IDisposable
    {
        private const string TxA = "7732a03c8cf133e0475ae37e4f2f49ba77beb631378216889e33e9847aa0049b";
        private const string TxB = "52d47c5f7f1f10346a299a915df44b644386f46eb8df809cbe5ce3ab95ad0618";
        private const string TxC = "f2cad4dc598347a47325ccacd26e3d75dbc41d17e8119a1e1b5d14b824cf1803";

        private static readonly Script OpReturn = TxNullDataTemplate.Instance.GenerateScriptPubKey(new byte[] { 1, 2, 3 });
        private static readonly Script Segwit = Script.FromHex("0014" + new string('a', 40));

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "tx0-candidates-" + Guid.NewGuid());

        public Tx0CandidateStoreTests()
        {
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }

        private string FilePath(string name) => Path.Combine(_directory, name);

        private static VerboseTransactionInfo Tx(string txid, params (long satoshis, Script script)[] outputs)
        {
            var input = new VerboseInputInfo(new OutPoint(uint256.Parse(TxB), 6), new VerboseOutputInfo(Money.Satoshis(100_000_000), Segwit), uint.MaxValue);
            return new VerboseTransactionInfo(
                new TransactionBlockInfo(uint256.Parse(TxC), DateTimeOffset.FromUnixTimeSeconds(1_560_000_000), 7),
                uint256.Parse(txid),
                new[] { input },
                outputs.Select(x => new VerboseOutputInfo(Money.Satoshis(x.satoshis), x.script)).ToArray());
        }

        // Shaped like a 0.01 BTC Whirlpool tx0: fee payload, premix outputs, coordinator fee and change.
        private static VerboseTransactionInfo Tx0(string txid) =>
            Tx(txid, (0, OpReturn), (1_000_170, Segwit), (1_000_170, Segwit), (50_000, Segwit), (97_000_000, Segwit));

        [Fact]
        public void Tx0IsACandidate()
        {
            Assert.True(Tx0CandidateStore.IsCandidate(Tx0(TxA)));
        }

        [Fact]
        public void TransactionsWithoutTheTx0ShapeAreNoCandidates()
        {
            // No OP_RETURN.
            Assert.False(Tx0CandidateStore.IsCandidate(Tx(TxA, (1_000_170, Segwit), (1_000_170, Segwit), (97_000_000, Segwit))));
            // OP_RETURN with dust and change only, like Omni or Runes.
            Assert.False(Tx0CandidateStore.IsCandidate(Tx(TxA, (0, OpReturn), (546, Segwit), (97_000_000, Segwit))));
            // An output just below a pool is no premix.
            Assert.False(Tx0CandidateStore.IsCandidate(Tx(TxA, (0, OpReturn), (999_999, Segwit), (97_000_000, Segwit))));
            // Two outputs only.
            Assert.False(Tx0CandidateStore.IsCandidate(Tx(TxA, (0, OpReturn), (1_000_170, Segwit))));
        }

        [Fact]
        public void StoredCandidatesReadBackAsTheyWereScanned()
        {
            var store = new Tx0CandidateStore(FilePath("Tx0Candidates.txt"));
            store.Append(new[] { Tx0(TxA), Tx0(TxC) });

            Assert.True(store.TryGet(uint256.Parse(TxC), out var tx));
            Assert.Equal(RpcParser.ToLine(Tx0(TxC)), RpcParser.ToLine(tx));
            Assert.False(store.TryGet(uint256.Parse(TxB), out _));
            Assert.Equal(2, store.Count);
        }

        [Fact]
        public void ReopeningIndexesOnlyWhatTheCheckpointKept()
        {
            var path = FilePath("Tx0Candidates.txt");
            var checkpoint = FilePath("Checkpoint.txt");
            new Tx0CandidateStore(path).Append(new[] { Tx0(TxA) });
            ScanCheckpoint.WriteCheckpoint(checkpoint, 700_000, new[] { path });
            new Tx0CandidateStore(path).Append(new[] { Tx0(TxC) });

            ScanCheckpoint.Restore(checkpoint, new[] { path });
            var store = new Tx0CandidateStore(path);

            Assert.Equal(1, store.Count);
            Assert.True(store.TryGet(uint256.Parse(TxA), out var tx));
            Assert.Equal(RpcParser.ToLine(Tx0(TxA)), RpcParser.ToLine(tx));
            Assert.False(store.TryGet(uint256.Parse(TxC), out _));
        }
    }
}
