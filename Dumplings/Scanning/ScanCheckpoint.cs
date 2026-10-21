using NBitcoin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Dumplings.Scanning
{
    /// <summary>
    /// Streaming access to the append-only Scanner files, which reach gigabytes on mainnet.
    /// </summary>
    public static class ScanCheckpoint
    {
        private const string LineSeparator = ":::";

        /// <summary>
        /// Streams the transaction ids of a Scanner file without materialising its lines.
        /// A resume needs only the ids, while a line holds every input and output of the transaction.
        /// </summary>
        public static IEnumerable<uint256> ReadIds(string path)
        {
            if (!File.Exists(path))
            {
                yield break;
            }

            foreach (var line in File.ReadLines(path))
            {
                var end = line.IndexOf(LineSeparator, StringComparison.Ordinal);
                if (end > 0)
                {
                    yield return uint256.Parse(line.Substring(0, end));
                }
            }
        }

        /// <summary>Removes the line of one transaction without loading the whole file into memory.</summary>
        public static void RemoveTransaction(string path, uint256 txid)
        {
            var prefix = txid + LineSeparator;
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                {
                    foreach (var line in File.ReadLines(path))
                    {
                        if (!line.StartsWith(prefix, StringComparison.Ordinal))
                        {
                            writer.Write(line);
                            writer.Write('\n');
                        }
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
    }
}
