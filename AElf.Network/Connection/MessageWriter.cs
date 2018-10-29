﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;
using AElf.Common;
using NLog;

namespace AElf.Network.Connection
{
    /// <summary>
    /// This class performs writes to the underlying tcp stream.
    /// </summary>
    public class MessageWriter : IMessageWriter
    {
        private const int DefaultMaxOutboundPacketSize = 20148;

        private readonly ILogger _logger;
        private readonly NetworkStream _stream;

        private BlockingCollection<Message> _outboundMessages;

        internal bool IsDisposed { get; private set; }

        /// <summary>
        /// This configuration property determines the maximum size an
        /// outgoing messages payload. If the payloads size is larger
        /// than this value, this message will be send in multiple sub
        /// packets.
        /// </summary>
        public int MaxOutboundPacketSize { get; set; } = DefaultMaxOutboundPacketSize;

        public MessageWriter(NetworkStream stream)
        {
            _outboundMessages = new BlockingCollection<Message>();
            _stream = stream;

            _logger = LogManager.GetLogger(nameof(MessageWriter));
        }

        /// <summary>
        /// Starts the dequing of outgoing messages.
        /// </summary>
        public void Start()
        {
            Task.Run(() => DequeueOutgoingLoop()).ConfigureAwait(false);
        }

        public void EnqueueMessage(Message p)
        {
            if (IsDisposed || _outboundMessages == null || _outboundMessages.IsAddingCompleted)
                return;

            try
            {
                _outboundMessages.Add(p);
            }
            catch (Exception e)
            {
                _logger.Trace(e, "Exception while enqueue for outgoing message.");
            }
        }

        /// <summary>
        /// The main loop that sends queud up messages from the message queue.
        /// </summary>
        internal void DequeueOutgoingLoop()
        {
            while (!IsDisposed && _outboundMessages != null)
            {
                Message p = null;

                try
                {
                    p = _outboundMessages.Take();
                }
                catch (Exception e)
                {
                    Dispose(); // if already disposed will do nothing 
                    break;
                }

                try
                {
                    if (p.Payload.Length > MaxOutboundPacketSize)
                    {
                        // Split
                        int packetCount = (p.Payload.Length / MaxOutboundPacketSize);
                        int lastPacketSize = p.Payload.Length % MaxOutboundPacketSize;

                        if (lastPacketSize != 0)
                            packetCount++;

                        List<PartialPacket> partials = new List<PartialPacket>();

                        int currentIndex = 0;
                        for (int i = 0; i < packetCount - 1; i++)
                        {
                            byte[] slice = new byte[MaxOutboundPacketSize];

                            Array.Copy(p.Payload, currentIndex, slice, 0, MaxOutboundPacketSize);

                            var partial = new PartialPacket
                            {
                                Type = p.Type,
                                Position = i,
                                IsEnd = false,
                                TotalDataSize = p.Payload.Length,
                                Data = slice
                            };

                            partials.Add(partial);

                            currentIndex += MaxOutboundPacketSize;
                        }

                        byte[] endSlice = new byte[lastPacketSize];
                        Array.Copy(p.Payload, currentIndex, endSlice, 0, lastPacketSize);

                        var endPartial = new PartialPacket
                        {
                            Type = p.Type,
                            Position = packetCount - 1,
                            IsEnd = true,
                            TotalDataSize = p.Payload.Length,
                            Data = endSlice
                        };

                        if (p.HasId)
                        {
                            endPartial.HasId = true;
                            endPartial.Id = p.Id;
                        }

                        partials.Add(endPartial);

                        
                        _logger?.Trace($"Message send started ({partials.Count} packets)");
                        
                        Stopwatch s = Stopwatch.StartNew();
                        foreach (var msg in partials)
                        {
                            SendPartialPacket(msg);
                        }
                        
                        s.Stop();
                        _logger?.Trace($"Message send complete: {s.Elapsed.TotalMilliseconds} ms");
                    }
                    else
                    {
                        // Send without splitting
                        SendPacketFromMessage(p);
                    }
                }
                catch (Exception e) when (e is IOException || e is ObjectDisposedException)
                {
                    _logger?.Trace("Exception with the underlying socket or stream closed.");
                    Dispose();
                }
                catch (Exception e)
                {
                    _logger?.Trace(e, "Exception while dequeing message.");
                }
            }

            _logger?.Trace("Finished writting messages.");
        }

        internal void SendPacketFromMessage(Message p)
        {
            byte[] type = {(byte) p.Type};
            byte[] hasId = {p.HasId ? (byte) 1 : (byte) 0};
            byte[] isbuffered = {0};
            byte[] length = BitConverter.GetBytes(p.Length);
            byte[] arrData = p.Payload;

            byte[] b;

            if (p.HasId)
            {
                b = ByteArrayHelpers.Combine(type, hasId, p.Id, isbuffered, length, arrData);
            }
            else
            {
                b = ByteArrayHelpers.Combine(type, hasId, isbuffered, length, arrData);
            }

            _stream.Write(b, 0, b.Length);
        }

        internal void SendPartialPacket(PartialPacket p)
        {
            byte[] type = {(byte) p.Type};
            byte[] hasId = {p.HasId ? (byte) 1 : (byte) 0};
            byte[] isbuffered = {1};
            byte[] length = BitConverter.GetBytes(p.Data.Length);

            byte[] posBytes = BitConverter.GetBytes(p.Position);
            byte[] isEndBytes = p.IsEnd ? new byte[] {1} : new byte[] {0};
            byte[] totalLengthBytes = BitConverter.GetBytes(p.TotalDataSize);

            byte[] arrData = p.Data;

            byte[] b;
            if (p.HasId)
            {
                b = ByteArrayHelpers.Combine(type, hasId, p.Id, isbuffered, length, posBytes, isEndBytes, totalLengthBytes, arrData);
            }
            else
            {
                b = ByteArrayHelpers.Combine(type, hasId, isbuffered, length, posBytes, isEndBytes, totalLengthBytes, arrData);
            }

            Stopwatch s = Stopwatch.StartNew();
            _stream.Write(b, 0, b.Length);
            s.Stop();
            _logger?.Trace($"Partial sent complete ({p.Position}) ({p.TotalDataSize} bytes): {s.Elapsed.TotalMilliseconds} ms");
        }

        #region Closing and disposing

        public void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (IsDisposed)
                return;

            // Note that This will cause an IOException in the read loop.
            _stream?.Close();

            _outboundMessages?.CompleteAdding();
            _outboundMessages?.Dispose();
            _outboundMessages = null;

            IsDisposed = true;
        }

        #endregion
    }
}