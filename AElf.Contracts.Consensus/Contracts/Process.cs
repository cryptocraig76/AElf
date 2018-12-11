using System;
using System.Collections.Generic;
using System.Linq;
using AElf.Common;
using AElf.Configuration.Config.Consensus;
using AElf.Kernel;
using AElf.Types.CSharp;
using Google.Protobuf.WellKnownTypes;
using Api = AElf.Sdk.CSharp.Api;

namespace AElf.Contracts.Consensus.Contracts
{
    // ReSharper disable UnusedMember.Global
    // ReSharper disable InconsistentNaming
    public class Process
    {
        private string RoundIdNotMatched => "Round Id not matched.";

        private ulong CurrentRoundNumber => _collection.CurrentRoundNumberField.GetValue();

        private ulong CurrentTermNumber => _collection.CurrentTermNumberField.GetValue();

        private int LogLevel { get; set; }

        private readonly DataCollection _collection;

        public Process(DataCollection collection)
        {
            _collection = collection;
        }

        public void InitialTerm(Term firstTerm, int logLevel)
        {
            _collection.AgeField.SetValue(1);

            _collection.CurrentTermNumberField.SetValue(1);
            
            _collection.CurrentRoundNumberField.SetValue(1);

            SetAliases(firstTerm);

            firstTerm.FirstRound.RealTimeMinersInfo[Api.GetPublicKeyToHex()].ProducedBlocks += 1;

            _collection.RoundsMap.SetValue(((ulong) 1).ToUInt64Value(), firstTerm.FirstRound);
            _collection.RoundsMap.SetValue(((ulong) 2).ToUInt64Value(), firstTerm.SecondRound);

            LogLevel = logLevel;
        }

        public void NextTerm(Term term)
        {
            Api.Assert(ValidateMiners(term.FirstRound),
                $"Miners list is wrong of round {term.FirstRound.RoundNumber}.");
            Api.Assert(ValidateMiners(term.SecondRound),
                $"Miners list is wrong of round {term.SecondRound.RoundNumber}.");

            SnapshotAndDividends();

            if (CurrentTermNumber == term.TermNumber - 1)
            {
                _collection.CurrentTermNumberField.SetValue(term.TermNumber);
            }
            
            if (CurrentRoundNumber == term.FirstRound.RoundNumber - 1)
            {
                _collection.CurrentRoundNumberField.SetValue(term.FirstRound.RoundNumber);
            }

            foreach (var minerInRound in term.FirstRound.RealTimeMinersInfo.Values)
            {
                minerInRound.MissedTimeSlots = 0;
                minerInRound.ProducedBlocks = 0;
            }
            
            foreach (var minerInRound in term.SecondRound.RealTimeMinersInfo.Values)
            {
                minerInRound.MissedTimeSlots = 0;
                minerInRound.ProducedBlocks = 0;
            }

            term.FirstRound.RealTimeMinersInfo[Api.GetPublicKeyToHex()].ProducedBlocks += 1;
            
            _collection.RoundsMap.SetValue(CurrentRoundNumber.ToUInt64Value(), term.FirstRound);
            _collection.RoundsMap.SetValue((CurrentRoundNumber + 1).ToUInt64Value(), term.SecondRound);
        }

