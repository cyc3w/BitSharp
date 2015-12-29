﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core.Domain;
using BitSharp.Core.Rules;
using BitSharp.Core.Script;
using BitSharp.Core.Storage;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace BitSharp.Core.Builders
{
    internal static class BlockValidator
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public static async Task ValidateBlockAsync(ICoreStorage coreStorage, IBlockchainRules rules, Chain chain, ChainedHeader chainedHeader, ISourceBlock<ValidatableTx> validatableTxes, CancellationToken cancelToken = default(CancellationToken))
        {
            // capture fees
            Transaction coinbaseTx = null;
            var txCount = 0U;
            var totalTxInputValue = 0UL;
            var totalTxOutputValue = 0UL;
            var totalSigOpCount = 0;
            var blockSize = 80;

            //TODO
            var MAX_BLOCK_SIZE = 1.MILLION(); // :(
            var MAX_MONEY = (ulong)21.MILLION() * (ulong)100.MILLION();

            // BIP16 didn't become active until Apr 1 2012
            var nBIP16SwitchTime = 1333238400U;
            var strictPayToScriptHash = chainedHeader.Time >= nBIP16SwitchTime;

            var feeCapturer = new TransformBlock<ValidatableTx, ValidatableTx>(
                validatableTx =>
                {
                    var tx = validatableTx.Transaction;

                    // first transaction must be coinbase
                    if (txCount == 0 && !tx.IsCoinbase)
                        throw new ValidationException(chainedHeader.Hash);
                    // all other transactions must not be coinbase
                    else if (txCount > 0 && tx.IsCoinbase)
                        throw new ValidationException(chainedHeader.Hash);

                    // must have inputs
                    if (tx.Inputs.Length == 0)
                        throw new ValidationException(chainedHeader.Hash);
                    // must have outputs
                    else if (tx.Outputs.Length == 0)
                        throw new ValidationException(chainedHeader.Hash);
                    // must not have any negative money value outputs
                    else if (tx.Outputs.Any(x => unchecked((long)x.Value) < 0))
                        throw new ValidationException(chainedHeader.Hash);
                    // must not have any outputs with a value greater than max money
                    else if (tx.Outputs.Any(x => x.Value > MAX_MONEY))
                        throw new ValidationException(chainedHeader.Hash);
                    // must not have a total output value greater than max money
                    else if (tx.Outputs.Sum(x => x.Value) > MAX_MONEY)
                        throw new ValidationException(chainedHeader.Hash);
                    // coinbase scriptSignature length must be >= 2 && <= 100
                    else if (tx.IsCoinbase && (tx.Inputs[0].ScriptSignature.Length < 2 || tx.Inputs[0].ScriptSignature.Length > 100))
                        throw new ValidationException(chainedHeader.Hash);
                    // non-coinbase txes must not have coinbase prev tx output key (txHash: 0, outputIndex: -1)
                    else if (!tx.IsCoinbase && tx.Inputs.Any(x => x.PreviousTxOutputKey.TxOutputIndex == uint.MaxValue && x.PreviousTxOutputKey.TxHash == UInt256.Zero))
                        throw new ValidationException(chainedHeader.Hash);

                    txCount++;
                    if (tx.IsCoinbase)
                    {
                        coinbaseTx = tx;
                    }
                    else
                    {
                        totalTxInputValue += validatableTx.PrevTxOutputs.Sum(x => x.Value);
                        totalTxOutputValue += tx.Outputs.Sum(x => x.Value);
                    }

                    totalSigOpCount += tx.Inputs.Sum(x => CountLegacySigOps(x.ScriptSignature, accurate: false));
                    totalSigOpCount += tx.Outputs.Sum(x => CountLegacySigOps(x.ScriptPublicKey, accurate: false));
                    if (!tx.IsCoinbase && strictPayToScriptHash)
                        totalSigOpCount += tx.Inputs.Sum(x => CountP2SHSigOps(validatableTx));

                    //TODO
                    var MAX_BLOCK_SIGOPS = 20.THOUSAND();
                    if (totalSigOpCount > MAX_BLOCK_SIGOPS)
                        throw new ValidationException(chainedHeader.Hash);

                    //TODO should be optimal encoding length
                    blockSize += validatableTx.TxBytes.Length;

                    //TODO
                    if (blockSize + DataEncoder.VarIntSize(txCount) > MAX_BLOCK_SIZE)
                        throw new ValidationException(chainedHeader.Hash);

                    return validatableTx;
                });

            validatableTxes.LinkTo(feeCapturer, new DataflowLinkOptions { PropagateCompletion = true });

            // validate transactions
            var txValidator = InitTxValidator(rules, chainedHeader, cancelToken);

            // begin feeding the tx validator
            feeCapturer.LinkTo(txValidator, new DataflowLinkOptions { PropagateCompletion = true });

            // validate scripts
            var scriptValidator = InitScriptValidator(rules, chainedHeader, cancelToken);

            // begin feeding the script validator
            txValidator.LinkTo(scriptValidator, new DataflowLinkOptions { PropagateCompletion = true });

            //TODO
            await PipelineCompletion.Create(
                new[] { validatableTxes.Completion, feeCapturer.Completion, txValidator.Completion, scriptValidator.Completion },
                new IDataflowBlock[] { validatableTxes, feeCapturer, txValidator, scriptValidator });

            // validate overall block
            rules.PostValidateBlock(chain, chainedHeader, coinbaseTx, totalTxInputValue, totalTxOutputValue);
        }

        private static TransformManyBlock<ValidatableTx, Tuple<ValidatableTx, int>> InitTxValidator(IBlockchainRules rules, ChainedHeader chainedHeader, CancellationToken cancelToken)
        {
            return new TransformManyBlock<ValidatableTx, Tuple<ValidatableTx, int>>(
                validatableTx =>
                {
                    rules.ValidateTransaction(chainedHeader, validatableTx);

                    if (!rules.IgnoreScripts && !validatableTx.IsCoinbase)
                    {
                        var tx = validatableTx.Transaction;

                        var scripts = new Tuple<ValidatableTx, int>[tx.Inputs.Length];
                        for (var i = 0; i < tx.Inputs.Length; i++)
                            scripts[i] = Tuple.Create(validatableTx, i);

                        return scripts;
                    }
                    else
                        return new Tuple<ValidatableTx, int>[0];
                },
                new ExecutionDataflowBlockOptions { CancellationToken = cancelToken, MaxDegreeOfParallelism = Environment.ProcessorCount });
        }

        private static ActionBlock<Tuple<ValidatableTx, int>> InitScriptValidator(IBlockchainRules rules, ChainedHeader chainedHeader, CancellationToken cancelToken)
        {
            return new ActionBlock<Tuple<ValidatableTx, int>>(
                tuple =>
                {
                    var validatableTx = tuple.Item1;
                    var inputIndex = tuple.Item2;
                    var tx = validatableTx.Transaction;
                    var txInput = tx.Inputs[inputIndex];
                    var prevTxOutputs = validatableTx.PrevTxOutputs[inputIndex];

                    if (!rules.IgnoreScriptErrors)
                    {
                        rules.ValidationTransactionScript(chainedHeader, validatableTx.BlockTx, txInput, inputIndex, prevTxOutputs);
                    }
                    else
                    {
                        try
                        {
                            rules.ValidationTransactionScript(chainedHeader, validatableTx.BlockTx, txInput, inputIndex, prevTxOutputs);
                        }
                        catch (Exception ex)
                        {
                            var aggEx = ex as AggregateException;
                            logger.Debug($"Ignoring script errors in block: {chainedHeader.Height,9:N0}, errors: {(aggEx?.InnerExceptions.Count ?? -1):N0}");
                        }
                    }
                },
                new ExecutionDataflowBlockOptions { CancellationToken = cancelToken, MaxDegreeOfParallelism = Environment.ProcessorCount });
        }

        //TODO - hasn't been checked for correctness, should also be moved
        private static int CountLegacySigOps(ImmutableArray<byte> script, bool accurate)
        {
            var sigOpCount = 0;

            var index = 0;
            while (index < script.Length)
            {
                ScriptOp op;
                if (!GetOp(script, ref index, out op))
                    break;

                switch (op)
                {
                    case ScriptOp.OP_CHECKSIG:
                    case ScriptOp.OP_CHECKSIGVERIFY:
                        sigOpCount++;
                        break;

                    case ScriptOp.OP_CHECKMULTISIG:
                    case ScriptOp.OP_CHECKMULTISIGVERIFY:
                        //TODO
                        var MAX_PUBKEYS_PER_MULTISIG = 20;
                        var prevOpCode = index >= 2 ? script[index - 2] : (byte)ScriptOp.OP_INVALIDOPCODE;
                        if (accurate && prevOpCode >= (byte)ScriptOp.OP_1 && prevOpCode <= (byte)ScriptOp.OP_16)
                            sigOpCount += prevOpCode;
                        else
                            sigOpCount += MAX_PUBKEYS_PER_MULTISIG;

                        break;
                }
            }

            return sigOpCount;
        }

        //TODO - hasn't been checked for correctness, should also be moved
        private static int CountP2SHSigOps(ValidatableTx validatableTx)
        {
            var sigOpCount = 0;

            if (validatableTx.IsCoinbase)
                return 0;

            for (var inputIndex = 0; inputIndex < validatableTx.Transaction.Inputs.Length; inputIndex++)
            {
                sigOpCount += CountP2SHSigOps(validatableTx, inputIndex);
            }

            return sigOpCount;
        }

        //TODO - hasn't been checked for correctness, should also be moved
        private static int CountP2SHSigOps(ValidatableTx validatableTx, int inputIndex)
        {
            var prevTxOutput = validatableTx.PrevTxOutputs[inputIndex];
            if (prevTxOutput.IsPayToScriptHash())
            {
                var script = validatableTx.Transaction.Inputs[inputIndex].ScriptSignature;

                ImmutableArray<byte>? data = null;

                var index = 0;
                while (index < script.Length)
                {
                    ScriptOp op;
                    if (!GetOp(script, ref index, out op, out data))
                        return 0;
                    else if (op > ScriptOp.OP_16)
                        return 0;
                }

                if (data == null)
                    return 0;

                return CountLegacySigOps(data.Value, true);
            }
            else
                return 0;
        }

        private static bool GetOp(ImmutableArray<byte> script, ref int index, out ScriptOp op)
        {
            ImmutableArray<byte>? data;
            return GetOp(script, ref index, out op, out data, readData: false);
        }

        private static bool GetOp(ImmutableArray<byte> script, ref int index, out ScriptOp op, out ImmutableArray<byte>? data)
        {
            return GetOp(script, ref index, out op, out data, readData: true);
        }

        //TODO - hasn't been checked for correctness, should also be moved
        private static bool GetOp(ImmutableArray<byte> script, ref int index, out ScriptOp op, out ImmutableArray<byte>? data, bool readData)
        {
            op = ScriptOp.OP_INVALIDOPCODE;
            data = null;

            if (index + 1 > script.Length)
                return false;

            var opByte = script[index++];
            var currentOp = (ScriptOp)opByte;

            if (currentOp <= ScriptOp.OP_PUSHDATA4)
            {
                //OP_PUSHBYTES1-75
                uint dataLength;
                if (currentOp < ScriptOp.OP_PUSHDATA1)
                {
                    dataLength = opByte;
                }
                else if (currentOp == ScriptOp.OP_PUSHDATA1)
                {
                    if (index + 1 > script.Length)
                        return false;

                    dataLength = script[index++];
                }
                else if (currentOp == ScriptOp.OP_PUSHDATA2)
                {
                    if (index + 2 > script.Length)
                        return false;

                    dataLength = (uint)script[index++] + ((uint)script[index++] << 8);
                }
                else if (currentOp == ScriptOp.OP_PUSHDATA4)
                {
                    if (index + 4 > script.Length)
                        return false;

                    dataLength = (uint)script[index++] + ((uint)script[index++] << 8) + ((uint)script[index++] << 16) + ((uint)script[index++] << 24);
                }
                else
                {
                    dataLength = 0;
                    Debug.Assert(false);
                }

                if ((ulong)index + dataLength > (uint)script.Length)
                    return false;

                if (readData)
                    data = ImmutableArray.Create(script, index, (int)dataLength);

                index += (int)dataLength;
            }

            op = currentOp;
            return true;
        }
    }
}
