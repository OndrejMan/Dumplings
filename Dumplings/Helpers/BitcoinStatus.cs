using NBitcoin.RPC;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Dumplings.Helpers
{
    public static class BitcoinStatus
    {
        public static async Task CheckAsync(RPCClient client, ulong? frozenHeight = null)
        {
            Logger.LogInfo("Checking Bitcoin sync status...");

            var bci = await client.GetBlockchainInfoAsync();

            if (frozenHeight.HasValue)
            {
                var network = await client.SendCommandAsync("getnetworkinfo");
                ValidateFrozenChain((ulong)bci.Blocks, frozenHeight.Value, network.Result["networkactive"].ToObject<bool>());
                Logger.LogInfo($"Bitcoin is frozen at height {bci.Blocks}.");
                return;
            }

            var missingBlocks = bci.Headers - bci.Blocks;
            if (missingBlocks != 0)
            {
                throw new InvalidOperationException($"Bitcoin is not synchronized. Blocks missing: {missingBlocks}.");
            }

            Logger.LogInfo($"Bitcoin is synchronized. Current height: {bci.Blocks}.");
        }

        internal static void ValidateFrozenChain(ulong blocks, ulong expectedHeight, bool networkActive)
        {
            if (networkActive || blocks != expectedHeight)
            {
                throw new InvalidOperationException("Frozen scan requires disabled networking and the exact Core height.");
            }
        }
    }
}