        public void Update(Forwarding forwarding)
        {
            // TODO: time slot validation

            var forwardingCurrentRoundInfo = forwarding.CurrentRoundInfo;
            var currentRoundInfo = GetRoundInfo(forwardingCurrentRoundInfo.RoundNumber);
            Api.Assert(forwardingCurrentRoundInfo.RoundId == currentRoundInfo.RoundId, RoundIdNotMatched);

            var completeCurrentRoundInfo = SupplyCurrentRoundInfo(currentRoundInfo, forwardingCurrentRoundInfo);

            if (forwarding.NextRoundInfo.RoundNumber == 0)
            {
                if (_collection.RoundsMap.TryGet((currentRoundInfo.RoundNumber + 1).ToUInt64Value(), out var nextRoundInfo))
                {
                    foreach (var minerInRound in completeCurrentRoundInfo.RealTimeMinersInfo)
                    {
                        nextRoundInfo.RealTimeMinersInfo[minerInRound.Key].MissedTimeSlots =
                            minerInRound.Value.MissedTimeSlots;
                        nextRoundInfo.RealTimeMinersInfo[minerInRound.Key].ProducedBlocks =
                            minerInRound.Value.ProducedBlocks;
                    }
                    nextRoundInfo.RealTimeMinersInfo[Api.GetPublicKeyToHex()].ProducedBlocks += 1;
                    _collection.RoundsMap.SetValue(nextRoundInfo.RoundNumber.ToUInt64Value(), nextRoundInfo);
                    _collection.CurrentRoundNumberField.SetValue(nextRoundInfo.RoundNumber);
                }
            }
            else
            {
                // Update missed time slots and  produced blocks for each miner.
                foreach (var minerInRound in completeCurrentRoundInfo.RealTimeMinersInfo)
                {
                    forwarding.NextRoundInfo.RealTimeMinersInfo[minerInRound.Key].MissedTimeSlots =
                        minerInRound.Value.MissedTimeSlots;
                    forwarding.NextRoundInfo.RealTimeMinersInfo[minerInRound.Key].ProducedBlocks =
                        minerInRound.Value.ProducedBlocks;
                }
                forwarding.NextRoundInfo.RealTimeMinersInfo[Api.GetPublicKeyToHex()].ProducedBlocks += 1;
                _collection.RoundsMap.SetValue(forwarding.NextRoundInfo.RoundNumber.ToUInt64Value(),
                    forwarding.NextRoundInfo);
                _collection.CurrentRoundNumberField.SetValue(forwarding.NextRoundInfo.RoundNumber);
            }

            WindUp();
        }

        public void PublishOutValue(ToPackage toPackage)
        {
            Api.Assert(toPackage.RoundId == GetCurrentRoundInfo().RoundId, RoundIdNotMatched);
            
            var roundInfo = GetCurrentRoundInfo();

            roundInfo.RealTimeMinersInfo[Api.GetPublicKeyToHex()].Signature = toPackage.Signature;
            roundInfo.RealTimeMinersInfo[Api.GetPublicKeyToHex()].OutValue = toPackage.OutValue;

            roundInfo.RealTimeMinersInfo[Api.GetPublicKeyToHex()].ProducedBlocks += 1;
            
            _collection.RoundsMap.SetValue(CurrentRoundNumber.ToUInt64Value(), roundInfo);
        }

        public void PublishInValue(ToBroadcast toBroadcast)
        {
            Api.Assert(toBroadcast.RoundId == GetCurrentRoundInfo().RoundId, RoundIdNotMatched);
            
            var roundInfo = GetCurrentRoundInfo();
            Api.Assert(roundInfo.RealTimeMinersInfo[Api.GetPublicKeyToHex()].OutValue != null,
                $"Out Value of {Api.GetPublicKeyToHex()} is null");
            Api.Assert(roundInfo.RealTimeMinersInfo[Api.GetPublicKeyToHex()].Signature != null,
                $"Signature of {Api.GetPublicKeyToHex()} is null");

            roundInfo.RealTimeMinersInfo[Api.GetPublicKeyToHex()].InValue = toBroadcast.InValue;
            
            _collection.RoundsMap.SetValue(CurrentRoundNumber.ToUInt64Value(), roundInfo);
        }
        
        #region Vital Steps

        private void SetAliases(Term term)
        {
            var index = 0;
            foreach (var publicKey in term.Miners.PublicKeys)
            {
                if (index >= Config.Aliases.Count) 
                    continue;

                var alias = Config.Aliases[index];
                _collection.AliasesMap.SetValue(new StringValue {Value = publicKey},
                    new StringValue {Value = alias});
                ConsoleWriteLine(nameof(SetAliases), $"Set alias {alias} to {publicKey}");
                index++;
            }
        }

        /// <summary>
        /// Can only supply signature, out value, in value if one missed his time slot.
        /// </summary>
        /// <param name="roundInfo"></param>
        /// <param name="forwardingRoundInfo"></param>
        private Round SupplyCurrentRoundInfo(Round roundInfo, Round forwardingRoundInfo)
        {
            foreach (var suppliedMiner in forwardingRoundInfo.RealTimeMinersInfo)
            {
                if (suppliedMiner.Value.MissedTimeSlots > roundInfo.RealTimeMinersInfo[suppliedMiner.Key].MissedTimeSlots
                    && roundInfo.RealTimeMinersInfo[suppliedMiner.Key].OutValue == null)
                {
                    roundInfo.RealTimeMinersInfo[suppliedMiner.Key].OutValue = suppliedMiner.Value.OutValue;
                    roundInfo.RealTimeMinersInfo[suppliedMiner.Key].InValue = suppliedMiner.Value.InValue;
                    roundInfo.RealTimeMinersInfo[suppliedMiner.Key].Signature = suppliedMiner.Value.Signature;

                    roundInfo.RealTimeMinersInfo[suppliedMiner.Key].MissedTimeSlots += 1;
                }
            }
            
            _collection.RoundsMap.SetValue(roundInfo.RoundNumber.ToUInt64Value(), roundInfo);

            return roundInfo;
        }

