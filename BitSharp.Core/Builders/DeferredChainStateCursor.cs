﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core.Domain;
using BitSharp.Core.Storage;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BitSharp.Core.Builders
{
    internal class DeferredChainStateCursor : IDeferredChainStateCursor
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        //TODO
        private static readonly DurationMeasure readUtxoDurationMeasure = new DurationMeasure(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));
        private static readonly RateMeasure readUtxoRateMeasure = new RateMeasure(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));
        private static readonly DurationMeasure missedReadUtxoDurationMeasure = new DurationMeasure(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));
        private static readonly RateMeasure missedReadUtxoRateMeasure = new RateMeasure(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5));

        private readonly IChainState chainState;
        private readonly ChainedHeader originalChainTip;

        private DeferredDictionary<UInt256, UnspentTx> unspentTxes;
        private DeferredDictionary<int, IImmutableList<UInt256>> blockSpentTxes;
        private DeferredDictionary<UInt256, IImmutableList<UnmintedTx>> blockUnmintedTxes;

        private bool changesApplied;

        public DeferredChainStateCursor(IChainState chainState)
        {
            this.chainState = chainState;
            this.originalChainTip = chainState.Chain.LastBlock;

            UnspentOutputCount = chainState.UnspentOutputCount;
            UnspentTxCount = chainState.UnspentTxCount;
            TotalTxCount = chainState.TotalTxCount;
            TotalInputCount = chainState.TotalInputCount;
            TotalOutputCount = chainState.TotalOutputCount;

            unspentTxes = new DeferredDictionary<UInt256, UnspentTx>(
                txHash =>
                {
                    UnspentTx unspentTx;
                    return Tuple.Create(TryLoadUnspenTx(txHash, out unspentTx), unspentTx);
                });

            blockSpentTxes = new DeferredDictionary<int, IImmutableList<UInt256>>(
                blockHeight =>
                {
                    IImmutableList<UInt256> spentTxes;
                    return Tuple.Create(chainState.TryGetBlockSpentTxes(blockHeight, out spentTxes), spentTxes);
                });

            blockUnmintedTxes = new DeferredDictionary<UInt256, IImmutableList<UnmintedTx>>(
                blockHash =>
                {
                    IImmutableList<UnmintedTx> unmintedTxes;
                    return Tuple.Create(chainState.TryGetBlockUnmintedTxes(blockHash, out unmintedTxes), unmintedTxes);
                });
        }

        public void Dispose()
        {
            unspentTxes.Dispose();
            blockSpentTxes.Dispose();
            blockUnmintedTxes.Dispose();
        }

        public bool InTransaction
        {
            get { throw new NotSupportedException(); }
        }

        public void BeginTransaction(bool readOnly = false)
        {
            throw new NotSupportedException();
        }

        public void CommitTransaction()
        {
            throw new NotSupportedException();
        }

        public void RollbackTransaction()
        {
            throw new NotSupportedException();
        }

        public IEnumerable<ChainedHeader> ReadChain()
        {
            throw new NotSupportedException();
        }

        public ChainedHeader ChainTip { get; set; }

        public int UnspentTxCount { get; set; }

        public int UnspentOutputCount { get; set; }

        public int TotalTxCount { get; set; }

        public int TotalInputCount { get; set; }

        public int TotalOutputCount { get; set; }

        public bool ContainsUnspentTx(UInt256 txHash)
        {
            return unspentTxes.ContainsKey(txHash);
        }

        public bool TryGetUnspentTx(UInt256 txHash, out UnspentTx unspentTx)
        {
            return unspentTxes.TryGetValue(txHash, out unspentTx);
        }

        public bool TryAddUnspentTx(UnspentTx unspentTx)
        {
            return unspentTxes.TryAdd(unspentTx.TxHash, unspentTx);
        }

        public bool TryRemoveUnspentTx(UInt256 txHash)
        {
            return unspentTxes.TryRemove(txHash);
        }

        public bool TryUpdateUnspentTx(UnspentTx unspentTx)
        {
            return unspentTxes.TryUpdate(unspentTx.TxHash, unspentTx);
        }

        public IEnumerable<UnspentTx> ReadUnspentTransactions()
        {
            throw new NotSupportedException();
        }

        public bool ContainsBlockSpentTxes(int blockIndex)
        {
            return blockSpentTxes.ContainsKey(blockIndex);
        }

        public bool TryGetBlockSpentTxes(int blockIndex, out IImmutableList<UInt256> spentTxes)
        {
            return blockSpentTxes.TryGetValue(blockIndex, out spentTxes);
        }

        public bool TryAddBlockSpentTxes(int blockIndex, IImmutableList<UInt256> spentTxes)
        {
            return blockSpentTxes.TryAdd(blockIndex, spentTxes);
        }

        public bool TryRemoveBlockSpentTxes(int blockIndex)
        {
            return blockSpentTxes.TryRemove(blockIndex);
        }

        public bool ContainsBlockUnmintedTxes(UInt256 blockHash)
        {
            return blockUnmintedTxes.ContainsKey(blockHash);
        }

        public bool TryGetBlockUnmintedTxes(UInt256 blockHash, out IImmutableList<UnmintedTx> unmintedTxes)
        {
            return blockUnmintedTxes.TryGetValue(blockHash, out unmintedTxes);
        }

        public bool TryAddBlockUnmintedTxes(UInt256 blockHash, IImmutableList<UnmintedTx> unmintedTxes)
        {
            return blockUnmintedTxes.TryAdd(blockHash, unmintedTxes);
        }

        public bool TryRemoveBlockUnmintedTxes(UInt256 blockHash)
        {
            return blockUnmintedTxes.TryRemove(blockHash);
        }

        public void Flush()
        {
            throw new NotSupportedException();
        }

        public void Defragment()
        {
            throw new NotSupportedException();
        }

        public void WarmUnspentTx(UInt256 txHash)
        {
            if (unspentTxes.ShouldWarmupValue(txHash))
            {
                UnspentTx unspentTx;
                if (TryLoadUnspenTx(txHash, out unspentTx))
                    unspentTxes.WarmupValue(txHash, unspentTx);
                else
                    unspentTxes.WarmupValue(txHash, null);
            }
        }

        public void ApplyChangesToParent(IChainStateCursor parent)
        {
            if (changesApplied)
                throw new InvalidOperationException();

            var currentChainTip = parent.ChainTip;
            if (!(currentChainTip == null && originalChainTip == null) && currentChainTip.Hash != originalChainTip.Hash)
                throw new InvalidOperationException();

            parent.ChainTip = ChainTip;
            parent.UnspentOutputCount = UnspentOutputCount;
            parent.UnspentTxCount = UnspentTxCount;
            parent.TotalTxCount = TotalTxCount;
            parent.TotalInputCount = TotalInputCount;
            parent.TotalOutputCount = TotalOutputCount;

            foreach (var unspentTx in unspentTxes.Updated.Values)
                if (!parent.TryUpdateUnspentTx(unspentTx))
                    throw new InvalidOperationException();
            foreach (var unspentTx in unspentTxes.Added.Values)
                if (!parent.TryAddUnspentTx(unspentTx))
                    throw new InvalidOperationException();
            foreach (var txHash in unspentTxes.Deleted)
                if (!parent.TryRemoveUnspentTx(txHash))
                    throw new InvalidOperationException();

            if (blockSpentTxes.Updated.Count > 0)
                throw new InvalidOperationException();
            foreach (var spentTxes in blockSpentTxes.Added)
                if (!parent.TryAddBlockSpentTxes(spentTxes.Key, spentTxes.Value))
                    throw new InvalidOperationException();
            foreach (var blockHeight in blockSpentTxes.Deleted)
                if (!parent.TryRemoveBlockSpentTxes(blockHeight))
                    throw new InvalidOperationException();

            if (blockUnmintedTxes.Updated.Count > 0)
                throw new InvalidOperationException();
            foreach (var unmintedTxes in blockUnmintedTxes.Added)
                if (!parent.TryAddBlockUnmintedTxes(unmintedTxes.Key, unmintedTxes.Value))
                    throw new InvalidOperationException();
            foreach (var blockHeight in blockUnmintedTxes.Deleted)
                if (!parent.TryRemoveBlockUnmintedTxes(blockHeight))
                    throw new InvalidOperationException();

            changesApplied = true;
        }

        private bool TryLoadUnspenTx(UInt256 txHash, out UnspentTx unspentTx)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = chainState.TryGetUnspentTx(txHash, out unspentTx);
            stopwatch.Stop();

            if (result)
            {
                readUtxoDurationMeasure.Tick(stopwatch.Elapsed);
                readUtxoRateMeasure.Tick();
            }
            else
            {
                missedReadUtxoDurationMeasure.Tick(stopwatch.Elapsed);
                missedReadUtxoRateMeasure.Tick();
            }

            Throttler.IfElapsed(TimeSpan.FromSeconds(5), () =>
            {
                logger.Info("---------------------------------------------");
                logger.Info("Read UTXO Duration:        {0,12:N3}ms", readUtxoDurationMeasure.GetAverage().TotalMilliseconds);
                logger.Info("Read UTXO Rate:            {0,8:N0}/s", readUtxoRateMeasure.GetAverage());
                logger.Info("Missed Read UTXO Duration: {0,12:N3}ms", missedReadUtxoDurationMeasure.GetAverage().TotalMilliseconds);
                logger.Info("Missed Read UTXO Rate:     {0,8:N0}/s", missedReadUtxoRateMeasure.GetAverage());
                logger.Info("---------------------------------------------");
            });

            return result;
        }
    }
}