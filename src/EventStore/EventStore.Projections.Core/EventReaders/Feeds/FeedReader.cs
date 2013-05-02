﻿// Copyright (c) 2012, Event Store LLP
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
using EventStore.Core.Bus;
using EventStore.Core.Messaging;
using EventStore.Projections.Core.Messages;
using EventStore.Projections.Core.Messages.EventReaders.Feeds;
using EventStore.Projections.Core.Services;
using EventStore.Projections.Core.Services.Processing;

namespace EventStore.Projections.Core.EventReaders.Feeds
{
    public class FeedReader : IHandle<EventReaderSubscriptionMessage.CommittedEventReceived>,
                              IHandle<EventReaderSubscriptionMessage.EofReached>,
                              IHandle<EventReaderSubscriptionMessage.CheckpointSuggested>
    {
        private readonly
            PublishSubscribeDispatcher<ReaderSubscriptionManagement.Subscribe, ReaderSubscriptionManagement.ReaderSubscriptionManagementMessage, EventReaderSubscriptionMessage> _subscriptionDispatcher;

        private readonly QuerySourcesDefinition _querySource;
        private readonly CheckpointTag _fromPosition;
        private readonly int _maxEvents;
        private readonly Guid _requestCorrelationId;

        private readonly List<TaggedResolvedEvent> _batch = new List<TaggedResolvedEvent>();
        private readonly IEnvelope _replyEnvelope;

        private Guid _subscriptionId;
        private CheckpointTag _lastReaderPosition;

        public FeedReader(
            PublishSubscribeDispatcher<ReaderSubscriptionManagement.Subscribe, ReaderSubscriptionManagement.ReaderSubscriptionManagementMessage, EventReaderSubscriptionMessage> subscriptionDispatcher, QuerySourcesDefinition querySource, CheckpointTag fromPosition, int maxEvents,
            Guid requestCorrelationId, IEnvelope replyEnvelope)
        {
            if (subscriptionDispatcher == null) throw new ArgumentNullException("subscriptionDispatcher");
            if (querySource == null) throw new ArgumentNullException("querySource");
            if (fromPosition == null) throw new ArgumentNullException("fromPosition");
            if (replyEnvelope == null) throw new ArgumentNullException("replyEnvelope");

            _subscriptionDispatcher = subscriptionDispatcher;
            _querySource = querySource;
            _fromPosition = fromPosition;
            _maxEvents = maxEvents;
            _requestCorrelationId = requestCorrelationId;
            _replyEnvelope = replyEnvelope;
        }

        public void Start()
        {
            var readerStrategy = ReaderStrategy.Create(_querySource);
            //TODO: make reader mode explicit
            var readerOptions = new ReaderSubscriptionOptions(1024*1024, _maxEvents + 1, false, _maxEvents);
            _subscriptionId =
                _subscriptionDispatcher.PublishSubscribe(
                    new ReaderSubscriptionManagement.Subscribe(
                        Guid.NewGuid(), _fromPosition, readerStrategy, readerOptions), this);
        }

        public void Handle(EventReaderSubscriptionMessage.CommittedEventReceived message)
        {
            _lastReaderPosition = message.CheckpointTag;
            _batch.Add(new TaggedResolvedEvent(message.Data, message.EventCategory, message.CheckpointTag));
        }

        public void Handle(EventReaderSubscriptionMessage.EofReached message)
        {
            _lastReaderPosition = message.CheckpointTag;
            Reply();
            Unsubscribe();
        }

        public void Handle(EventReaderSubscriptionMessage.CheckpointSuggested message)
        {
            _lastReaderPosition = message.CheckpointTag;
            Reply();
            Unsubscribe();
        }

        private void Unsubscribe()
        {
            _subscriptionDispatcher.Cancel(_subscriptionId);
        }

        private void Reply()
        {
            _replyEnvelope.ReplyWith(new FeedReaderMessage.FeedPage(_requestCorrelationId, _batch.ToArray(), _lastReaderPosition));
        }

        public static FeedReader Create(PublishSubscribeDispatcher<ReaderSubscriptionManagement.Subscribe, ReaderSubscriptionManagement.ReaderSubscriptionManagementMessage, EventReaderSubscriptionMessage> publishSubscribeDispatcher, FeedReaderMessage.ReadPage message)
        {
            return new FeedReader(publishSubscribeDispatcher, message.QuerySource, message.FromPosition, message.MaxEvents, message.CorrelationId, message.Envelope);
        }
    }
}