        private void WindUp()
        {
            // Check in and out value, complain if not match.
            
        }
        
        #endregion

        private DateTime GetLocalTime()
        {
            return DateTime.UtcNow.ToLocalTime();
        }

        private Timestamp GetTimestampWithOffset(Timestamp origin, int offset)
        {
            return Timestamp.FromDateTime(origin.ToDateTime().AddMilliseconds(offset));
        }

        private string GetAlias(string publicKey)
        {
            return _collection.AliasesMap.TryGet(new StringValue {Value = publicKey}, out var alias)
                ? alias.Value
                : publicKey.Substring(5);
        }

        private Round GetCurrentRoundInfo()
        {
            Api.Assert(_collection.RoundsMap.TryGet(CurrentRoundNumber.ToUInt64Value(), out var currentRoundInfo),
                $"Can't get information of round {CurrentRoundNumber}");

            return currentRoundInfo;
        }

        private Round GetRoundInfo(ulong roundNumber)
        {
            Api.Assert(_collection.RoundsMap.TryGet(roundNumber.ToUInt64Value(), out var roundInfo),
                $"Can't get information of round {roundNumber}");

            return roundInfo;
        }

        private bool ValidateMiners(Round roundInfo)
        {
            var minersList = roundInfo.RealTimeMinersInfo.Keys;
            return !GetVictories().Except(minersList).Any();
        }

        private IEnumerable<string> GetVictories()
        {
            var candidates = _collection.CandidatesField.GetValue();
            var ticketsMap = new Dictionary<string, ulong>();
            foreach (var candidate in candidates.PublicKeys)
            {
                if (_collection.TicketsMap.TryGet(candidate.ToStringValue(), out var tickets))
                {
                    ticketsMap.Add(candidate, tickets.TotalTickets);
                }
            }

            return ticketsMap.OrderBy(tm => tm.Value).Take(GlobalConfig.BlockProducerNumber).Select(tm => tm.Key)
                .ToList();
        }

        private void SnapshotAndDividends()
        {
            var currentRoundInfo = GetCurrentRoundInfo();

            UpdateCandidatesInfoInHistory(currentRoundInfo, GetPreviousTerm());
            
            var minedBlocks = currentRoundInfo.RealTimeMinersInfo.Values.Aggregate<MinerInRound, ulong>(0,
                (current, minerInRound) => current + minerInRound.ProducedBlocks);
            var candidateInTerms = new List<CandidateInTerm>();
  
            ulong totalVotes = 0;
            ulong totalReappointment = 0;
            var temp = new Dictionary<string, ulong>();
            foreach (var minerInRound in currentRoundInfo.RealTimeMinersInfo)
            {
                ulong votes = 0;
                if (_collection.TicketsMap.TryGet(minerInRound.Key.ToStringValue(), out var candidateTickets))
                {
                    foreach (var votingRecord in candidateTickets.VotingRecords)
                    {
                        votes += votingRecord.Count;
                    }
                }

                totalVotes += votes;
                candidateInTerms.Add(new CandidateInTerm
                {
                    PublicKey = minerInRound.Key,
                    Votes = votes
                });

                if (_collection.HistoryMap.TryGet(minerInRound.Key.ToStringValue(), out var candidateInHistory))
                {
                    totalReappointment += candidateInHistory.ReappointmentCount;
                    temp.Add(minerInRound.Key, candidateInHistory.ReappointmentCount);
                }
            }

            // Transfer dividends for actual miners. (The miners list based on last round of current term.)
            foreach (var candidateInTerm in candidateInTerms)
            {
                Api.Call(Api.TokenContractAddress, "Transfer",
                    ParamsPacker.Pack(new List<object>
                    {
                        candidateInTerm.PublicKey,
                        (ulong) (Config.GetDividendsForEveryMiner(minedBlocks) + Config.GetDividendsForTicketsCount(minedBlocks) * candidateInTerm.Votes / totalVotes +
                         Config.GetDividendsForReappointment(minedBlocks) * temp[candidateInTerm.PublicKey] / totalReappointment)
                    }));
            }

            var backups = _collection.CandidatesField.GetValue().PublicKeys
                .Except(candidateInTerms.Select(cit => cit.PublicKey)).ToList();
            foreach (var backup in backups)
            {
                Api.Call(Api.TokenContractAddress, "Transfer",
                    ParamsPacker.Pack(new List<object>
                    {
                        backup,
                        (ulong) (Config.GetDividendsForBackupNodes(minedBlocks) / (ulong) backups.Count)
                    }));
            }

            var currentTermNumber =
                CurrentTermNumber == 0 ? ((ulong) 1).ToUInt64Value() : CurrentTermNumber.ToUInt64Value();
            
            _collection.TermKeyLookUpMap.SetValue(CurrentRoundNumber.ToUInt64Value(),currentTermNumber);
            _collection.SnapshotField.SetValue(currentTermNumber, new TermSnapshot
            {
                TermNumber = currentTermNumber.Value,
                EndRoundNumber = CurrentRoundNumber,
                TotalBlocks = minedBlocks,
                CandidatesSnapshot = {candidateInTerms}
            });
        }

