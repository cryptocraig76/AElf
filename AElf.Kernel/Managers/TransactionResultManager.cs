﻿using System.Threading.Tasks;
using AElf.Common;
using AElf.Kernel.Storages;

namespace AElf.Kernel.Managers
{
    public class TransactionResultManager : ITransactionResultManager
    {
        private readonly ITransactionResultStore _transactionResultStore;

        public TransactionResultManager(ITransactionResultStore transactionResultStore)
        {
            _transactionResultStore = transactionResultStore;
        }

        public async Task AddTransactionResultAsync(TransactionResult tr)
        {
            await _transactionResultStore.SetAsync(tr.TransactionId.ToHex(), tr);
        }

        public async Task<TransactionResult> GetTransactionResultAsync(Hash txId)
        {
            return await _transactionResultStore.GetAsync<TransactionResult>(txId.ToHex());
        }
    }
}