using Dumplings.Analysis;
using Dumplings.Helpers;
using Dumplings.Rpc;
using Microsoft.Extensions.Caching.Memory;
using NBitcoin;
using NBitcoin.RPC;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Dumplings.Scanning
{
    public class Scanner
    {
        public Scanner(RPCClient rpc, ulong? frozenHeight = null)
        {
            Rpc = rpc;
            Directory.CreateDirectory(WorkFolder);
            BitcoinStatus.CheckAsync(rpc, frozenHeight).GetAwaiter().GetResult();
        }

        //public const string WorkFolder = @"C:\Users\user\source\repos\Dumplings\Dumplings.Cli\bin\Release\netcoreapp3.1\Scanner";
        public const string WorkFolder = "Scanner";

        public static readonly string LastProcessedBlockHeightPath = Path.Combine(WorkFolder, "LastProcessedBlockHeight.txt");
        public static readonly string WasabiCoinJoinsPath = Path.Combine(WorkFolder, "WasabiCoinJoins.txt");
        public static readonly string Wasabi2CoinJoinsPath = Path.Combine(WorkFolder, "Wasabi2CoinJoins.txt");
        public static readonly string SamouraiCoinJoinsPath = Path.Combine(WorkFolder, "SamouraiCoinJoins.txt");
        public static readonly string SamouraiTx0sPath = Path.Combine(WorkFolder, "SamouraiTx0s.txt");
        public static readonly string OtherCoinJoinsPath = Path.Combine(WorkFolder, "OtherCoinJoins.txt");
        public static readonly string Wasabi2PostMixTxsPath = Path.Combine(WorkFolder, "Wasabi2PostMixTxs.txt");
        public static readonly string WasabiPostMixTxsPath = Path.Combine(WorkFolder, "WasabiPostMixTxs.txt");
        public static readonly string SamouraiPostMixTxsPath = Path.Combine(WorkFolder, "SamouraiPostMixTxs.txt");
        public static readonly string OtherCoinJoinPostMixTxsPath = Path.Combine(WorkFolder, "OtherCoinJoinPostMixTxs.txt");
        public static readonly string Tx0CandidatesPath = Path.Combine(WorkFolder, "Tx0Candidates.txt");

        /// <summary>
        /// One line per tx0 the candidate rule failed: "&lt;height&gt; &lt;txid&gt; not-candidate" for a tx0 that does not match
        /// <see cref="Tx0CandidateStore.IsCandidate"/>, "&lt;height&gt; &lt;txid&gt; unresolved" for one a node without txindex
        /// could not supply. Empty after a full scan with txindex means pruning loses no tx0.
        /// </summary>
        public static readonly string Tx0CandidateMissesPath = Path.Combine(WorkFolder, "Tx0CandidateMisses.txt");
        public static readonly string CheckpointPath = Path.Combine(WorkFolder, "Checkpoint.txt");
        private const int CommitEveryBlocks = 100;
        private static readonly TimeSpan CommitInterval = TimeSpan.FromSeconds(60);

        private static readonly string[] OutputPaths =
        {
            Wasabi2CoinJoinsPath,
            WasabiCoinJoinsPath,
            SamouraiCoinJoinsPath,
            SamouraiTx0sPath,
            OtherCoinJoinsPath,
            Wasabi2PostMixTxsPath,
            WasabiPostMixTxsPath,
            SamouraiPostMixTxsPath,
            OtherCoinJoinPostMixTxsPath,
            Tx0CandidatesPath,
            Tx0CandidateMissesPath
        };

        public RPCClient Rpc { get; }

        private bool? TxIndex { get; set; }

        private decimal PercentageDone { get; set; } = 0;
        private decimal PreviousPercentageDone { get; set; } = -1;
        public static HashSet<long> Wasabi2Denominations { get; } = CreateWasabi2Denominations().ToHashSet();

        /// <summary>
        /// Fee allowance for the max-input bound of "other" (JoinMarket) coinjoins: the larger of a fixed
        /// amount covering mining fees on small denominations and a fraction of the denomination covering
        /// the taker's fees to makers on large ones.
        /// </summary>
        internal static Money OtherCoinJoinFeeAllowance(Money equalOutputValue)
        {
            var relative = Money.Satoshis(equalOutputValue.Satoshi * Constants.OtherCoinJoinFeeAllowancePerMille / 1000);
            return relative > Constants.OtherCoinJoinMinFeeAllowance ? relative : Constants.OtherCoinJoinMinFeeAllowance;
        }

        /// <summary>Largest premix input above the pool size, the mixing fee the premix pays on top.</summary>
        private static readonly Money SamouraiPremixMargin = Money.Coins(0.0011m);

        /// <summary>
        /// Most inputs of a Whirlpool round: the largest round seen. Surge Cycles (Samourai, 2023-05-15) add inputs above
        /// 5×5 as long as the miner fee the premixes bring covers them (whirlpool-server Mix.computeSurge), up to the
        /// deployment setting server.pools[x].surge, which the public source does not contain. Of the 541 131 rounds up
        /// to April 2024 none before 2023-11-10 has more than eight inputs, and the largest have 11, 12 and 15 inputs,
        /// all in blocks 824977–824986 (4e663aa7…, ccb2eb71…, e9cee5d9…). All 808 rounds of Ashigaru up to block
        /// 975643 have five.
        /// A larger round is rejected. It becomes visible only once a round remixes one of its outputs: that round
        /// looks it up as a tx0, and since a round has no OP_RETURN, the lookup writes a line to
        /// <see cref="Tx0CandidateMissesPath"/>, on which coinjoin-pipeline's mainnet-sync stops.
        /// </summary>
        private const int SamouraiMaxRoundInputs = 15;

        /// <summary>
        /// Samourai Whirlpool (and Ashigaru) coinjoin detection, based on SCDH from "Pinpointing and Measuring Wasabi and
        /// Samourai CoinJoins in the Bitcoin Ecosystem": exactly five uniform outputs of p BTC and five inputs, one to three
        /// of them equal to p BTC, the remaining two to four between p and p + 0.0011 BTC.
        /// Since Whirlpool rounds grew beyond five participants, this accepts 5 to <see cref="SamouraiMaxRoundInputs"/>
        /// inputs and as many outputs, and drops the upper limit on inputs equal to p BTC.
        /// A round without any input equal to p BTC (no remix) is accepted only when every input spends a stored tx0
        /// candidate. Such rounds start every pool, as no postmix coin exists yet to remix. Rejected, they were looked
        /// up as tx0s of the later rounds that remix their outputs.
        /// A pool counts only from its first coinjoin (<see cref="Constants.SamouraiPoolFirstBlocks"/>).
        /// An output may also be P2WSH: a client can send its postmix output to another wallet (Sparrow's "Mix to", from
        /// version 1.5.0), in practice a multisig one. Inputs stay P2WPKH, the script of the premix and postmix accounts.
        /// A round is built by the coordinator alone (whirlpool-server, MixService.computeTransaction), so every input
        /// has the final sequence and inputs and outputs are in BIP69 order. A wallet imitating a round leaves its own
        /// fingerprint, such as 4a49f53d… in block 815011, which spends four postmix coins and a fresh one with
        /// sequence 0xfffffffd in unsorted order. Accepted, its parent 22991a07… was looked up as a tx0, which it is
        /// not: it lacks the OP_RETURN that every tx0 carries and that the coordinator requires of one.
        /// </summary>
        /// <param name="tx0Candidates">The tx0 candidates of earlier blocks (<see cref="Tx0CandidateStore.IsCandidate"/>).</param>
        /// <param name="blockTx0Candidates">The tx0 candidates of this block that precede <paramref name="tx"/>.</param>
        internal static bool IsSamouraiCoinJoin(VerboseTransactionInfo tx, ulong height, Tx0CandidateStore tx0Candidates, IReadOnlyCollection<VerboseTransactionInfo> blockTx0Candidates)
        {
            var inputs = tx.Inputs.ToArray();
            var outputs = tx.Outputs.ToArray();
            if (height < Constants.FirstSamouraiBlock || outputs.Length == 0)
            {
                return false;
            }

            var poolSize = outputs[0].Value;
            var poolSizedInputCount = inputs.Count(x => x.PrevOutput.Value == poolSize);
            return inputs.All(x => x.PrevOutput.ScriptPubKey.IsScriptType(ScriptType.P2WPKH))
                && outputs.All(x => x.ScriptPubKey.IsScriptType(ScriptType.P2WPKH) || x.ScriptPubKey.IsScriptType(ScriptType.P2WSH))
                && inputs.Length >= 5 && inputs.Length <= SamouraiMaxRoundInputs
                && outputs.Length == inputs.Length
                && outputs.All(x => x.Value == poolSize) // Outputs are always equal.
                && Constants.IsSamouraiPool(poolSize, height)
                && inputs.All(x => x.Sequence == Sequence.SEQUENCE_FINAL)
                && IsBip69Ordered(inputs, outputs)
                && (poolSizedInputCount >= 1 || inputs.All(x => IsTx0Candidate(x.OutPoint.Hash)))
                && inputs.Where(x => x.PrevOutput.Value != poolSize).All(x => x.PrevOutput.Value.Almost(poolSize, SamouraiPremixMargin));

            // Only the persistent store and this block's earlier candidates confirm a round without remix, not the
            // size-limited OP_RETURN cache, so the result does not depend on what the cache has evicted.
            bool IsTx0Candidate(uint256 txid) => tx0Candidates.Contains(txid) || blockTx0Candidates.Any(x => x.Id == txid);
        }

        /// <summary>
        /// Whether inputs and outputs are in BIP69 order: inputs by the spent txid as displayed, then by its output
        /// index; outputs by value, then by the bytes of the scriptPubKey.
        /// </summary>
        internal static bool IsBip69Ordered(IReadOnlyList<VerboseInputInfo> inputs, IReadOnlyList<VerboseOutputInfo> outputs)
        {
            for (var i = 1; i < inputs.Count; i++)
            {
                var previous = inputs[i - 1].OutPoint;
                var current = inputs[i].OutPoint;
                var byTxid = previous.Hash.CompareTo(current.Hash);
                if (byTxid > 0 || (byTxid == 0 && previous.N > current.N))
                {
                    return false;
                }
            }

            for (var i = 1; i < outputs.Count; i++)
            {
                var previous = outputs[i - 1];
                var current = outputs[i];
                if (previous.Value > current.Value
                    || (previous.Value == current.Value && previous.ScriptPubKey.ToBytes().AsSpan().SequenceCompareTo(current.ScriptPubKey.ToBytes()) > 0))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Identifies Wasabi 1 coinjoins in blocks from <see cref="Constants.FirstWasabiBlock"/>. Only meaningful for non-coinbase
        /// transactions with equal outputs that weren't identified as Wasabi 2 coinjoins.
        /// Large JoinMarket coinjoins have the shape of the later rule too since JoinMarket moved to native segwit in 2021, and
        /// labelled as Wasabi 1 they turn the JoinMarket coinjoins that spend them into Wasabi 1 post-mix transactions. David
        /// Rajnoha found this, see section 1.2 of
        /// https://github.com/DavidRajnoha/coinjoin-analysis/blob/9c36115596c81a709acbce594c33ca9edafe098e/analysis_output/FINDINGS_2026.md#12-pipeline-issue-wasabi-1-precedence-hides-joinmarket-coinjoins
        /// The later rule therefore also requires remixing: at least <see cref="Constants.WasabiMinRemixInputs"/> inputs spend
        /// earlier Wasabi 1 coinjoins. The coins of the Wasabi 1 pool keep re-entering its rounds, while a JoinMarket coinjoin
        /// hardly ever spends them. <see cref="IsOtherCoinJoin"/> cannot make this distinction: a round with one mixing level
        /// and one or two participants without change passes it too.
        /// </summary>
        /// <param name="isNativeSegwitOnly">Whether all inputs and outputs are P2WPKH.</param>
        /// <param name="isKnownWasabiCoinJoin">Whether a txid is an already detected Wasabi 1 coinjoin.</param>
        internal static bool IsWasabiCoinJoin(VerboseTransactionInfo tx, ulong height, bool isNativeSegwitOnly, Func<uint256, bool> isKnownWasabiCoinJoin)
        {
            var indistinguishableOutputs = tx.GetIndistinguishableOutputs(includeSingle: false).ToArray();
            var inputCount = tx.Inputs.Count();
            (Money mostFrequentEqualOutputValue, int mostFrequentEqualOutputCount) = indistinguishableOutputs.OrderByDescending(x => x.count).First(); // Segwit only inputs.

            // Before Wasabi had constant coordinator addresses and different base denominations at the beginning.
            if (height < Constants.FirstWasabiNoCoordAddressBlock)
            {
                return tx.Outputs.Any(x => Constants.WasabiCoordScripts.Contains(x.ScriptPubKey)) && indistinguishableOutputs.Any(x => x.count > 2);
            }
            else
            {
                var uniqueOutputCount = tx.GetIndistinguishableOutputs(includeSingle: true).Count(x => x.count == 1);
                return
                    isNativeSegwitOnly
                    && mostFrequentEqualOutputCount >= Constants.MinWW2EqualOutputs
                    && inputCount >= mostFrequentEqualOutputCount // More inptuts than most frequent equal outputs.
                    && mostFrequentEqualOutputValue.Almost(Constants.ApproximateWasabiBaseDenomination, Constants.WasabiBaseDenominationPrecision) // The most frequent equal outputs must be almost the base denomination.
                    && uniqueOutputCount >= 2 // It's very likely there's at least one change and at least one coord output those have unique values.
                    && tx.Inputs.Count(x => isKnownWasabiCoinJoin(x.OutPoint.Hash)) >= Constants.WasabiMinRemixInputs; // A round remixes, a JoinMarket coinjoin of this shape does not.
            }
        }

        /// <summary>
        /// Identifies other (in practice JoinMarket) equal output coinjoin like transactions.
        /// Only meaningful for non-coinbase transactions that weren't identified as Wasabi, Wasabi 2 or Samourai coinjoins.
        /// </summary>
        /// <param name="isKnownOtherCoinJoin">Whether a txid is an already detected other coinjoin.</param>
        internal static bool IsOtherCoinJoin(VerboseTransactionInfo tx, Func<uint256, bool> isKnownOtherCoinJoin)
        {
            var indistinguishableOutputs = tx.GetIndistinguishableOutputs(includeSingle: false).ToArray();
            if (!indistinguishableOutputs.Any())
            {
                return false;
            }

            var outputs = tx.Outputs.ToArray();
            var inputs = tx.Inputs.Select(x => x.PrevOutput).ToArray();
            var outputValues = outputs.Select(x => x.Value);
            var inputValues = inputs.Select(x => x.Value);
            var outputCount = outputs.Length;
            (Money mostFrequentEqualOutputValue, int mostFrequentEqualOutputCount) = indistinguishableOutputs.OrderByDescending(x => x.count).First();

            return indistinguishableOutputs.Length == 1 // If it isn't then it'd be likely a multidenomination CJ, which only Wasabi does.
                && mostFrequentEqualOutputCount >= Constants.OtherCoinJoinMinEqualOutputs // Taker plus at least two makers. Two-party look-alikes are dust/anchor tooling, not JoinMarket.
                && (mostFrequentEqualOutputCount == outputCount - mostFrequentEqualOutputCount || mostFrequentEqualOutputCount == outputCount - mostFrequentEqualOutputCount + 1) // Rarely it isn't, but it helps filtering out false positives. +1 condition is for case when taker make sweep tx with no change
                && outputs.Select(x => x.ScriptPubKey).Distinct().Count() >= mostFrequentEqualOutputCount // Otherwise more participants would be single actors which makes no sense.
                && inputs.Select(x => x.ScriptPubKey).Distinct().Count() >= mostFrequentEqualOutputCount // Otherwise more participants would be single actors which makes no sense.

                // No single input may exceed what one participant can take out (equal output + largest change)
                // plus an allowance for fees. The allowance must be positive: in JoinMarket the taker pays the
                // mining fee and the makers' fees, and makers earn, so the participant holding the largest input
                // is routinely also the one with the largest change. The former "- 0.0001 BTC" margin assumed
                // every participant pays a coordinator and rejected most such transactions.
                && (inputValues.Max() <= mostFrequentEqualOutputValue + outputValues.Where(x => x != mostFrequentEqualOutputValue).Max() + OtherCoinJoinFeeAllowance(mostFrequentEqualOutputValue) // Shortcut instead of an expensive subset sum.

                    // Graph evidence: a tx that already passed every structural rule above and spends outputs of several
                    // known JoinMarket-like coinjoins is a remix, even when its taker paid unusually high fees. Only
                    // replaces the max-input bound; the structural rules stay mandatory. Same lookup as the post-mix check.
                    || tx.Inputs.Select(x => x.OutPoint.Hash).Distinct().Count(isKnownOtherCoinJoin) >= Constants.OtherCoinJoinMinParentCoinJoins);
        }

        public async Task ScanAsync(bool rescan, ulong? fromHeight = null, bool includeTip = false)
        {
            if (rescan)
            {
                Logger.LogWarning("Rescanning...");
            }
            if (rescan && Directory.Exists(WorkFolder))
            {
                Directory.Delete(WorkFolder, true);
            }
            Directory.CreateDirectory(WorkFolder);
            var allWasabi2CoinJoinSet = new HashSet<uint256>();
            var allWasabiCoinJoinSet = new HashSet<uint256>();
            var allSamouraiCoinJoinSet = new HashSet<uint256>();
            var allOtherCoinJoinSet = new HashSet<uint256>();
            var allSamouraiTx0Set = new HashSet<uint256>();

            var opreturnTransactionCache = new MemoryCache(new MemoryCacheOptions() { SizeLimit = 100000 });

            var bestHeight = (ulong)await Rpc.GetBlockCountAsync().ConfigureAwait(false);
            ulong startingHeight = fromHeight ?? Constants.FirstJoinMarketBlock;
            ulong height = includeTip ? NextFrozenScanHeight(startingHeight, bestHeight) : startingHeight;
            if (File.Exists(LastProcessedBlockHeightPath) || File.Exists(CheckpointPath))
            {
                var checkpointHeight = ScanCheckpoint.Restore(CheckpointPath, OutputPaths);
                ScanCheckpoint.WriteAtomically(LastProcessedBlockHeightPath, checkpointHeight.ToString());
                height = includeTip
                    ? NextFrozenScanHeight(startingHeight, bestHeight, checkpointHeight)
                    : checkpointHeight + 1;
                // Only the ids are needed; reading whole lines would hold every input and output in memory.
                allSamouraiCoinJoinSet = Enumerable.ToHashSet(ScanCheckpoint.ReadIds(SamouraiCoinJoinsPath));
                allWasabi2CoinJoinSet = Enumerable.ToHashSet(ScanCheckpoint.ReadIds(Wasabi2CoinJoinsPath));
                allWasabiCoinJoinSet = Enumerable.ToHashSet(ScanCheckpoint.ReadIds(WasabiCoinJoinsPath));
                allOtherCoinJoinSet = Enumerable.ToHashSet(ScanCheckpoint.ReadIds(OtherCoinJoinsPath));
                allSamouraiTx0Set = Enumerable.ToHashSet(ScanCheckpoint.ReadIds(SamouraiTx0sPath));
                Logger.LogWarning($"Checkpoint at {checkpointHeight}. Continue scanning at {height}...");
            }
            else
            {
                ScanCheckpoint.Commit(CheckpointPath, LastProcessedBlockHeightPath, height - 1, OutputPaths);
            }
            if (includeTip && height > bestHeight)
            {
                // A bounded historical chain may end before the first supported
                // CoinJoin. Persist an empty snapshot at its real tip, not at
                // FirstJoinMarketBlock - 1, and allow the next run to advance it.
                foreach (var path in OutputPaths)
                {
                    ScanCheckpoint.Append(path, Array.Empty<string>());
                }
                ScanCheckpoint.Commit(CheckpointPath, LastProcessedBlockHeightPath, bestHeight, OutputPaths);
                Logger.LogInfo($"Frozen scan is complete at {bestHeight}.");
                return;
            }
            var tx0Candidates = new Tx0CandidateStore(Tx0CandidatesPath);
            Logger.LogInfo($"{tx0Candidates.Count} stored tx0 candidates.");

            Logger.LogInfo($"Last processed block: {height - 1}.");
            ulong totalBlocks = bestHeight - height + 1;
            Logger.LogInfo($"About {totalBlocks} ({totalBlocks / 144} days) blocks will be processed.");

            // fsync of nine files per block would dominate a full scan on network storage.
            var pendingBlocks = 0;
            var commitWatch = Stopwatch.StartNew();
            var stopWatch = new Stopwatch();
            var processedBlocksWhenSwStarted = CalculateProcessedBlocks(height, bestHeight, totalBlocks);
            stopWatch.Start();

            while (height <= bestHeight)
            {
                var block = await Rpc.GetVerboseBlockAsync(height, safe: false).ConfigureAwait(false);

                var wasabiCoinJoins = new List<VerboseTransactionInfo>();
                var wasabi2CoinJoins = new List<VerboseTransactionInfo>();
                var samouraiCoinJoins = new List<VerboseTransactionInfo>();
                var samouraiTx0s = new List<VerboseTransactionInfo>();
                var otherCoinJoins = new List<VerboseTransactionInfo>();
                var wasabiPostMixTxs = new List<VerboseTransactionInfo>();
                var wasabi2PostMixTxs = new List<VerboseTransactionInfo>();
                var samouraiPostMixTxs = new List<VerboseTransactionInfo>();
                var otherCoinJoinPostMixTxs = new List<VerboseTransactionInfo>();
                var blockTx0Candidates = new List<VerboseTransactionInfo>();
                var tx0CandidateMisses = new List<string>();

                foreach (var tx in block.Transactions)
                {
                    if (tx.Outputs.Count() > 2 && tx.Outputs.Any(x => TxNullDataTemplate.Instance.CheckScriptPubKey(x.ScriptPubKey)))
                    {
                        opreturnTransactionCache.Set(tx.Id, tx, new MemoryCacheEntryOptions().SetSize(1));
                    }
                    if (block.Height >= Constants.FirstSamouraiBlock && Tx0CandidateStore.IsCandidate(tx))
                    {
                        blockTx0Candidates.Add(tx);
                    }

                    bool isWasabiCj = false;
                    bool isWasabi2Cj = false;
                    bool isSamouraiCj = false;
                    bool isOtherCj = false;
                    if (tx.Inputs.All(x => x.Coinbase is null))
                    {
                        var indistinguishableOutputs = tx.GetIndistinguishableOutputs(includeSingle: false).ToArray();
                        if (indistinguishableOutputs.Any())
                        {
                            var outputs = tx.Outputs.ToArray();
                            var inputs = tx.Inputs.Select(x => x.PrevOutput).ToArray();
                            var outputValues = outputs.Select(x => x.Value);
                            var inputValues = inputs.Select(x => x.Value);
                            var outputCount = outputs.Length;
                            var inputCount = inputs.Length;
                            var isNativeSegwitOnly = tx.Inputs.All(x => x.PrevOutput.ScriptPubKey.IsScriptType(ScriptType.P2WPKH)) && tx.Outputs.All(x => x.ScriptPubKey.IsScriptType(ScriptType.P2WPKH)); // Segwit only outputs.

                            // IDENTIFY WASABI 2 COINJOINS
                            if (block.Height >= Constants.FirstWasabi2Block)
                            {
                                isWasabi2Cj =
                                    tx.Inputs.All(x => x.PrevOutput.ScriptPubKey.IsScriptType(ScriptType.P2WPKH) || x.PrevOutput.ScriptPubKey.IsScriptType(ScriptType.Taproot))
                                    && inputCount >= Constants.MinWW2Inputs
                                    && inputValues.SequenceEqual(inputValues.OrderByDescending(x => x)) // Inputs are ordered descending.
                                    && outputValues.SequenceEqual(outputValues.OrderByDescending(x => x)) // Outputs are ordered descending.
                                    && outputValues.Count(x => Wasabi2Denominations.Contains(x.Satoshi)) > outputCount * 0.8; // Most of the outputs contains the denomination.
                            }

                            // IDENTIFY WASABI COINJOINS
                            if (!isWasabi2Cj && block.Height >= Constants.FirstWasabiBlock)
                            {
                                isWasabiCj = IsWasabiCoinJoin(tx, block.Height, isNativeSegwitOnly, allWasabiCoinJoinSet.Contains);
                            }

                            // IDENTIFY SAMOURAI COINJOINS
                            isSamouraiCj = IsSamouraiCoinJoin(tx, block.Height, tx0Candidates, blockTx0Candidates);
                            if (isSamouraiCj && tx.Inputs.All(x => x.PrevOutput.Value != tx.Outputs.First().Value))
                            {
                                Logger.LogInfo($"Samourai coinjoin {tx.Id} in block {block.Height} has no remix input; every input spends a tx0 candidate.");
                            }

                            // IDENTIFY OTHER EQUAL OUTPUT COINJOIN LIKE TRANSACTIONS
                            if (!isWasabi2Cj && !isWasabiCj && !isSamouraiCj)
                            {
                                isOtherCj = IsOtherCoinJoin(tx, allOtherCoinJoinSet.Contains);
                            }

                            if (isWasabi2Cj)
                            {
                                wasabi2CoinJoins.Add(tx);
                                allWasabi2CoinJoinSet.Add(tx.Id);
                            }
                            else if (isWasabiCj)
                            {
                                wasabiCoinJoins.Add(tx);
                                allWasabiCoinJoinSet.Add(tx.Id);
                            }
                            else if (isSamouraiCj)
                            {
                                samouraiCoinJoins.Add(tx);
                                allSamouraiCoinJoinSet.Add(tx.Id);
                            }
                            else if (isOtherCj)
                            {
                                otherCoinJoins.Add(tx);
                                allOtherCoinJoinSet.Add(tx.Id);
                            }
                        }

                        foreach (var inputTxId in tx.Inputs.Select(x => x.OutPoint.Hash))
                        {
                            if (!isWasabi2Cj && allWasabi2CoinJoinSet.Contains(inputTxId) && !wasabi2PostMixTxs.Any(x => x.Id == tx.Id))
                            {
                                // Then it's a post mix tx.
                                wasabi2PostMixTxs.Add(tx);
                                if (isOtherCj)
                                {
                                    // Then it's false positive detection.
                                    isOtherCj = false;
                                    allOtherCoinJoinSet.Remove(tx.Id);
                                    otherCoinJoins.Remove(tx);
                                }
                            }

                            if (!isWasabiCj && allWasabiCoinJoinSet.Contains(inputTxId) && !wasabiPostMixTxs.Any(x => x.Id == tx.Id))
                            {
                                // Then it's a post mix tx.
                                wasabiPostMixTxs.Add(tx);
                                if (isOtherCj)
                                {
                                    // Then it's false positive detection.
                                    isOtherCj = false;
                                    allOtherCoinJoinSet.Remove(tx.Id);
                                    otherCoinJoins.Remove(tx);
                                }
                            }

                            if (!isSamouraiCj && allSamouraiCoinJoinSet.Contains(inputTxId) && !samouraiPostMixTxs.Any(x => x.Id == tx.Id))
                            {
                                // Then it's a post mix tx.
                                samouraiPostMixTxs.Add(tx);
                                if (isOtherCj)
                                {
                                    // Then it's false positive detection.
                                    isOtherCj = false;
                                    allOtherCoinJoinSet.Remove(tx.Id);
                                    otherCoinJoins.Remove(tx);
                                }
                            }

                            if (!isOtherCj && allOtherCoinJoinSet.Contains(inputTxId) && !otherCoinJoinPostMixTxs.Any(x => x.Id == tx.Id))
                            {
                                // Then it's a post mix tx.
                                otherCoinJoinPostMixTxs.Add(tx);
                            }
                        }
                    }
                }

                foreach (var txid in samouraiCoinJoins.SelectMany(x => x.Inputs).Select(x => x.OutPoint.Hash).Where(x => !allSamouraiCoinJoinSet.Contains(x) && !allSamouraiTx0Set.Contains(x)).Distinct())
                {
                    if (!opreturnTransactionCache.TryGetValue(txid, out VerboseTransactionInfo vtxi)
                        && !tx0Candidates.TryGet(txid, out vtxi))
                    {
                        if (!await HasTxIndexAsync().ConfigureAwait(false))
                        {
                            // Without txindex a parent that is no stored candidate cannot be fetched; it is no tx0 either.
                            Logger.LogWarning($"Parent {txid} of a Samourai coinjoin in block {height} is not a stored tx0 candidate; not recorded as a tx0.");
                            tx0CandidateMisses.Add($"{height} {txid} unresolved");
                            continue;
                        }

                        var tx0 = await Rpc.GetSmartRawTransactionInfoAsync(txid).ConfigureAwait(false);
                        var verboseOutputs = new List<VerboseOutputInfo>(tx0.Transaction.Outputs.Count);
                        foreach (var o in tx0.Transaction.Outputs)
                        {
                            var voi = new VerboseOutputInfo(o.Value, o.ScriptPubKey);
                            verboseOutputs.Add(voi);
                        }

                        var verboseInputs = new List<VerboseInputInfo>(tx0.Transaction.Inputs.Count);
                        foreach (var i in tx0.Transaction.Inputs)
                        {
                            var tx = await Rpc.GetRawTransactionAsync(i.PrevOut.Hash).ConfigureAwait(false);
                            var o = tx.Outputs[i.PrevOut.N];
                            var voi = new VerboseOutputInfo(o.Value, o.ScriptPubKey);
                            var vii = new VerboseInputInfo(i.PrevOut, voi, i.Sequence);
                            verboseInputs.Add(vii);
                        }

                        vtxi = new VerboseTransactionInfo(tx0.TransactionBlockInfo, txid, verboseInputs, verboseOutputs);
                    }

                    if (vtxi is { })
                    {
                        allSamouraiTx0Set.Add(txid);
                        samouraiTx0s.Add(vtxi);

                        if (!Tx0CandidateStore.IsCandidate(vtxi))
                        {
                            Logger.LogWarning($"Tx0 {txid} of a Samourai coinjoin in block {height} does not match the tx0 candidate rule.");
                            tx0CandidateMisses.Add($"{height} {txid} not-candidate");
                        }

                        if (allOtherCoinJoinSet.Contains(txid))
                        {
                            // Then it's false positive detection.
                            // It happens like 10-ish times, so it shouldn't be too expensive to rewrite the file.
                            allOtherCoinJoinSet.Remove(txid);
                            var found = otherCoinJoins.FirstOrDefault(x => x.Id == txid);
                            if (found is { })
                            {
                                otherCoinJoins.Remove(found);
                            }
                            else
                            {
                                // Commit first: a rewrite shifts every later line, so no uncommitted
                                // block may sit behind the checkpoint a crash would restore.
                                ScanCheckpoint.Commit(CheckpointPath, LastProcessedBlockHeightPath, height - 1, OutputPaths);
                                ScanCheckpoint.RemoveTransaction(OtherCoinJoinsPath, txid);
                                // The previous block's checkpoint must not restore the line just removed.
                                ScanCheckpoint.Commit(CheckpointPath, LastProcessedBlockHeightPath, height - 1, OutputPaths);
                                pendingBlocks = 0;
                                commitWatch.Restart();
                            }
                        }
                    }
                }

                decimal totalBlocksPer100 = totalBlocks / 100m;
                ulong processedBlocks = CalculateProcessedBlocks(height, bestHeight, totalBlocks);
                PercentageDone = processedBlocks / totalBlocksPer100;
                bool displayProgress = (PercentageDone - PreviousPercentageDone) >= 0.1m;
                if (displayProgress)
                {
                    var blocksWithinElapsed = processedBlocks - processedBlocksWhenSwStarted;
                    ulong blocksLeft = bestHeight - height;
                    var elapsed = stopWatch.Elapsed;

                    if (blocksWithinElapsed != 0)
                    {
                        var estimatedTimeLeft = (elapsed / blocksWithinElapsed) * blocksLeft;

                        Logger.LogInfo($"Progress: {PercentageDone:0.#}%, Current height: {height}, Estimated time left: {estimatedTimeLeft.TotalHours:0.#} hours.");

                        PreviousPercentageDone = PercentageDone;

                        processedBlocksWhenSwStarted = processedBlocks;
                        stopWatch.Restart();
                    }
                }
                if (!includeTip && bestHeight <= height)
                {
                    // Refresh bestHeight and if still no new block, then end here.
                    bestHeight = (ulong)await Rpc.GetBlockCountAsync().ConfigureAwait(false);
                    if (bestHeight <= height)
                    {
                        break;
                    }
                }

                // Detections first, then (per batch) the checkpoint and the height: a process killed
                // in between leaves extra bytes that the next resume cuts off before re-scanning.
                ScanCheckpoint.Append(Wasabi2CoinJoinsPath, wasabi2CoinJoins.Select(x => RpcParser.ToLine(x)));
                ScanCheckpoint.Append(WasabiCoinJoinsPath, wasabiCoinJoins.Select(x => RpcParser.ToLine(x)));
                ScanCheckpoint.Append(SamouraiCoinJoinsPath, samouraiCoinJoins.Select(x => RpcParser.ToLine(x)));
                ScanCheckpoint.Append(SamouraiTx0sPath, samouraiTx0s.Select(x => RpcParser.ToLine(x)));
                ScanCheckpoint.Append(OtherCoinJoinsPath, otherCoinJoins.Select(x => RpcParser.ToLine(x)));
                ScanCheckpoint.Append(Wasabi2PostMixTxsPath, wasabi2PostMixTxs.Select(x => RpcParser.ToLine(x)));
                ScanCheckpoint.Append(WasabiPostMixTxsPath, wasabiPostMixTxs.Select(x => RpcParser.ToLine(x)));
                ScanCheckpoint.Append(SamouraiPostMixTxsPath, samouraiPostMixTxs.Select(x => RpcParser.ToLine(x)));
                ScanCheckpoint.Append(OtherCoinJoinPostMixTxsPath, otherCoinJoinPostMixTxs.Select(x => RpcParser.ToLine(x)));
                tx0Candidates.Append(blockTx0Candidates);
                ScanCheckpoint.Append(Tx0CandidateMissesPath, tx0CandidateMisses);
                if (++pendingBlocks >= CommitEveryBlocks || commitWatch.Elapsed >= CommitInterval)
                {
                    ScanCheckpoint.Commit(CheckpointPath, LastProcessedBlockHeightPath, height, OutputPaths);
                    pendingBlocks = 0;
                    commitWatch.Restart();
                }

                height++;
            }

            // Live scans leave the tip unsaved. A caller that froze Core may
            // explicitly include it; height then points one past that saved tip.
            if (pendingBlocks > 0)
            {
                ScanCheckpoint.Commit(CheckpointPath, LastProcessedBlockHeightPath, height - 1, OutputPaths);
            }
        }

        internal static ulong NextFrozenScanHeight(ulong startingHeight, ulong bestHeight, ulong? checkpointHeight = null)
        {
            if (checkpointHeight > bestHeight)
            {
                throw new InvalidOperationException("Scanner checkpoint is ahead of the frozen Core tip.");
            }
            var first = Math.Min(startingHeight, bestHeight + 1);
            return checkpointHeight.HasValue ? Math.Max(first, checkpointHeight.Value + 1) : first;
        }

        /// <summary>A pruned node has no txindex, so `getrawtransaction` only finds mempool transactions.</summary>
        private async Task<bool> HasTxIndexAsync()
        {
            if (TxIndex is null)
            {
                var response = await Rpc.SendCommandAsync("getindexinfo", "txindex").ConfigureAwait(false);
                TxIndex = response.Result["txindex"] is { };
            }
            return TxIndex.Value;
        }

        private static IEnumerable<VerboseTransactionInfo> ReadWasabi2CoinJoins()
        {
            return File.ReadAllLines(Wasabi2CoinJoinsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x));
        }

        private static IEnumerable<VerboseTransactionInfo> ReadWasabiCoinJoins()
        {
            return File.ReadAllLines(WasabiCoinJoinsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x));
        }

        private static IEnumerable<VerboseTransactionInfo> ReadSamouraiCoinJoins()
        {
            return File.ReadAllLines(SamouraiCoinJoinsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x));
        }

        private static IEnumerable<VerboseTransactionInfo> ReadOtherCoinJoins()
        {
            return File.ReadAllLines(OtherCoinJoinsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x));
        }

        private static IEnumerable<VerboseTransactionInfo> ReadSamouraiTx0s()
        {
            return File.ReadAllLines(SamouraiTx0sPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x));
        }

        private static IEnumerable<VerboseTransactionInfo> ReadWasabi2PostMixTxs()
        {
            return File.ReadAllLines(Wasabi2PostMixTxsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x));
        }

        private static IEnumerable<VerboseTransactionInfo> ReadWasabiPostMixTxs()
        {
            return File.ReadAllLines(WasabiPostMixTxsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x));
        }

        private static IEnumerable<VerboseTransactionInfo> ReadSamouraiPostMixTxs()
        {
            return File.ReadAllLines(SamouraiPostMixTxsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x));
        }

        private static IEnumerable<VerboseTransactionInfo> ReadOtherCoinJoinPostMixTxs()
        {
            return File.ReadAllLines(OtherCoinJoinPostMixTxsPath).Select(x => RpcParser.VerboseTransactionInfoFromLine(x));
        }

        private static ulong ReadBestHeight()
        {
            return ulong.Parse(File.ReadAllText(LastProcessedBlockHeightPath));
        }

        public static ScannerFiles Load()
        {
            return new ScannerFiles(
                ReadBestHeight(),
                ReadWasabi2CoinJoins(),
                ReadWasabiCoinJoins(),
                ReadSamouraiCoinJoins(),
                ReadOtherCoinJoins(),
                ReadSamouraiTx0s(),
                ReadWasabi2PostMixTxs(),
                ReadWasabiPostMixTxs(),
                ReadSamouraiPostMixTxs(),
                ReadOtherCoinJoinPostMixTxs());
        }

        private static ulong CalculateProcessedBlocks(ulong height, ulong bestHeight, ulong totalBlocks)
        {
            ulong blocksLeft = bestHeight - height;
            ulong processedBlocks = totalBlocks - blocksLeft;
            return processedBlocks;
        }

        private static IOrderedEnumerable<long> CreateWasabi2Denominations()
        {
            long maxSatoshis = 134375000000;
            long minSatoshis = 5000;
            var denominations = new HashSet<long>();

            // Powers of 2
            for (int i = 0; i < int.MaxValue; i++)
            {
                var denom = (long)Math.Pow(2, i);

                if (denom < minSatoshis)
                {
                    continue;
                }

                if (denom > maxSatoshis)
                {
                    break;
                }

                denominations.Add(denom);
            }

            // Powers of 3
            for (int i = 0; i < int.MaxValue; i++)
            {
                var denom = (long)Math.Pow(3, i);

                if (denom < minSatoshis)
                {
                    continue;
                }

                if (denom > maxSatoshis)
                {
                    break;
                }

                denominations.Add(denom);
            }

            // Powers of 3 * 2
            for (int i = 0; i < int.MaxValue; i++)
            {
                var denom = (long)Math.Pow(3, i) * 2;

                if (denom < minSatoshis)
                {
                    continue;
                }

                if (denom > maxSatoshis)
                {
                    break;
                }

                denominations.Add(denom);
            }

            // Powers of 10 (1-2-5 series)
            for (int i = 0; i < int.MaxValue; i++)
            {
                var denom = (long)Math.Pow(10, i);

                if (denom < minSatoshis)
                {
                    continue;
                }

                if (denom > maxSatoshis)
                {
                    break;
                }

                denominations.Add(denom);
            }

            // Powers of 10 * 2 (1-2-5 series)
            for (int i = 0; i < int.MaxValue; i++)
            {
                var denom = (long)Math.Pow(10, i) * 2;

                if (denom < minSatoshis)
                {
                    continue;
                }

                if (denom > maxSatoshis)
                {
                    break;
                }

                denominations.Add(denom);
            }

            // Powers of 10 * 5 (1-2-5 series)
            for (int i = 0; i < int.MaxValue; i++)
            {
                var denom = (long)Math.Pow(10, i) * 5;

                if (denom < minSatoshis)
                {
                    continue;
                }

                if (denom > maxSatoshis)
                {
                    break;
                }

                denominations.Add(denom);
            }

            return denominations.OrderByDescending(x => x);
        }
    }
}
