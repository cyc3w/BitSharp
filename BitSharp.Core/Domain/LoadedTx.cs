﻿using BitSharp.Common.ExtensionMethods;
using System;
using System.Collections.Immutable;

namespace BitSharp.Core.Domain
{
    /// <summary>
    /// Represents a fully loaded transaction, with each input's previous transaction included.
    /// </summary>
    public class LoadedTx
    {
        /// <summary>
        /// Initializes a new instance of <see cref="LoadedTx"/> with the specified transaction and each input's previous transaction.
        /// </summary>
        /// <param name="transaction">The transaction.</param>
        /// <param name="txIndex">The index of the transaction.</param>
        /// <param name="inputTxes">The array of transactions corresponding to each input's previous transaction.</param>
        public LoadedTx(Transaction transaction, int txIndex, ImmutableArray<DecodedTx> inputTxes)
        {
            Transaction = transaction;
            TxIndex = txIndex;
            InputTxes = inputTxes;
            if (TxIndex == 0)
            {
                if (inputTxes.Length != 0)
                    throw new InvalidOperationException($"Coinbase InputTxes.Length: {inputTxes.Length}");
            }
            else
            {
                if (inputTxes.Length != transaction.Inputs.Length)
                    throw new InvalidOperationException($"Transaction.Inputs.Length: {transaction.Inputs.Length}, InputTxes.Length: {inputTxes.Length}");
            }
        }

        /// <summary>
        /// Indicates whether this is the coinbase transaction.
        /// </summary>
        public bool IsCoinbase => Transaction.IsCoinbase;

        /// <summary>
        /// Gets the transaction.
        /// </summary>
        public Transaction Transaction { get; }

        /// <summary>
        /// Gets the index of the transaction.
        /// </summary>
        public int TxIndex { get; }

        /// <summary>
        /// Gets array of transactions corresponding to each input's previous transaction.
        /// </summary>
        public ImmutableArray<DecodedTx> InputTxes { get; }

        /// <summary>
        /// Get the previous transaction output for the specified input.
        /// </summary>
        /// <param name="inputIndex">The index of the input.</param>
        /// <returns>The input's previous transaction output.</returns>
        public TxOutput GetInputPrevTxOutput(int inputIndex)
        {
            if (inputIndex < 0)
                throw new ArgumentException($"{nameof(inputIndex)} of {inputIndex} is < 0");
            else if (inputIndex >= Transaction.Inputs.Length)
                throw new ArgumentException($"{nameof(inputIndex)} of {inputIndex} is >= {Transaction.Inputs.Length}");

            var inputTx = this.InputTxes[inputIndex].Transaction;

            var prevTxOutputsLength = inputTx.Outputs.Length;
            var prevTxOutputIndex = this.Transaction.Inputs[inputIndex].PrevTxOutputKey.TxOutputIndex.ToIntChecked();

            if (prevTxOutputIndex < 0 || prevTxOutputIndex >= prevTxOutputsLength)
                throw new InvalidOperationException($"{nameof(prevTxOutputIndex)} of {prevTxOutputIndex} is < 0 or >= {prevTxOutputsLength}");

            return inputTx.Outputs[prevTxOutputIndex];
        }
    }
}
