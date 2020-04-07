// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Xunit;
using Microsoft.Diagnostics.Tools.Counters;
using Microsoft.Diagnostics.Tools.Counters.Exporters;
using Microsoft.Diagnostics.Monitoring;

namespace DotnetCounters.UnitTests
{
    /// <summary>
    /// These test the some of the known providers that we provide as a default configuration for customers to use.
    /// </summary>
    public class CSVExporterTests
    {
        private List<string> ReadAllLines(MemoryStream s)
        {
            s.Position = 0;
            List<string> lines = new List<string>();
            StreamReader reader = new StreamReader(s);
            while(!reader.EndOfStream)
            {
                lines.Add(reader.ReadLine());
            }
            return lines;
        }

        [Fact]
        public void IncrementingCounterTest()
        {
            MemoryStream ms = new MemoryStream();
        	CounterCsvStreamExporter exporter = new CounterCsvStreamExporter(ms);
            exporter.PipelineStarted();
            for (int i = 0; i < 100; i++)
            {
                exporter.CounterPayloadReceived("myProvider", TestHelpers.GenerateCounterPayload(true, "incrementingCounterOne", i, 1, "Incrementing Counter One: " + i.ToString()));
            }
            exporter.PipelineStopped();

            List<string> lines = ReadAllLines(ms);
            Assert.Equal(101, lines.Count); // should be 101 including the headers

            string[] headerTokens = lines[0].Split(',');
            Assert.Equal("Provider", headerTokens[1]);
            Assert.Equal("Counter Name", headerTokens[2]);
            Assert.Equal("Counter Type", headerTokens[3]);
            Assert.Equal("Mean/Increment", headerTokens[4]);

            for (int i = 1; i < lines.Count; i++)
            {
                string[] tokens = lines[i].Split(',');

                Assert.Equal("myProvider", tokens[1]);
                Assert.Equal($"Incrementing Counter One: {i - 1} / 1 sec", tokens[2]);
                Assert.Equal("Rate", tokens[3]);
                Assert.Equal((i - 1).ToString(), tokens[4]);
            }
        }

        [Fact]
        public void CounterTest()
        {
            MemoryStream ms = new MemoryStream();
            CounterCsvStreamExporter exporter = new CounterCsvStreamExporter(ms);
            exporter.PipelineStarted();
            for (int i = 0; i < 10; i++)
            {
                exporter.CounterPayloadReceived("myProvider", TestHelpers.GenerateCounterPayload(false, "counterOne", i, 1, "Counter One: " + i.ToString()));
            }
            exporter.PipelineStopped();

            List<string> lines = ReadAllLines(ms);
            Assert.Equal(11, lines.Count); // should be 11 including the headers

            string[] headerTokens = lines[0].Split(',');
            Assert.Equal("Provider", headerTokens[1]);
            Assert.Equal("Counter Name", headerTokens[2]);
            Assert.Equal("Counter Type", headerTokens[3]);
            Assert.Equal("Mean/Increment", headerTokens[4]);


            for (int i = 1; i < lines.Count; i++)
            {
                string[] tokens = lines[i].Split(',');

                Assert.Equal("myProvider", tokens[1]);
                Assert.Equal("Counter One: " + (i - 1).ToString(), tokens[2]);
                Assert.Equal("Metric", tokens[3]);
                Assert.Equal((i - 1).ToString(), tokens[4]);
            }
        }

        [Fact]
        public void DifferentDisplayRateTest()
        {
            MemoryStream ms = new MemoryStream();
            CounterCsvStreamExporter exporter = new CounterCsvStreamExporter(ms);
            exporter.PipelineStarted();
            for (int i = 0; i < 100; i++)
            {
                exporter.CounterPayloadReceived("myProvider", TestHelpers.GenerateCounterPayload(true, "incrementingCounterOne", i, 60, "Incrementing Counter One: " + i.ToString()));
            }
            exporter.PipelineStopped();


            List<string> lines = ReadAllLines(ms);
            Assert.Equal(101, lines.Count); // should be 101 including the headers

            string[] headerTokens = lines[0].Split(',');
            Assert.Equal("Provider", headerTokens[1]);
            Assert.Equal("Counter Name", headerTokens[2]);
            Assert.Equal("Counter Type", headerTokens[3]);
            Assert.Equal("Mean/Increment", headerTokens[4]);

            for (int i = 1; i < lines.Count; i++)
            {
                string[] tokens = lines[i].Split(',');

                Assert.Equal("myProvider", tokens[1]);
                Assert.Equal($"Incrementing Counter One: {i - 1} / 60 sec", tokens[2]);
                Assert.Equal("Rate", tokens[3]);
                Assert.Equal(((i - 1) * 60).ToString(), tokens[4]);
            }
        }

        [Fact]
        public void DisplayUnitsTest()
        {
            MemoryStream ms = new MemoryStream();
            CounterCsvStreamExporter exporter = new CounterCsvStreamExporter(ms);
            exporter.PipelineStarted();
            for (int i = 0; i < 100; i++)
            {
                exporter.CounterPayloadReceived("myProvider", TestHelpers.GenerateCounterPayload(true, "allocRateGen", i, 60, "Allocation Rate Gen: " + i.ToString(), "MB"));
            }
            exporter.PipelineStopped();

            List<string> lines = ReadAllLines(ms);
            Assert.Equal(101, lines.Count); // should be 101 including the headers

            string[] headerTokens = lines[0].Split(',');
            Assert.Equal("Provider", headerTokens[1]);
            Assert.Equal("Counter Name", headerTokens[2]);
            Assert.Equal("Counter Type", headerTokens[3]);
            Assert.Equal("Mean/Increment", headerTokens[4]);

            for (int i = 1; i < lines.Count; i++)
            {
                string[] tokens = lines[i].Split(',');

                Assert.Equal("myProvider", tokens[1]);
                Assert.Equal($"Allocation Rate Gen: {i - 1} / 60 sec (MB)", tokens[2]);
                Assert.Equal("Rate", tokens[3]);
                Assert.Equal(((i - 1) * 60).ToString(), tokens[4]);
            }
        }
    }
}
