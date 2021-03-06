﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.JsonRpc;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AElf.Kernel;
using AElf.Common;
using AElf.Configuration.Config.Chain;
using AElf.SmartContract;
using Community.AspNetCore.JsonRpc;
using Google.Protobuf;
using Svc = AElf.ChainController.Rpc.ChainControllerRpcService;

namespace AElf.ChainController.Rpc
{
    internal static class ServiceExtensions
    {
        internal static IDictionary<string, (JsonRpcRequestContract, MethodInfo, ParameterInfo[], string[])>
            GetRpcMethodContracts(this Svc s)
        {
            var methods = s.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public);
            IDictionary<string, (JsonRpcRequestContract, MethodInfo, ParameterInfo[], string[])> contracts =
                new ConcurrentDictionary<string, (JsonRpcRequestContract, MethodInfo, ParameterInfo[], string[])>();
            foreach (var method in methods)
            {
                var attribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();

                if (attribute == null)
                {
                    continue;
                }

                if (!(method.ReturnType == typeof(Task)) &&
                    !(method.ReturnType.IsGenericType &&
                      (method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))))
                {
                    continue;
                }

                var contract = default(JsonRpcRequestContract);
                var parameters = method.GetParameters();
                var parametersBindings = default(string[]);

                Func<JsonRpcParametersType> ParametersType = () =>
                    (JsonRpcParametersType) typeof(JsonRpcMethodAttribute).GetProperty("ParametersType",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(attribute, null);
                Func<int[]> ParameterPositions = () =>
                    (int[]) typeof(JsonRpcMethodAttribute).GetProperty("ParameterPositions",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(attribute, null);
                Func<string[]> ParameterNames = () =>
                    (string[]) typeof(JsonRpcMethodAttribute).GetProperty("ParameterNames",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(attribute, null);
                Func<string> MethodName = () =>
                    (string) typeof(JsonRpcMethodAttribute)
                        .GetProperty("MethodName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        ?.GetValue(attribute, null);

                switch (ParametersType())
                {
                    case JsonRpcParametersType.ByPosition:
                    {
                        var parameterPositions = ParameterPositions();

                        if (parameterPositions.Length != parameters.Length)
                        {
                            continue;
                        }

                        if (!Enumerable.Range(0, parameterPositions.Length).All(i =>
                            parameterPositions.Contains(i)
                        ))
                        {
                            continue;
                        }

                        var parametersContract = new Type[parameters.Length];

                        for (var i = 0; i < parameters.Length; i++)
                        {
                            parametersContract[i] = parameters[i].ParameterType;
                        }

                        contract = new JsonRpcRequestContract(parametersContract);
                    }
                        break;
                    case JsonRpcParametersType.ByName:
                    {
                        var parameterNames = ParameterNames();

                        if (parameterNames.Length != parameters.Length)
                        {
                            continue;
                        }

                        if (parameterNames.Length != parameterNames.Distinct(StringComparer.Ordinal).Count())
                        {
                            continue;
                        }

                        var parametersContract =
                            new Dictionary<string, Type>(parameters.Length, StringComparer.Ordinal);

                        parametersBindings = new string[parameters.Length];

                        for (var i = 0; i < parameters.Length; i++)
                        {
                            parametersContract[parameterNames[i]] = parameters[i].ParameterType;
                            parametersBindings[i] = parameterNames[i];
                        }

                        contract = new JsonRpcRequestContract(parametersContract);
                    }
                        break;
                    default:
                    {
                        if (parameters.Length != 0)
                        {
                            continue;
                        }

                        contract = new JsonRpcRequestContract();
                    }
                        break;
                }

                contracts[MethodName()] = (contract, method, parameters, parametersBindings);
            }

            return contracts;
        }

        internal static async Task<IMessage> GetContractAbi(this Svc s, Address address)
        {
            return await s.SmartContractService.GetAbiAsync(address);
        }

        internal static async Task<Transaction> GetTransaction(this Svc s, Hash txId)
        {
            var r = await s.TxHub.GetReceiptAsync(txId);
            return r?.Transaction;
        }

        internal static async Task<TransactionReceipt> GetTransactionReceipt(this Svc s, Hash txId)
        {
            return await s.TxHub.GetReceiptAsync(txId);
        }

        internal static async Task<TransactionResult> GetTransactionResult(this Svc s, Hash txHash)
        {
            var res = await s.TransactionResultService.GetResultAsync(txHash);
            return res;
        }

        internal static async Task<TransactionTrace> GetTransactionTrace(this Svc s, Hash txHash, ulong height)
        {
            var b = await s.GetBlockAtHeight(height);
            if (b == null)
            {
                return null;
            }

            var prodAddr = Hash.FromRawBytes(b.Header.P.ToByteArray());
            var res = await s.TransactionTraceManager.GetTransactionTraceAsync(txHash,
                HashHelpers.GetDisambiguationHash(height, prodAddr));
            return res;
        }

        internal static async Task<IEnumerable<string>> GetTransactionParameters(this Svc s, Transaction tx)
        {
            try
            {
                return await s.SmartContractService.GetInvokingParams(tx);
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        internal static async Task<ulong> GetCurrentChainHeight(this Svc s)
        {
            var chainContext = await s.ChainContextService.GetChainContextAsync(Hash.LoadBase58(ChainConfig.Instance.ChainId));
            return chainContext.BlockHeight;
        }

        internal static async Task<Block> GetBlockAtHeight(this Svc s, ulong height)
        {
            var blockchain = s.ChainService.GetBlockChain(Hash.LoadBase58(ChainConfig.Instance.ChainId));
            return (Block) await blockchain.GetBlockByHeightAsync(height);
        }

        internal static async Task<ulong> GetTransactionPoolSize(this Svc s)
        {
            return (ulong)(await s.TxHub.GetReceiptsOfExecutablesAsync()).Count;
        }

        internal static async Task<BinaryMerkleTree> GetBinaryMerkleTreeByHeight(this Svc s, ulong height)
        {
            return await s.BinaryMerkleTreeManager.GetTransactionsMerkleTreeByHeightAsync(Hash.LoadBase58(ChainConfig.Instance.ChainId), height);
        }

//        internal static void SetBlockVolume(this Svc s, int minimal, int maximal)
//        {
//            // TODO: Maybe control this in miner
////            s.TxPool.SetBlockVolume(minimal, maximal);
//        }

        internal static async Task<byte[]> CallReadOnly(this Svc s, Transaction tx)
        {
            var trace = new TransactionTrace
            {
                TransactionId = tx.GetHash()
            };

            var chainContext = await s.ChainContextService.GetChainContextAsync(Hash.LoadBase58(ChainConfig.Instance.ChainId));
            var txCtxt = new TransactionContext
            {
                PreviousBlockHash = chainContext.BlockHash,
                Transaction = tx,
                Trace = trace,
                BlockHeight = chainContext.BlockHeight
            };

            var executive = await s.SmartContractService.GetExecutiveAsync(tx.To, Hash.LoadBase58(ChainConfig.Instance.ChainId));

            try
            {
                await executive.SetTransactionContext(txCtxt).Apply();
            }
            finally
            {
                await s.SmartContractService.PutExecutiveAsync(tx.To, executive);
            }

            if(!string.IsNullOrEmpty(trace.StdErr))
                throw new Exception(trace.StdErr);
            return trace.RetVal.ToFriendlyBytes();
        }

        #region Cross chain

        internal static async Task<MerklePath> GetTxRootMerklePathInParentChain(this Svc s, ulong height)
        {
            var merklePath = await s.CrossChainInfoReader.GetTxRootMerklePathInParentChainAsync(height);
            if (merklePath != null)
                return merklePath;
            throw new Exception();
        }

        internal static async Task<ParentChainBlockInfo> GetParentChainBlockInfo(this Svc s, ulong height)
        {
            var parentChainBlockInfo = await s.CrossChainInfoReader.GetBoundParentChainBlockInfoAsync(height);
            if (parentChainBlockInfo != null)
                return parentChainBlockInfo;
            throw new Exception();
        }

        internal static async Task<ulong> GetBoundParentChainHeight(this Svc s, ulong height)
        {
            var parentHeight = await s.CrossChainInfoReader.GetBoundParentChainHeightAsync(height);
            if (parentHeight != 0)
                return parentHeight;
            throw new Exception();
        }

        #endregion

        #region Proposal

        internal static async Task<Proposal> GetProposal(this Svc s, Hash proposalHash)
        {
            return await s.AuthorizationInfoReader.GetProposal(proposalHash);
        }

        internal static async Task<Authorization> GetAuthorization(this Svc s, Address msig)
        {
            return await s.AuthorizationInfoReader.GetAuthorization(msig);
        }

        #endregion
        
        internal static async Task<Block> GetBlock(this Svc s, Hash blockHash)
        {
            var blockchain = s.ChainService.GetBlockChain(Hash.LoadBase58(ChainConfig.Instance.ChainId));
            return (Block) await blockchain.GetBlockByHashAsync(blockHash);
        }

        internal static async Task<int> GetInvalidBlockCountAsync(this Svc s)
        {
            // TODO: change hard code
            return await Task.FromResult(999); 
        }
        
        #region Consensus

        internal static async Task<Tuple<ulong, ulong>> GetVotesGeneral(this Svc s)
        {
            return await s.ElectionInfo.GetVotesGeneral();
        }
        
        internal static async Task<Tickets> GetVotingInfo(this Svc s, string pubKey)
        {
            return await s.ElectionInfo.GetVotingInfo(pubKey);
        }
        
        #endregion

        internal static IMessage GetInstance(this Svc s,string type)
        {
            switch (type)
            {
                case "MerklePath":
                    return new MerklePath();
                case "BinaryMerkleTree":
                    return new BinaryMerkleTree ();
                case "BlockHeader":
                    return new BlockHeader();
                case "BlockBody":
                    return new BlockBody();
                case "Hash":
                    return new Hash();
                case "SmartContractRegistration":
                    return new SmartContractRegistration();
                case "Transaction":
                    return new Transaction();
                case "TransactionResult":
                    return new TransactionResult();
                case "TransactionTrace":
                    return new TransactionTrace();
                default:
                    throw new ArgumentException($"[{type}] not found");
            }
        }
        
        internal static async Task<int> GetRollBackTimesAsync(this Svc s)
        {
            return await Task.FromResult(s.BlockSynchronizer.RollBackTimes);
        }
    }
}