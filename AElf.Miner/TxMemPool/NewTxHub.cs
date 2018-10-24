using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AElf.ChainController;
using AElf.Common;
using AElf.Configuration;
using AElf.Cryptography.ECDSA;
using AElf.Kernel;
using AElf.Kernel.Managers;
using AElf.Miner.EventMessages;
using Easy.MessageHub;
using Google.Protobuf;
using NLog;

namespace AElf.Miner.TxMemPool
{
    public class NewTxHub
    {
        private ITransactionManager _transactionManager;
        private ConcurrentDictionary<Hash, TransactionReceipt> _allTxns =
            new ConcurrentDictionary<Hash, TransactionReceipt>();

        private IChainService _chainService;
        private IBlockChain _blockChain;
        private CanonicalBlockHashCache _canonicalBlockHashCache;

        public CanonicalBlockHashCache CanonicalBlockHashCache
        {
            get
            {
                if (_canonicalBlockHashCache == null)
                {
                    _canonicalBlockHashCache= new CanonicalBlockHashCache(BlockChain, LogManager.GetLogger(nameof(NewTxHub))); 
                }

                return _canonicalBlockHashCache;
            }
        }


        private IBlockChain BlockChain
        {
            get
            {
                if (_blockChain == null)
                {
                    _blockChain =
                        _chainService.GetBlockChain(Hash.LoadHex(NodeConfig.Instance.ChainId));
                }

                return _blockChain;
            }
        }

        public Action<TransactionReceipt> SignatureValidator { get; set; } = (tr) =>
        {
            Task.Run(() =>
            {
                if (VerifySignature(tr.Transaction))
                {
                    tr.SignatureSt = TransactionReceipt.Types.SignatureStatus.SignatureValid;
                    MaybePublishTransaction(tr);
                }
                else
                {
                    tr.SignatureSt = TransactionReceipt.Types.SignatureStatus.SignatureInvalid;
                }
            }).ConfigureAwait(false);
        };

        public Action<NewTxHub, TransactionReceipt> RefBlockValidator { get; set; } = (hub, tr) => { 
            Task.Run(async () =>
            {
                tr.RefBlockSt = await hub.ValidateReferenceBlockAsync(tr.Transaction);
                MaybePublishTransaction(tr);
            }); 
        };

        public Action<TransactionReceipt> SystemTxnIdentifier { get; set; } = (tr) =>
        {
            var systemAddresses = new List<Address>()
            {
                AddressHelpers.GetSystemContractAddress(
                    Hash.LoadHex(NodeConfig.Instance.ChainId),
                    SmartContractType.AElfDPoS.ToString()),
                AddressHelpers.GetSystemContractAddress(
                    Hash.LoadHex(NodeConfig.Instance.ChainId),
                    SmartContractType.SideChainContract.ToString())
            };
            if (systemAddresses.Contains(tr.Transaction.To))
            {
                tr.IsSystemTxn = true;
            }
        };

        public NewTxHub(ITransactionManager transactionManager, IChainService chainService)
        {
            _transactionManager = transactionManager;
            _chainService = chainService;
            MessageHub.Instance.Subscribe<TransactionExecuted>((te) =>
            {
                foreach (var tx in te.Transactions)
                {
                    if (_allTxns.TryGetValue(tx.GetHash(), out var tr))
                    {
                        tr.Status = TransactionReceipt.Types.TransactionStatus.TransactionExecuted;
                    }
                }
            });
        }

        // This may be moved to extension method
        private static bool VerifySignature(Transaction tx)
        {
            if (tx.P == null)
            {
                return false;
            }

            byte[] uncompressedPrivKey = tx.P.ToByteArray();
            var addr = Address.FromRawBytes(uncompressedPrivKey);

            if (!addr.Equals(tx.From))
                return false;
            ECKeyPair recipientKeyPair = ECKeyPair.FromPublicKey(uncompressedPrivKey);
            ECVerifier verifier = new ECVerifier(recipientKeyPair);
            return verifier.Verify(tx.GetSignature(), tx.GetHash().DumpByteArray());
        }
        
