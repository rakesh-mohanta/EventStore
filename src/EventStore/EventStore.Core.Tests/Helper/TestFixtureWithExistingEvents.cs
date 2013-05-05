// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EventStore.Common.Utils;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Messages;
using EventStore.Core.Tests.Bus.Helpers;
using EventStore.Core.TransactionLog.LogRecords;
using NUnit.Framework;

namespace EventStore.Core.Tests.Helper
{
    public abstract class TestFixtureWithExistingEvents : TestFixtureWithReadWriteDispatchers,
                                                           IHandle<ClientMessage.ReadStreamEventsBackward>,
                                                           IHandle<ClientMessage.ReadStreamEventsForward>,
                                                           IHandle<ClientMessage.ReadAllEventsForward>,
                                                           IHandle<ClientMessage.WriteEvents>,
                                                           IHandle<ClientMessage.DeleteStream>
    {
        protected TestHandler<ClientMessage.ReadStreamEventsBackward> _listEventsHandler;

        protected readonly Dictionary<string, List<EventRecord>> _lastMessageReplies =
            new Dictionary<string, List<EventRecord>>();

        protected readonly SortedList<TFPos, EventRecord> _all = new SortedList<TFPos, EventRecord>();

        protected readonly HashSet<string> _deletedStreams = new HashSet<string>();

        private int _fakePosition = 100;
        private bool _allWritesSucceed;
        private readonly HashSet<string> _writesToSucceed = new HashSet<string>();
        private bool _allWritesQueueUp;
        private Queue<ClientMessage.WriteEvents> _writesQueue;
        private bool _readAllEnabled;

        protected TFPos ExistingEvent(string streamId, string eventType, string eventMetadata, string eventData)
        {
            List<EventRecord> list;
            if (!_lastMessageReplies.TryGetValue(streamId, out list) || list == null)
            {
                list = new List<EventRecord>();
                _lastMessageReplies[streamId] = list;
            }
            var eventRecord = new EventRecord(
                list.Count,
                new PrepareLogRecord(
                    _fakePosition, Guid.NewGuid(), Guid.NewGuid(), _fakePosition, 0, streamId, list.Count - 1,
                    DateTime.UtcNow, PrepareFlags.TransactionBegin | PrepareFlags.TransactionEnd, eventType,
                    Encoding.UTF8.GetBytes(eventData),
                    eventMetadata == null ? new byte[0] : Encoding.UTF8.GetBytes(eventMetadata)));
            list.Add(eventRecord);
            var eventPosition = new TFPos(_fakePosition + 50, _fakePosition);
            _all.Add(eventPosition, eventRecord);
            _fakePosition += 100;
            return eventPosition;
        }

        protected void EnableReadAll()
        {
            _readAllEnabled = true;
        }

        protected void NoStream(string streamId)
        {
            _lastMessageReplies[streamId] = null;
        }

        protected void DeletedStream(string streamId)
        {
            _deletedStreams.Add(streamId);
        }

        protected void AllWritesSucceed()
        {
            _allWritesSucceed = true;
        }

        protected void AllWritesToSucceed(string streamId)
        {
            _writesToSucceed.Add(streamId);
        }

        protected void AllWritesQueueUp()
        {
            _allWritesQueueUp = true;
        }

        protected void OneWriteCompletes()
        {
            var message = _writesQueue.Dequeue();
            ProcessWrite(message);
        }

        protected void AllWriteComplete()
        {
            while (_writesQueue.Count > 0)
                OneWriteCompletes();
        }

        [SetUp]
        public void setup1()
        {
            _writesQueue = new Queue<ClientMessage.WriteEvents>();
            _listEventsHandler = new TestHandler<ClientMessage.ReadStreamEventsBackward>();
            _bus.Subscribe(_listEventsHandler);
            _bus.Subscribe<ClientMessage.WriteEvents>(this);
            _bus.Subscribe<ClientMessage.ReadStreamEventsBackward>(this);
            _bus.Subscribe<ClientMessage.ReadStreamEventsForward>(this);
            _bus.Subscribe<ClientMessage.ReadAllEventsForward>(this);
            _bus.Subscribe<ClientMessage.DeleteStream>(this);
            _bus.Subscribe(_readDispatcher);
            _bus.Subscribe(_writeDispatcher);
            _bus.Subscribe(_ioDispatcher.StreamDeleter);
            _lastMessageReplies.Clear();
            _deletedStreams.Clear();
            _all.Clear();
            Given1();
            Given();
        }

        protected virtual void Given1()
        {
        }

        protected virtual void Given()
        {
        }

        void IHandle<ClientMessage.ReadStreamEventsBackward>.Handle(ClientMessage.ReadStreamEventsBackward message)
        {
            List<EventRecord> list;
            if (_deletedStreams.Contains(message.EventStreamId))
            {
                message.Envelope.ReplyWith(
                    new ClientMessage.ReadStreamEventsBackwardCompleted(
                        message.CorrelationId, message.EventStreamId, message.FromEventNumber, message.MaxCount,
                        ReadStreamResult.StreamDeleted, new ResolvedEvent[0], string.Empty, -1, -1, true, _fakePosition + 50));
                            
            }
            else if (_lastMessageReplies.TryGetValue(message.EventStreamId, out list))
            {
                if (list != null && list.Count > 0 && (list.Last().EventNumber >= message.FromEventNumber)
                    || (message.FromEventNumber == -1))
                {
                    ResolvedEvent[] records =
                        list.Safe()
                            .Reverse()
                            .SkipWhile(v => message.FromEventNumber != -1 && v.EventNumber > message.FromEventNumber)
                            .Take(message.MaxCount)
                            .Select(v => BuildEvent(v, message.ResolveLinks))
                            .ToArray();
                    message.Envelope.ReplyWith(
                        new ClientMessage.ReadStreamEventsBackwardCompleted(
                            message.CorrelationId, message.EventStreamId,
                            message.FromEventNumber == -1
                                ? (EnumerableExtensions.IsEmpty(list) ? -1 : list.Last().EventNumber)
                                : message.FromEventNumber, message.MaxCount, ReadStreamResult.Success, records,
                            string.Empty,
                            nextEventNumber: records.Length > 0 ? records.Last().Event.EventNumber - 1 : -1,
                            lastEventNumber: list.Safe().Any() ? list.Safe().Last().EventNumber : -1,
                            isEndOfStream: records.Length == 0 || records.Last().Event.EventNumber == 0,
                            lastCommitPosition: _fakePosition + 50));
                }
                else
                {
                    throw new NotImplementedException();
/*
                    message.Envelope.ReplyWith(
                            new ClientMessage.ReadStreamEventsBackwardCompleted(
                                    message.CorrelationId,
                                    message.EventStreamId,
                                    new EventLinkPair[0],
                                    ReadStreamResult.Success,
                                    nextEventNumber: -1,
                                    lastEventNumber: list.Safe().Last().EventNumber,
                                    isEndOfStream: true,// NOTE AN: don't know how to correctly determine this here
                                    lastCommitPosition: _lastPosition));
*/
                }
            }
        }


        public void Handle(ClientMessage.ReadStreamEventsForward message)
        {
            List<EventRecord> list;
            if (_deletedStreams.Contains(message.EventStreamId))
            {
                message.Envelope.ReplyWith(
                    new ClientMessage.ReadStreamEventsBackwardCompleted(
                        message.CorrelationId, message.EventStreamId, message.FromEventNumber, message.MaxCount,
                        ReadStreamResult.StreamDeleted, new ResolvedEvent[0], string.Empty, -1, -1, true, _fakePosition + 50));
                            
            }
            else if (_lastMessageReplies.TryGetValue(message.EventStreamId, out list))
            {
                if (list != null && list.Count > 0 && message.FromEventNumber >= 0)
                {
                    ResolvedEvent[] records =
                        list.Safe()
                            .SkipWhile(v => v.EventNumber < message.FromEventNumber)
                            .Take(message.MaxCount)
                            .Select(v => BuildEvent(v, message.ResolveLinks))
                            .ToArray();
                    message.Envelope.ReplyWith(
                        new ClientMessage.ReadStreamEventsForwardCompleted(
                            message.CorrelationId, message.EventStreamId,
                            message.FromEventNumber, message.MaxCount, ReadStreamResult.Success, records,
                            string.Empty,
                            nextEventNumber: records.Length > 0 ? records.Last().Event.EventNumber + 1 : -1,
                            lastEventNumber: list.Safe().Any() ? list.Safe().Last().EventNumber : -1,
                            isEndOfStream: records.Length == 0 || records.Last().Event.EventNumber == list.Last().EventNumber,
                            lastCommitPosition: _fakePosition + 50));
                }
                else
                {
                    if (list == null)
                    {
                        message.Envelope.ReplyWith(
                            new ClientMessage.ReadStreamEventsForwardCompleted(
                                message.CorrelationId, message.EventStreamId, message.FromEventNumber, message.MaxCount,
                                ReadStreamResult.NoStream, new ResolvedEvent[0], "", nextEventNumber: -1, lastEventNumber: -1,
                                isEndOfStream: true, // NOTE AN: don't know how to correctly determine this here
                                lastCommitPosition: _fakePosition + 50));
                        return;
                    }
                    throw new NotImplementedException();
/*
                    message.Envelope.ReplyWith(
                            new ClientMessage.ReadStreamEventsBackwardCompleted(
                                    message.CorrelationId,
                                    message.EventStreamId,
                                    new EventLinkPair[0],
                                    ReadStreamResult.Success,
                                    nextEventNumber: -1,
                                    lastEventNumber: list.Safe().Last().EventNumber,
                                    isEndOfStream: true,// NOTE AN: don't know how to correctly determine this here
                                    lastCommitPosition: _lastPosition));
*/
                }
            }
        }

        private ResolvedEvent BuildEvent(EventRecord x, bool resolveLinks)
        {
            if (x.EventType == "$>" && resolveLinks)
            {
                var parts = Encoding.UTF8.GetString(x.Data).Split('@');
                var list = _lastMessageReplies[parts[1]];
                var eventNumber = int.Parse(parts[0]);
                var target = list[eventNumber];

                return new ResolvedEvent(target, x);
            }
            else
                return new ResolvedEvent(x, null);
        }

        private ResolvedEvent BuildEvent(EventRecord x, bool resolveLinks, long commitPosition)
        {
            if (x.EventType == "$>" && resolveLinks)
            {
                var parts = Encoding.UTF8.GetString(x.Data).Split('@');
                var list = _lastMessageReplies[parts[1]];
                var eventNumber = int.Parse(parts[0]);
                var target = list[eventNumber];

                return new ResolvedEvent(target, x, commitPosition);
            }
            else
                return new ResolvedEvent(x, commitPosition);
        }

        public void Handle(ClientMessage.WriteEvents message)
        {
            if (_allWritesSucceed || _writesToSucceed.Contains(message.EventStreamId))
            {
                ProcessWrite(message);
            }
            else if (_allWritesQueueUp)
                _writesQueue.Enqueue(message);
        }

        private void ProcessWrite(ClientMessage.WriteEvents message)
        {
            List<EventRecord> list;
            if (!_lastMessageReplies.TryGetValue(message.EventStreamId, out list) || list == null)
            {
                list = new List<EventRecord>();
                _lastMessageReplies[message.EventStreamId] = list;
            }
            if (message.ExpectedVersion != EventStore.ClientAPI.ExpectedVersion.Any)
            {
                if (message.ExpectedVersion != list.Count - 1)
                {
                    message.Envelope.ReplyWith(new ClientMessage.WriteEventsCompleted(message.CorrelationId, OperationResult.WrongExpectedVersion, "wrong expected version"));
                    return;
                }
            }
            var eventRecords = (from e in message.Events
                                let eventNumber = list.Count
                                let tfPosition = (_fakePosition += 100)
                                select
                                    new EventRecord(
                                    eventNumber, tfPosition, message.CorrelationId, e.EventId, tfPosition, 0,
                                    message.EventStreamId, ExpectedVersion.Any, DateTime.UtcNow,
                                    PrepareFlags.SingleWrite, e.EventType, e.Data, e.Metadata)).ToArray();
            foreach (var eventRecord in eventRecords)
            {
                list.Add(eventRecord);
                _all.Add(new TFPos(_fakePosition + 50, eventRecord.LogPosition), eventRecord);
            }

            message.Envelope.ReplyWith(new ClientMessage.WriteEventsCompleted(message.CorrelationId, list.Count - message.Events.Length));
        }

        public void Handle(ClientMessage.DeleteStream message)
        {
            List<EventRecord> list;
            if (_deletedStreams.Contains(message.EventStreamId))
            {
                message.Envelope.ReplyWith(new ClientMessage.DeleteStreamCompleted(message.CorrelationId, OperationResult.StreamDeleted, string.Empty));
                return;
            }
            if (!_lastMessageReplies.TryGetValue(message.EventStreamId, out list) || list == null)
            {
                message.Envelope.ReplyWith(new ClientMessage.DeleteStreamCompleted(message.CorrelationId, OperationResult.WrongExpectedVersion, string.Empty));
                return;
            }
            _deletedStreams.Add(message.EventStreamId);
                message.Envelope.ReplyWith(new ClientMessage.DeleteStreamCompleted(message.CorrelationId, OperationResult.Success, string.Empty));
        }

        public void Handle(ClientMessage.ReadAllEventsForward message)
        {
            if (!_readAllEnabled)
                return;
            var from = new TFPos(message.CommitPosition, message.PreparePosition);
            var records = _all.SkipWhile(v => v.Key < from).Take(message.MaxCount).ToArray();
            var list = new List<ResolvedEvent>();
            var pos = from;
            var next = pos;
            var prev = new TFPos(pos.CommitPosition, Int64.MaxValue);
            foreach (KeyValuePair<TFPos, EventRecord> record in records)
            {
                pos = record.Key;
                next = new TFPos(pos.CommitPosition, pos.PreparePosition + 1);
                list.Add(BuildEvent(record.Value, message.ResolveLinks, record.Key.CommitPosition));
            }
            var events = list.ToArray();
            message.Envelope.ReplyWith(
                new ClientMessage.ReadAllEventsForwardCompleted(
                    message.CorrelationId, ReadAllResult.Success, "", events, message.MaxCount, pos, next, prev,
                    _fakePosition));
        }
    }
}

