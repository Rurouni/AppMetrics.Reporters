﻿// Copyright (c) Allan Hardy. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using App.Metrics.Abstractions.ReservoirSampling;
using App.Metrics.Apdex;
using App.Metrics.Core;
using App.Metrics.Counter;
using App.Metrics.Extensions.Reporting.InfluxDB;
using App.Metrics.Extensions.Reporting.InfluxDB.Client;
using App.Metrics.Gauge;
using App.Metrics.Health;
using App.Metrics.Histogram;
using App.Metrics.Infrastructure;
using App.Metrics.Meter;
using App.Metrics.ReservoirSampling.ExponentialDecay;
using App.Metrics.Tagging;
using App.Metrics.Timer;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace App.Metrics.Extensions.Middleware.Integration.Facts
{
    public class InfluxDbReporterTests
    {
        private const string MultidimensionalMetricNameSuffix = "|host:server1,env:staging";
        private readonly Lazy<IReservoir> _defaultReservoir = new Lazy<IReservoir>(() => new DefaultForwardDecayingReservoir());
        private readonly MetricTags _tags = new MetricTags(new[] { "host", "env" }, new[] { "server1", "staging" });

        [Fact]
        public void can_clear_payload()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var meter = new DefaultMeterMetric(clock);
            meter.Mark(new MetricSetItem("item1", "value1"), 1);
            meter.Mark(new MetricSetItem("item2", "value2"), 1);
            var meterValueSource = new MeterValueSource(
                "test meter",
                ConstantValue.Provider(meter.Value),
                Unit.None,
                TimeUnit.Milliseconds,
                MetricTags.Empty);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", meterValueSource);

            var sr = new StringWriter();
            payloadBuilder.Payload().Format(sr);
            sr.ToString().Should().NotBeNullOrWhiteSpace();

            payloadBuilder.Clear();

            payloadBuilder.Payload().Should().BeNull();
        }

        [Fact]
        public void can_report_apdex()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var gauge = new DefaultApdexMetric(_defaultReservoir, clock, false);
            var apdexValueSource = new ApdexValueSource(
                "test apdex",
                ConstantValue.Provider(gauge.Value),
                MetricTags.Empty,
                false);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", apdexValueSource);

            payloadBuilder.PayloadFormatted().Should().Be("test__test_apdex samples=0i,score=0,satisfied=0i,tolerating=0i,frustrating=0i\n");
        }

        [Fact]
        public void can_report_apdex__when_multidimensional()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var gauge = new DefaultApdexMetric(_defaultReservoir, clock, false);
            var apdexValueSource = new ApdexValueSource(
                "test apdex" + MultidimensionalMetricNameSuffix,
                ConstantValue.Provider(gauge.Value),
                _tags,
                resetOnReporting: false);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", apdexValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be("test__test_apdex,host=server1,env=staging samples=0i,score=0,satisfied=0i,tolerating=0i,frustrating=0i\n");
        }

        [Fact]
        public void can_report_apdex_with_tags()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var gauge = new DefaultApdexMetric(_defaultReservoir, clock, false);
            var apdexValueSource = new ApdexValueSource(
                "test apdex",
                ConstantValue.Provider(gauge.Value),
                new MetricTags(new[] { "key1", "key2" }, new[] { "value1", "value2" }),
                false);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", apdexValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be("test__test_apdex,key1=value1,key2=value2 samples=0i,score=0,satisfied=0i,tolerating=0i,frustrating=0i\n");
        }

        [Fact]
        public void can_report_apdex_with_tags_when_multidimensional()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var gauge = new DefaultApdexMetric(_defaultReservoir, clock, false);
            var apdexValueSource = new ApdexValueSource(
                "test apdex" + MultidimensionalMetricNameSuffix,
                ConstantValue.Provider(gauge.Value),
                MetricTags.Concat(_tags, new MetricTags("anothertag", "thevalue")),
                resetOnReporting: false);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", apdexValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_apdex,host=server1,env=staging,anothertag=thevalue samples=0i,score=0,satisfied=0i,tolerating=0i,frustrating=0i\n");
        }

        [Fact]
        public void can_report_counter_with_items()
        {
            var metricsMock = new Mock<IMetrics>();
            var counter = new DefaultCounterMetric();
            counter.Increment(new MetricSetItem("item1", "value1"), 1);
            counter.Increment(new MetricSetItem("item2", "value2"), 1);
            var counterValueSource = new CounterValueSource(
                "test counter",
                ConstantValue.Provider(counter.Value),
                Unit.None,
                MetricTags.Empty);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", counterValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_counter__items,item=item1:value1 total=1i,percent=50\ntest__test_counter__items,item=item2:value2 total=1i,percent=50\ntest__test_counter value=2i\n");
        }

        [Fact]
        public void can_report_counter_with_items_and_tags()
        {
            var metricsMock = new Mock<IMetrics>();
            var counter = new DefaultCounterMetric();
            counter.Increment(new MetricSetItem("item1", "value1"), 1);
            counter.Increment(new MetricSetItem("item2", "value2"), 1);
            var counterValueSource = new CounterValueSource(
                "test counter",
                ConstantValue.Provider(counter.Value),
                Unit.None,
                new MetricTags(new[] { "key1", "key2" }, new[] { "value1", "value2" }));
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", counterValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_counter__items,key1=value1,key2=value2,item=item1:value1 total=1i,percent=50\ntest__test_counter__items,key1=value1,key2=value2,item=item2:value2 total=1i,percent=50\ntest__test_counter,key1=value1,key2=value2 value=2i\n");
        }

        [Fact]
        public void can_report_counter_with_items_tags_when_multidimensional()
        {
            var counterTags = new MetricTags(new[] { "key1", "key2" }, new[] { "value1", "value2" });
            var metricsMock = new Mock<IMetrics>();
            var counter = new DefaultCounterMetric();
            counter.Increment(new MetricSetItem("item1", "value1"), 1);
            counter.Increment(new MetricSetItem("item2", "value2"), 1);
            var counterValueSource = new CounterValueSource(
                "test counter" + MultidimensionalMetricNameSuffix,
                ConstantValue.Provider(counter.Value),
                Unit.None,
                MetricTags.Concat(_tags, counterTags));
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", counterValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_counter__items,host=server1,env=staging,key1=value1,key2=value2,item=item1:value1 total=1i,percent=50\ntest__test_counter__items,host=server1,env=staging,key1=value1,key2=value2,item=item2:value2 total=1i,percent=50\ntest__test_counter,host=server1,env=staging,key1=value1,key2=value2 value=2i\n");
        }

        [Fact]
        public void can_report_counter_with_items_with_option_not_to_report_percentage()
        {
            var metricsMock = new Mock<IMetrics>();
            var counter = new DefaultCounterMetric();
            counter.Increment(new MetricSetItem("item1", "value1"), 1);
            counter.Increment(new MetricSetItem("item2", "value2"), 1);
            var counterValueSource = new CounterValueSource(
                "test counter",
                ConstantValue.Provider(counter.Value),
                Unit.None,
                MetricTags.Empty,
                reportItemPercentages: false);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", counterValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_counter__items,item=item1:value1 total=1i\ntest__test_counter__items,item=item2:value2 total=1i\ntest__test_counter value=2i\n");
        }

        [Fact]
        public void can_report_counters()
        {
            var metricsMock = new Mock<IMetrics>();
            var counter = new DefaultCounterMetric();
            counter.Increment(1);
            var counterValueSource = new CounterValueSource(
                "test counter",
                ConstantValue.Provider(counter.Value),
                Unit.None,
                MetricTags.Empty);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", counterValueSource);

            payloadBuilder.PayloadFormatted().Should().Be("test__test_counter value=1i\n");
        }

        [Fact]
        public void can_report_counters__when_multidimensional()
        {
            var metricsMock = new Mock<IMetrics>();
            var counter = new DefaultCounterMetric();
            counter.Increment(1);
            var counterValueSource = new CounterValueSource(
                "test counter" + MultidimensionalMetricNameSuffix,
                ConstantValue.Provider(counter.Value),
                Unit.None,
                _tags);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", counterValueSource);

            payloadBuilder.PayloadFormatted().Should().Be("test__test_counter,host=server1,env=staging value=1i\n");
        }

        [Fact]
        public void can_report_gauges()
        {
            var metricsMock = new Mock<IMetrics>();
            var gauge = new FunctionGauge(() => 1);
            var gaugeValueSource = new GaugeValueSource(
                "test gauge",
                ConstantValue.Provider(gauge.Value),
                Unit.None,
                MetricTags.Empty);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", gaugeValueSource);

            payloadBuilder.PayloadFormatted().Should().Be("test__test_gauge value=1\n");
        }

        [Fact]
        public void can_report_gauges__when_multidimensional()
        {
            var metricsMock = new Mock<IMetrics>();
            var gauge = new FunctionGauge(() => 1);
            var gaugeValueSource = new GaugeValueSource(
                "gauge-group" + MultidimensionalMetricNameSuffix,
                ConstantValue.Provider(gauge.Value),
                Unit.None,
                _tags);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", gaugeValueSource);

            payloadBuilder.PayloadFormatted().Should().Be("test__gauge-group,host=server1,env=staging value=1\n");
        }

        [Fact]
        public void can_report_histograms()
        {
            var metricsMock = new Mock<IMetrics>();
            var histogram = new DefaultHistogramMetric(_defaultReservoir);
            histogram.Update(1000, "client1");
            var histogramValueSource = new HistogramValueSource(
                "test histogram",
                ConstantValue.Provider(histogram.Value),
                Unit.None,
                MetricTags.Empty);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", histogramValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_histogram samples=1i,last=1000,count.hist=1i,min=1000,max=1000,mean=1000,median=1000,stddev=0,p999=1000,p99=1000,p98=1000,p95=1000,p75=1000,user.last=\"client1\",user.min=\"client1\",user.max=\"client1\"\n");
        }

        [Fact]
        public void can_report_histograms_when_multidimensional()
        {
            var metricsMock = new Mock<IMetrics>();
            var histogram = new DefaultHistogramMetric(_defaultReservoir);
            histogram.Update(1000, "client1");
            var histogramValueSource = new HistogramValueSource(
                "test histogram" + MultidimensionalMetricNameSuffix,
                ConstantValue.Provider(histogram.Value),
                Unit.None,
                _tags);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", histogramValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_histogram,host=server1,env=staging samples=1i,last=1000,count.hist=1i,min=1000,max=1000,mean=1000,median=1000,stddev=0,p999=1000,p99=1000,p98=1000,p95=1000,p75=1000,user.last=\"client1\",user.min=\"client1\",user.max=\"client1\"\n");
        }

        [Fact]
        public void can_report_meters()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var meter = new DefaultMeterMetric(clock);
            meter.Mark(1);
            var meterValueSource = new MeterValueSource(
                "test meter",
                ConstantValue.Provider(meter.Value),
                Unit.None,
                TimeUnit.Milliseconds,
                MetricTags.Empty);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", meterValueSource);

            payloadBuilder.PayloadFormatted().Should().Be("test__test_meter count.meter=1i,rate1m=0,rate5m=0,rate15m=0\n");
        }

        [Fact]
        public void can_report_meters_when_multidimensional()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var meter = new DefaultMeterMetric(clock);
            meter.Mark(1);
            var meterValueSource = new MeterValueSource(
                "test meter" + MultidimensionalMetricNameSuffix,
                ConstantValue.Provider(meter.Value),
                Unit.None,
                TimeUnit.Milliseconds,
                _tags);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", meterValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be("test__test_meter,host=server1,env=staging count.meter=1i,rate1m=0,rate5m=0,rate15m=0\n");
        }

        [Fact]
        public void can_report_meters_with_items()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var meter = new DefaultMeterMetric(clock);
            meter.Mark(new MetricSetItem("item1", "value1"), 1);
            meter.Mark(new MetricSetItem("item2", "value2"), 1);
            var meterValueSource = new MeterValueSource(
                "test meter",
                ConstantValue.Provider(meter.Value),
                Unit.None,
                TimeUnit.Milliseconds,
                MetricTags.Empty);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", meterValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_meter__items,item=item1:value1 count.meter=1i,rate1m=0,rate5m=0,rate15m=0,percent=50\ntest__test_meter__items,item=item2:value2 count.meter=1i,rate1m=0,rate5m=0,rate15m=0,percent=50\ntest__test_meter count.meter=2i,rate1m=0,rate5m=0,rate15m=0\n");
        }

        [Fact]
        public void can_report_meters_with_items_tags_when_multidimensional()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var meter = new DefaultMeterMetric(clock);
            meter.Mark(new MetricSetItem("item1", "value1"), 1);
            meter.Mark(new MetricSetItem("item2", "value2"), 1);
            var meterValueSource = new MeterValueSource(
                "test meter" + MultidimensionalMetricNameSuffix,
                ConstantValue.Provider(meter.Value),
                Unit.None,
                TimeUnit.Milliseconds,
                _tags);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", meterValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_meter__items,host=server1,env=staging,item=item1:value1 count.meter=1i,rate1m=0,rate5m=0,rate15m=0,percent=50\ntest__test_meter__items,host=server1,env=staging,item=item2:value2 count.meter=1i,rate1m=0,rate5m=0,rate15m=0,percent=50\ntest__test_meter,host=server1,env=staging count.meter=2i,rate1m=0,rate5m=0,rate15m=0\n");
        }

        [Fact]
        public void can_report_timers()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var timer = new DefaultTimerMetric(_defaultReservoir, clock);
            timer.Record(1000, TimeUnit.Milliseconds, "client1");
            var timerValueSource = new TimerValueSource(
                "test timer",
                ConstantValue.Provider(timer.Value),
                Unit.None,
                TimeUnit.Milliseconds,
                TimeUnit.Milliseconds,
                MetricTags.Empty);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", timerValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_timer count.meter=1i,rate1m=0,rate5m=0,rate15m=0,samples=1i,last=1000,count.hist=1i,min=1000,max=1000,mean=1000,median=1000,stddev=0,p999=1000,p99=1000,p98=1000,p95=1000,p75=1000,user.last=\"client1\",user.min=\"client1\",user.max=\"client1\"\n");
        }

        [Fact]
        public void can_report_timers__when_multidimensional()
        {
            var metricsMock = new Mock<IMetrics>();
            var clock = new TestClock();
            var timer = new DefaultTimerMetric(_defaultReservoir, clock);
            timer.Record(1000, TimeUnit.Milliseconds, "client1");
            var timerValueSource = new TimerValueSource(
                "test timer" + MultidimensionalMetricNameSuffix,
                ConstantValue.Provider(timer.Value),
                Unit.None,
                TimeUnit.Milliseconds,
                TimeUnit.Milliseconds,
                _tags);
            var payloadBuilder = new LineProtocolPayloadBuilder();
            var reporter = CreateReporter(payloadBuilder);

            reporter.StartReportRun(metricsMock.Object);
            reporter.ReportMetric("test", timerValueSource);

            payloadBuilder.PayloadFormatted().
                           Should().
                           Be(
                               "test__test_timer,host=server1,env=staging count.meter=1i,rate1m=0,rate5m=0,rate15m=0,samples=1i,last=1000,count.hist=1i,min=1000,max=1000,mean=1000,median=1000,stddev=0,p999=1000,p99=1000,p98=1000,p95=1000,p75=1000,user.last=\"client1\",user.min=\"client1\",user.max=\"client1\"\n");
        }

        [Fact]
        public async Task on_end_report_clears_playload()
        {
            var metricsMock = new Mock<IMetrics>();
            var payloadBuilderMock = new Mock<ILineProtocolPayloadBuilder>();
            payloadBuilderMock.Setup(p => p.Clear());
            var reporter = CreateReporter(payloadBuilderMock.Object);

            await reporter.EndAndFlushReportRunAsync(metricsMock.Object).ConfigureAwait(false);

            payloadBuilderMock.Verify(p => p.Clear(), Times.Once);
        }

        [Fact]
        public void when_disposed_clears_playload()
        {
            var payloadBuilderMock = new Mock<ILineProtocolPayloadBuilder>();
            payloadBuilderMock.Setup(p => p.Clear());
            var reporter = CreateReporter(payloadBuilderMock.Object);

            reporter.Dispose();

            payloadBuilderMock.Verify(p => p.Clear(), Times.Once);
        }

        private static InfluxDbReporter CreateReporter(ILineProtocolPayloadBuilder payloadBuilder)
        {
            var lineProtocolClientMock = new Mock<ILineProtocolClient>();
            var reportInterval = TimeSpan.FromSeconds(1);
            var loggerFactory = new LoggerFactory();
            var settings = new InfluxDBReporterSettings();

            return new InfluxDbReporter(
                lineProtocolClientMock.Object,
                payloadBuilder,
                reportInterval,
                loggerFactory,
                settings.MetricNameFormatter);
        }
    }
}