        public async Task AddTransactionAsync(Transaction transaction)
        {
            var tr = new TransactionReceipt(transaction);

            var txn = await _transactionManager.GetTransaction(tr.TransactionId);

            // if the transaction is in TransactionManager, it is either executed or added into _allTxns
            if (txn != null && !txn.Equals(new Transaction()))
            {
                throw new Exception("Transaction already exists.");
            }

            
            if (!_allTxns.TryAdd(tr.TransactionId, tr))
            {
                // Add failed, transaction exists already
                throw new Exception("Transaction already exists.");
            }

            if (SignatureValidator == null)
            {
                throw  new Exception($"{nameof(SignatureValidator)} is not set.");
            }

            SystemTxnIdentifier.Invoke(tr);
            SignatureValidator.Invoke(tr);
            RefBlockValidator.Invoke(this, tr);
        }

        public Task<IEnumerable<TransactionReceipt>> GetReadyTxsAsync()
        {
            return Task.FromResult(_allTxns.Values.Where(x => x.IsExecutable));
        }

        public async Task<TransactionReceipt> GetTxReceiptAsync(Hash txId)
        {
            _allTxns.TryGetValue(txId, out var tr);
            return await Task.FromResult(tr);
        }

        public async Task<Transaction> GetTxAsync(Hash txId)
        {
            if (_allTxns.TryGetValue(txId, out var tr))
            {
                return await Task.FromResult(tr.Transaction);
            }

            return await _transactionManager.GetTransaction(txId);
        }
        
        private static bool CheckPrefix(Hash blockHash, ByteString prefix)
        {
            if (prefix.Length > blockHash.Value.Length)
            {
                return false;
            }

            return !prefix.Where((t, i) => t != blockHash.Value[i]).Any();
        }

        public async Task<TransactionReceipt.Types.RefBlockStatus> ValidateReferenceBlockAsync(Transaction tx)
        {
            if (tx.RefBlockNumber <= GlobalConfig.GenesisBlockHeight && CheckPrefix(Hash.Genesis, tx.RefBlockPrefix))
            {
                return TransactionReceipt.Types.RefBlockStatus.RefBlockValid;
            }

            var curHeight = CanonicalBlockHashCache.CurrentHeight;
            if (tx.RefBlockNumber > curHeight && curHeight > GlobalConfig.GenesisBlockHeight)
            {
                return TransactionReceipt.Types.RefBlockStatus.RefBlockInvalid;
            }

            if (curHeight > GlobalConfig.ReferenceBlockValidPeriod + GlobalConfig.GenesisBlockHeight &&
                curHeight - tx.RefBlockNumber > GlobalConfig.ReferenceBlockValidPeriod)
            {
                return TransactionReceipt.Types.RefBlockStatus.RefBlockExpired;
            }

            Hash canonicalHash;
            if (curHeight == 0)
            {
                canonicalHash = await BlockChain.GetCurrentBlockHashAsync();
            }
            else
            {
                canonicalHash = CanonicalBlockHashCache.GetHashByHeight(tx.RefBlockNumber);
            }

            if (canonicalHash == null)
            {
                canonicalHash = (await BlockChain.GetBlockByHeightAsync(tx.RefBlockNumber)).GetHash();
            }

            if (canonicalHash == null)
            {
                throw new Exception(
                    $"Unable to get canonical hash for height {tx.RefBlockNumber} - current height: {curHeight}");
            }

            // TODO: figure out why do we need this
            if (GlobalConfig.BlockProducerNumber == 1)
            {
                return TransactionReceipt.Types.RefBlockStatus.RefBlockValid;
            }

            var res = CheckPrefix(canonicalHash, tx.RefBlockPrefix)
                ? TransactionReceipt.Types.RefBlockStatus.RefBlockValid
                : TransactionReceipt.Types.RefBlockStatus.RefBlockInvalid;
            return res;
        }

        private static void MaybePublishTransaction(TransactionReceipt tr)
        {
            if (tr.IsExecutable)
            {
                MessageHub.Instance.Publish(new TransactionAddedToPool(tr.Transaction));
            }
        }
    }
}