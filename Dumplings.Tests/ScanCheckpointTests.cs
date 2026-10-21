using Dumplings.Scanning;
using NBitcoin;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Dumplings.Tests
{
    public class ScanCheckpointTests : IDisposable
    {
        private const string TxA = "7732a03c8cf133e0475ae37e4f2f49ba77beb631378216889e33e9847aa0049b";
        private const string TxB = "52d47c5f7f1f10346a299a915df44b644386f46eb8df809cbe5ce3ab95ad0618";
        private const string TxC = "f2cad4dc598347a47325ccacd26e3d75dbc41d17e8119a1e1b5d14b824cf1803";

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "scan-checkpoint-" + Guid.NewGuid());

        public ScanCheckpointTests()
        {
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }

        private string FilePath(string name) => Path.Combine(_directory, name);

        private static string Line(string txid) => $"{txid}:::00000000000000000002dddc:::7:::1530763257:::{TxB}-6-100000000+0014ab+TxWitnessV0Keyhash:::83240446+0014cd+TxWitnessV0Keyhash";

        [Fact]
        public void ReadIdsStreamsTransactionIds()
        {
            var coinjoins = FilePath("OtherCoinJoins.txt");
            File.WriteAllText(coinjoins, Line(TxA) + "\n" + Line(TxC) + "\n");

            var ids = ScanCheckpoint.ReadIds(coinjoins).ToArray();

            Assert.Equal(new[] { uint256.Parse(TxA), uint256.Parse(TxC) }, ids);
            Assert.Empty(ScanCheckpoint.ReadIds(FilePath("Missing.txt")));
        }

        [Fact]
        public void RemoveTransactionRemovesOnlyTheGivenTransaction()
        {
            var coinjoins = FilePath("OtherCoinJoins.txt");
            File.WriteAllText(coinjoins, Line(TxA) + "\n" + Line(TxB) + "\n" + Line(TxC) + "\n");

            ScanCheckpoint.RemoveTransaction(coinjoins, uint256.Parse(TxB));

            Assert.Equal(Line(TxA) + "\n" + Line(TxC) + "\n", File.ReadAllText(coinjoins));
            Assert.False(File.Exists(coinjoins + ".tmp"));
        }
    }
}