        private void UpdateCandidatesInfoInHistory(Round currentRoundInfo, TermSnapshot previousTerm)
        {
            if (previousTerm == null)
            {
                // Initial history information for every miner.
                foreach (var candidate in currentRoundInfo.RealTimeMinersInfo)
                {
                    _collection.HistoryMap.SetValue(candidate.Key.ToStringValue(), new CandidateInHistory
                    {
                        MissedTimeSlots = candidate.Value.MissedTimeSlots,
                        ProducedBlocks = candidate.Value.ProducedBlocks,
                        ReappointmentCount = 0,
                        Terms = {1}
                    });
                }
            }
            else
            {
                foreach (var candidate in currentRoundInfo.RealTimeMinersInfo)
                {
                    if (_collection.HistoryMap.TryGet(candidate.Key.ToStringValue(), out var historyInfo))
                    {
                        _collection.HistoryMap.SetValue(candidate.Key.ToStringValue(), new CandidateInHistory
                        {
                            MissedTimeSlots = historyInfo.MissedTimeSlots + candidate.Value.MissedTimeSlots,
                            ProducedBlocks = historyInfo.ProducedBlocks + candidate.Value.ProducedBlocks,
                            ReappointmentCount =
                                previousTerm.CandidatesSnapshot.Any(cit => cit.PublicKey == candidate.Key)
                                    ? historyInfo.ReappointmentCount + 1
                                    : 0,
                            Terms = {historyInfo.Terms, previousTerm.TermNumber}
                        });
                    }
                    else
                    {
                        _collection.HistoryMap.SetValue(candidate.Key.ToStringValue(), new CandidateInHistory
                        {
                            MissedTimeSlots = candidate.Value.MissedTimeSlots,
                            ProducedBlocks = candidate.Value.ProducedBlocks,
                            ReappointmentCount = 0,
                            Terms = {previousTerm.TermNumber}
                        });
                    }
                }
            }
        }

        private TermSnapshot GetPreviousTerm()
        {
            if (_collection.SnapshotField.TryGet((CurrentTermNumber - 1).ToUInt64Value(), out var previousTerm))
            {
                return previousTerm;
            }

            return null;
        }

        /// <summary>
        /// Debug level:
        /// 6 = Off
        /// 5 = Fatal
        /// 4 = Error
        /// 3 = Warn
        /// 2 = Info
        /// 1 = Debug
        /// 0 = Trace
        /// </summary>
        /// <param name="prefix"></param>
        /// <param name="log"></param>
        /// <param name="ex"></param>
        private void ConsoleWriteLine(string prefix, string log, Exception ex = null)
        {
            if (LogLevel == 6)
                return;

            Console.WriteLine($"[{GetLocalTime():yyyy-MM-dd HH:mm:ss.fff} - Consensus]{prefix} - {log}.");
            if (ex != null)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// Return true if ts1 >= ts2
        /// </summary>
        /// <param name="ts1"></param>
        /// <param name="ts2"></param>
        /// <returns></returns>
        // ReSharper disable once MemberCanBeMadeStatic.Local
        private bool CompareTimestamp(Timestamp ts1, Timestamp ts2)
        {
            return ts1.ToDateTime() >= ts2.ToDateTime();
        }
    }
}