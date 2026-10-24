using Dumplings.Rpc;
using NBitcoin;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Dumplings.Scanning
{
    /// <summary>
    /// Transactions that may be the tx0 of a later Samourai coinjoin, kept in an append-only Scanner file.
    /// Resolving a tx0 from here instead of `getrawtransaction` removes the need for Bitcoin Core's txindex,
    /// which a pruned node cannot have. Only the byte offset of each line stays in memory.
    /// </summary>
    public class Tx0CandidateStore
    {
        /// <summary>The premix output pays the mixing fee on top of the pool size; same margin as the coinjoin detection.</summary>
        private static readonly Money MaxPremixFee = Money.Coins(0.0011m);

        /// <summary>
        /// Indexing a 500 MB file of 500 000 candidates took 1.72 s with 64 KiB, against 1.85 s with
        /// the 4 KiB default and 1.76 s with 1 MiB (page cache warm, median of five runs).
        /// </summary>
        private const int IndexReadBufferBytes = 64 * 1024;

        private readonly Dictionary<uint256, long> _offsets = new();

        /// <summary>Indexes an existing file, which must end on a line boundary (see <see cref="ScanCheckpoint.Restore"/>).</summary>
        public Tx0CandidateStore(string path)
        {
            Path = path;
            if (!File.Exists(path))
            {
                return;
            }

            // A line starts with its 64 hex txid; anything shorter is not a transaction line.
            using var stream = new BufferedStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), IndexReadBufferBytes);
            var id = new char[64];
            long position = 0;
            long lineStart = 0;
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (b == '\n')
                {
                    if (position - lineStart >= id.Length)
                    {
                        _offsets[uint256.Parse(new string(id))] = lineStart;
                    }
                    lineStart = position + 1;
                }
                else if (position - lineStart < id.Length)
                {
                    id[position - lineStart] = (char)b;
                }
                position++;
            }
        }

        public string Path { get; }

        public int Count => _offsets.Count;

        /// <summary>
        /// Whirlpool and Ashigaru tx0s carry an OP_RETURN and at least one premix output slightly above a pool size.
        /// </summary>
        public static bool IsCandidate(VerboseTransactionInfo tx)
        {
            return tx.Outputs.Count() > 2
                && tx.Outputs.Any(x => TxNullDataTemplate.Instance.CheckScriptPubKey(x.ScriptPubKey))
                && tx.Outputs.Any(x => Constants.SamouraiPools.Any(pool => x.Value >= pool && x.Value <= pool + MaxPremixFee));
        }

        /// <summary>Appends lines like <see cref="ScanCheckpoint.Append"/>; its <see cref="ScanCheckpoint.Commit"/> makes them durable.</summary>
        public void Append(IEnumerable<VerboseTransactionInfo> txs)
        {
            using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            var encoding = new UTF8Encoding(false);
            foreach (var tx in txs)
            {
                var offset = stream.Position;
                var bytes = encoding.GetBytes(RpcParser.ToLine(tx) + "\n");
                stream.Write(bytes, 0, bytes.Length);
                _offsets[tx.Id] = offset;
            }
        }

        public bool TryGet(uint256 txid, out VerboseTransactionInfo tx)
        {
            if (!_offsets.TryGetValue(txid, out var offset))
            {
                tx = null;
                return false;
            }

            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            tx = RpcParser.VerboseTransactionInfoFromLine(reader.ReadLine());
            return true;
        }
    }
}
