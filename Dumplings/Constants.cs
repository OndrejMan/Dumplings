using NBitcoin;
using System;
using System.Collections.Generic;
using System.Text;

namespace Dumplings
{
    public static class Constants
    {
        /// <summary>
        /// January 09, 2015 Bitcointalk announcement - https://bitcointalk.org/index.php?topic=919116.msg10096718
        /// </summary>
        public const ulong FirstJoinMarketBlock = 336861;  // block significantly before first known JoinMarket tx from 2015-05-07

        /// <summary>
        /// Fee allowance for the max-input check of JoinMarket-like coinjoins:
        /// max(OtherCoinJoinMinFeeAllowance, equal output value * OtherCoinJoinFeeAllowancePerMille / 1000).
        /// </summary>
        public static readonly Money OtherCoinJoinMinFeeAllowance = Money.Coins(0.001m);

        public const long OtherCoinJoinFeeAllowancePerMille = 5; // 0.5 % of the denomination

        /// <summary>
        /// Minimum number of equal outputs of a JoinMarket-like coinjoin (taker + at least two makers).
        /// </summary>
        public const int OtherCoinJoinMinEqualOutputs = 3;

        /// <summary>
        /// Number of distinct already-detected JoinMarket-like coinjoins a structurally valid tx must spend from
        /// to be accepted even when it exceeds the max-input bound (e.g. a taker paying high fees).
        /// </summary>
        public const int OtherCoinJoinMinParentCoinJoins = 2;

        /// <summary>
        /// July 06, 2018 Twitter announcement - https://twitter.com/wasabiwallet/status/1537911130718228480
        /// </summary>
        public const ulong FirstWasabi2Block = 741213;

        /// <summary>
        /// July 06, 2018 Reddit announcement - https://old.reddit.com/r/Bitcoin/comments/8wk63k/wasabi_privacy_focused_bitcoin_wallet_for_desktop/
        /// </summary>
        public const ulong FirstWasabiBlock = 530500;

        /// <summary>
        /// June 17, 2019 Reddit announcement - https://old.reddit.com/r/Bitcoin/comments/c1niri/samourai_whirlpool_coinjoin_implementation_is_now/
        /// </summary>
        public const ulong FirstSamouraiBlock = 570000;

        public const ulong FirstWasabiNoCoordAddressBlock = 610000;

        /// <summary>
        /// 50 was the minimum input count at the beginning of Wasabi 2. (2022-2024) and with at least 10 equal outputs.. 
        /// Post-zkSNACKs coordinators are sometimes smaller
        /// </summary>
        public const int MinWW2Inputs = 15;
        public const int MinWW2EqualOutputs = 10;

        public static IEnumerable<Script> WasabiCoordScripts = new Script[]
        {
            BitcoinAddress.Create("bc1qs604c7jv6amk4cxqlnvuxv26hv3e48cds4m0ew", Network.Main).ScriptPubKey,
            BitcoinAddress.Create("bc1qa24tsgchvuxsaccp8vrnkfd85hrcpafg20kmjw", Network.Main).ScriptPubKey
        };

        public static Money ApproximateWasabiBaseDenomination = Money.Coins(0.1m);
        public static Money WasabiBaseDenominationPrecision = Money.Coins(0.02m);

        public static IEnumerable<Money> SamouraiPools = new Money[]
        {
            Money.Coins(0.001m), // Samourai pools
            Money.Coins(0.01m),  // Samourai pools
            Money.Coins(0.05m),  // Samourai pools
            Money.Coins(0.5m),   // Samourai pools
            Money.Coins(0.25m),  // Ashigaru pools  
            Money.Coins(0.025m)  // Ashigaru pools

        };

        /// <summary>
        /// Height of the first coinjoin of each Whirlpool pool, in satoshis. These are the funding transactions from which
        /// coinjoin-analysis seeds its pools (cj_consts.WHIRLPOOL_FUNDING_TXS). Before that height an equal-output transaction
        /// of the pool size cannot be a round of that pool: the Ashigaru pools started in 2025 (PETS 2026 paper, Sect. 2.1),
        /// yet 0.25 BTC look-alikes exist from 2021.
        /// </summary>
        public static readonly IReadOnlyDictionary<long, ulong> SamouraiPoolFirstBlocks = new Dictionary<long, ulong>
        {
            [Money.Coins(0.05m).Satoshi] = 572030,  // a554db794560458c102bab0af99773883df13bc66ad287c29610ad9bac138926, 2019-04-17
            [Money.Coins(0.01m).Satoshi] = 577445,  // 4c906f89…, a4259682…, c6c27bef…, 2019-05-23
            [Money.Coins(0.5m).Satoshi] = 588252,   // b42df707a3d876b24a22b0199e18dc39aba2eafa6dbeaaf9dd23d925bb379c59, 2019-08-02
            [Money.Coins(0.001m).Satoshi] = 673322, // ac9566a240a5e037471b1a58ea50206062c13e1a75c0c2de3f21c7053573330a, 2021-03-05
            [Money.Coins(0.025m).Satoshi] = 899205, // AshigaruSeedTxIds[0], 2025-05-31
            [Money.Coins(0.25m).Satoshi] = 899973,  // AshigaruSeedTxIds[1], 2025-06-06
        };

        /// <summary>Whether a Whirlpool round of this pool size can exist at this height.</summary>
        public static bool IsSamouraiPool(Money poolSize, ulong height)
            => SamouraiPoolFirstBlocks.TryGetValue(poolSize.Satoshi, out var firstBlock) && height >= firstBlock;

        public static readonly uint256[] AshigaruSeedTxIds = new[]
        {
            // 0.025 BTC pool genesis / very first mix
            uint256.Parse("737a867727db9a2c981ad622f2fa14b021ce8b1066a001e34fb793f8da833155"),

            // 0.25 BTC pool genesis / very first mix
            uint256.Parse("7784df1182ab86ee33577b75109bb0f7c5622b9fb91df24b65ab2ab01b27dffa")
        };
    }
}
