using NBitcoin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Dumplings.Scanning
{
    /// <summary>
    /// Streaming, crash-safe bookkeeping for the append-only Scanner files, which reach gigabytes on mainnet.
    /// After every block the scanner records the height and the length of each output file. A
    /// process killed before the next commit leaves extra bytes behind, which <see cref="Restore"/> cuts off
    /// before those blocks are scanned again, so no detection is lost and none is written twice.
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

        /// <summary>Appends lines; <see cref="Commit"/> makes them durable.</summary>
        public static void Append(string path, IEnumerable<string> lines)
        {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var line in lines)
            {
                writer.Write(line);
                writer.Write('\n');
            }
        }

        /// <summary>
        /// Flushes every file to disk, then records the checkpoint and the height. Until the next
        /// commit a crash only costs re-scanning the blocks since this one.
        /// </summary>
        public static void Commit(string checkpointPath, string heightPath, ulong height, IReadOnlyCollection<string> paths)
        {
            foreach (var path in paths.Where(File.Exists))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                stream.Flush(flushToDisk: true);
            }
            WriteCheckpoint(checkpointPath, height, paths);
            WriteAtomically(heightPath, height.ToString());
        }

        /// <summary>Replaces a file so that readers see either the old or the new content.</summary>
        public static void WriteAtomically(string path, string content)
        {
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }

        /// <summary>Records that every file holds exactly the detections up to and including <paramref name="height"/>.</summary>
        public static void WriteCheckpoint(string checkpointPath, ulong height, IEnumerable<string> paths)
        {
            var content = new StringBuilder();
            content.Append(height).Append('\n');
            foreach (var path in paths)
            {
                var length = File.Exists(path) ? new FileInfo(path).Length : 0;
                content.Append(Path.GetFileName(path)).Append(' ').Append(length).Append('\n');
            }
            WriteAtomically(checkpointPath, content.ToString());
        }

        /// <summary>
        /// Cuts every file back to its recorded length and returns the recorded height.
        /// </summary>
        public static ulong Restore(string checkpointPath, IEnumerable<string> paths)
        {
            var lines = File.ReadAllLines(checkpointPath);
            var height = ulong.Parse(lines[0]);
            var lengths = lines.Skip(1)
                .Select(x => x.Split(' '))
                .ToDictionary(x => x[0], x => long.Parse(x[1]));

            foreach (var path in paths)
            {
                if (!lengths.TryGetValue(Path.GetFileName(path), out var length) || !File.Exists(path))
                {
                    continue;
                }

                // A file can only be shorter after a rewrite that dropped a false positive; it is kept as is.
                if (new FileInfo(path).Length > length)
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
                    stream.SetLength(length);
                    stream.Flush(flushToDisk: true);
                }
            }
            return height;
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
