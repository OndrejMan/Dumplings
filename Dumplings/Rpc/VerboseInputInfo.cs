using NBitcoin;
using System;

namespace Dumplings.Rpc
{
    public class VerboseInputInfo
    {
        public VerboseInputInfo(string coinbase)
        {
            Coinbase = coinbase;
        }

        public VerboseInputInfo(OutPoint outPoint, VerboseOutputInfo prevOutput, uint sequence)
        {
            OutPoint = outPoint;
            PrevOutput = prevOutput;
            Sequence = sequence;
        }

        public OutPoint OutPoint { get; }

        public VerboseOutputInfo PrevOutput { get; }

        public uint Sequence { get; }

        public string Coinbase { get; }

        private const string Separator = "-";

        public override string ToString()
        {
            if (Coinbase is { })
            {
                return $"coinbase{Separator}{Coinbase}";
            }

            return $"{OutPoint.Hash}{Separator}{OutPoint.N}{Separator}{PrevOutput}{Separator}{Sequence}";
        }

        internal static VerboseInputInfo FromString(string x)
        {
            var parts = x.Split(Separator, StringSplitOptions.None);

            if (parts[0] == "coinbase")
            {
                return new VerboseInputInfo(parts[1]);
            }

            var hash = uint256.Parse(parts[0]);
            var n = uint.Parse(parts[1]);
            var po = parts[2] is null ? null : VerboseOutputInfo.FromString(parts[2]);

            var seq = 0u;
            if (parts.Length > 3 && parts[3] is null)
            {
                seq = uint.Parse(parts[3]);
            }

            return new VerboseInputInfo(new OutPoint(hash, n), po, seq);
        }
    }
}
