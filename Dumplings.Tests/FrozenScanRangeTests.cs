using Dumplings.Scanning;
using Dumplings.Helpers;
using NBitcoin;
using NBitcoin.RPC;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Dumplings.Tests
{
    [CollectionDefinition("Frozen scanner files", DisableParallelization = true)]
    public class FrozenScannerFilesCollection { }

    [Collection("Frozen scanner files")]
    public class FrozenScanRangeTests
    {
        [Theory]
        [InlineData(200000UL, null, 200001UL)]
        [InlineData(300000UL, 200000UL, 300001UL)]
        [InlineData(400000UL, 300000UL, 336861UL)]
        [InlineData(400000UL, 350000UL, 350001UL)]
        [InlineData(400000UL, 400000UL, 400001UL)]
        public void BoundsInitialAndResumedScans(ulong tip, ulong? checkpoint, ulong expected)
        {
            Assert.Equal(expected, Scanner.NextFrozenScanHeight(Constants.FirstJoinMarketBlock, tip, checkpoint));
        }

        [Fact]
        public void RegtestStartsAtItsExplicitFirstBlock()
        {
            Assert.Equal(1UL, Scanner.NextFrozenScanHeight(1, 100));
        }

        [Fact]
        public void RefusesCheckpointAboveTheFrozenTip()
        {
            Assert.Throws<InvalidOperationException>(() => Scanner.NextFrozenScanHeight(336861, 200000, 300000));
        }

        [Fact]
        public void FrozenChainRequiresOfflineCoreAtExactHeight()
        {
            BitcoinStatus.ValidateFrozenChain(200000, 200000, false);
            Assert.Throws<InvalidOperationException>(() => BitcoinStatus.ValidateFrozenChain(200000, 200000, true));
            Assert.Throws<InvalidOperationException>(() => BitcoinStatus.ValidateFrozenChain(200001, 200000, false));
        }

        [Fact]
        public async Task HistoricalScanPublishesAndAdvancesAnEmptyCheckpoint()
        {
            var previousDirectory = Directory.GetCurrentDirectory();
            var directory = Path.Combine(Path.GetTempPath(), "frozen-scanner-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            using var listener = new HttpListener();
            var url = new Uri($"http://127.0.0.1:{port}/");
            listener.Prefixes.Add(url.ToString());
            listener.Start();
            ulong tip = 200000;
            var server = Task.Run(async () =>
            {
                try
                {
                    while (listener.IsListening)
                    {
                        var context = await listener.GetContextAsync();
                        using var reader = new StreamReader(context.Request.InputStream);
                        var request = JObject.Parse(await reader.ReadToEndAsync());
                        var method = request["method"].ToString();
                        JToken result = method switch
                        {
                            "getblockcount" => new JValue(tip),
                            "getnetworkinfo" => JObject.FromObject(new { networkactive = false }),
                            "getblockchaininfo" => JObject.FromObject(new
                            {
                                chain = "main", blocks = tip, headers = 900000,
                                bestblockhash = new string('a', 64), difficulty = 1,
                                mediantime = 1231006505, verificationprogress = 0.01,
                                initialblockdownload = true, chainwork = new string('0', 64),
                                size_on_disk = 1, pruned = true, warnings = ""
                            }),
                            _ => throw new InvalidOperationException("Unexpected RPC: " + method)
                        };
                        var response = new JObject { ["result"] = result, ["error"] = null, ["id"] = request["id"] };
                        var bytes = Encoding.UTF8.GetBytes(response.ToString());
                        context.Response.ContentType = "application/json";
                        await context.Response.OutputStream.WriteAsync(bytes);
                        context.Response.Close();
                    }
                }
                catch (HttpListenerException) when (!listener.IsListening) { }
                catch (ObjectDisposedException) when (!listener.IsListening) { }
            });
            try
            {
                Directory.SetCurrentDirectory(directory);
                var client = new RPCClient("user:password", url, Network.Main);
                foreach (var height in new[] { 200000UL, 300000UL })
                {
                    tip = height;
                    var scanner = new Scanner(client, height);
                    await scanner.ScanAsync(rescan: false, includeTip: true);
                    Assert.Equal(height.ToString(), File.ReadAllText(Scanner.LastProcessedBlockHeightPath));
                    Assert.Empty(File.ReadAllText(Scanner.Tx0CandidatesPath));
                    Assert.Empty(File.ReadAllText(Scanner.Tx0CandidateMissesPath));
                    Assert.StartsWith(height + "\n", File.ReadAllText(Scanner.CheckpointPath));
                }
            }
            finally
            {
                listener.Stop();
                await server;
                Directory.SetCurrentDirectory(previousDirectory);
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
