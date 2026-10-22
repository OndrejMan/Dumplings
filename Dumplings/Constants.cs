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

        public static readonly uint256[] AshigaruSeedTxIds = new[]
        {
            // 0.025 BTC pool genesis / very first mix
            uint256.Parse("737a867727db9a2c981ad622f2fa14b021ce8b1066a001e34fb793f8da833155"),

            // 0.25 BTC pool genesis / very first mix
            uint256.Parse("7784df1182ab86ee33577b75109bb0f7c5622b9fb91df24b65ab2ab01b27dffa")
        };
    }
}
