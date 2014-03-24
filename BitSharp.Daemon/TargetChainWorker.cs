﻿using BitSharp.Blockchain;
using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Data;
using BitSharp.Storage;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BitSharp.Daemon
{
    public class TargetChainWorker : Worker
    {
        public event EventHandler<ChainedBlock> OnWinningBlockChanged;

        private readonly IBlockchainRules rules;
        private readonly ICacheContext cacheContext;

        private readonly TargetBlockWorker targetBlockWorker;
        private ChainedBlocks targetChainedBlocks;

        public TargetChainWorker(IBlockchainRules rules, ICacheContext cacheContext, bool initialNotify, TimeSpan minIdleTime, TimeSpan maxIdleTime)
            : base("TargetChainWorker", initialNotify, minIdleTime, maxIdleTime)
        {
            this.rules = rules;
            this.cacheContext = cacheContext;

            this.targetBlockWorker = new TargetBlockWorker(cacheContext, initialNotify: true, minIdleTime: TimeSpan.Zero, maxIdleTime: TimeSpan.MaxValue);

            this.targetBlockWorker.OnTargetBlockChanged += NotifyWork;
        }

        protected override void SubDispose()
        {
            // cleanup events
            this.targetBlockWorker.OnTargetBlockChanged -= NotifyWork;

            // cleanup workers
            this.targetBlockWorker.Dispose();
        }

        public ICacheContext CacheContext { get { return this.cacheContext; } }

        public ChainedBlocks TargetChainedBlocks { get { return this.targetChainedBlocks; } }

        public ChainedBlock WinningBlock { get { return this.targetBlockWorker.TargetBlock; } }

        internal TargetBlockWorker TargetBlockWorker { get { return this.targetBlockWorker; } }

        protected override void SubStart()
        {
            this.targetBlockWorker.Start();
        }

        protected override void SubStop()
        {
            this.targetBlockWorker.Stop();
        }

        protected override void WorkAction()
        {
            try
            {
                var targetBlockLocal = this.targetBlockWorker.TargetBlock;
                var targetChainedBlocksLocal = this.targetChainedBlocks;

                if (targetBlockLocal != null &&
                    (targetChainedBlocksLocal == null || targetBlockLocal.BlockHash != targetChainedBlocksLocal.LastBlock.BlockHash))
                {
                    var newTargetChainedBlocks =
                        targetChainedBlocksLocal != null
                        ? targetChainedBlocksLocal.ToBuilder()
                        : new ChainedBlocksBuilder(ChainedBlocks.CreateForGenesisBlock(this.rules.GenesisChainedBlock));

                    var deltaBlockPath = new MethodTimer(false).Time("deltaBlockPath", () =>
                        new BlockchainWalker().GetBlockchainPath(newTargetChainedBlocks.LastBlock, targetBlockLocal, blockHash => this.CacheContext.ChainedBlockCache[blockHash]));

                    foreach (var rewindBlock in deltaBlockPath.RewindBlocks)
                        newTargetChainedBlocks.RemoveBlock(rewindBlock);

                    var invalid = false;
                    foreach (var advanceBlock in deltaBlockPath.AdvanceBlocks)
                    {
                        if (this.cacheContext.InvalidBlockCache.ContainsKey(advanceBlock.BlockHash))
                            invalid = true;

                        if (!invalid)
                            newTargetChainedBlocks.AddBlock(advanceBlock);
                        else
                            this.cacheContext.InvalidBlockCache.TryAdd(advanceBlock.BlockHash, "");
                    }

                    //Debug.WriteLine("Winning chained block {0} at height {1}, total work: {2}".Format2(targetBlock.BlockHash.ToHexNumberString(), targetBlock.Height, targetBlock.TotalWork.ToString("X")));
                    this.targetChainedBlocks = newTargetChainedBlocks.ToImmutable();

                    var handler = this.OnWinningBlockChanged;
                    if (handler != null)
                        handler(this, targetBlockLocal);
                }
            }
            catch (MissingDataException) { }
        }
    }
}
