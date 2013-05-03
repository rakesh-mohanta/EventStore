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
using System.Net;
using EventStore.Projections.Core.Services.Processing;
using NUnit.Framework;
using Newtonsoft.Json.Linq;
using HttpBehaviorSpecification = EventStore.Integration.Tests.Helpers.HttpBehaviorSpecification;

namespace EventStore.Integration.Tests.EventReader
{
    namespace event_reader
    {
        abstract class FeedReaderSpecification : HttpBehaviorSpecification
        {
            protected void PostEvent<T>(T data, ICredentials credentials = null)
            {
                var response = MakeJsonPost(
                    TestStream, new[] {new {EventId = Guid.NewGuid(), EventType = "event-type", Data = data}},
                    credentials);
                Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            }

            protected override void Given()
            {
                PostEvent(new {SampleData = 1});
                PostEvent(new {SampleData = 2});
                PostEvent(new {SampleData = 3});
                PostEvent(new {SampleData = 4});
            }
        }

        [TestFixture, Category("LongRunning")]
        class when_reading_a_single_event : FeedReaderSpecification
        {
            private HttpWebResponse _response;

            protected override void When()
            {
                _response = MakeJsonPost(
                    "/projections/read-events",
                    new
                        {
                            Query = new {Streams = new[] {TestStreamName}, All_Events = true},
                            Position = new JRaw(CheckpointTag.FromStreamPosition(TestStreamName, -1).ToJsonString())
                        });
            }

            [Test]
            public void returns_ok_status_code()
            {
                Assert.AreEqual(HttpStatusCode.OK, _response.StatusCode);
            }
        }
    }
}
