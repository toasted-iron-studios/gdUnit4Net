// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Tests.Core.Execution;

using System.Collections.Concurrent;

using Api;

using GdUnit4.Core.Execution;

using static Assertions;

[TestSuite]
public class TestSuiteExecutionStageTest
{
    [TestCase]
    public async Task ExecutesCasesConcurrentlyOnIsolatedFixtures()
    {
        ConcurrentFixture.Reset();
        var tests = new List<TestCaseNode>
        {
            TestNode(nameof(ConcurrentFixture.First)),
            TestNode(nameof(ConcurrentFixture.Second))
        };
        using var suite = new GdUnit4.Core.Execution.TestSuite(typeof(ConcurrentFixture), tests, "ConcurrentFixture.cs");
        var listener = new EventListener();
        using var context = new GdUnit4.Core.Execution.ExecutionContext(suite, [listener], false, false)
        {
            IsCaptureStdOut = true
        };

        await new TestSuiteExecutionStage(suite).Execute(context);

        AssertThat(ConcurrentFixture.MaximumConcurrency).IsEqual(2);
        AssertThat(ConcurrentFixture.InstanceCount).IsEqual(2);
        AssertThat(listener.Events.SelectMany(testEvent => testEvent.Reports).Any(report => report.Type == ReportType.Stdout)).IsFalse();
        AssertProtocol(listener.Events, 2);
        foreach (var fixtureEvents in ConcurrentFixture.Events.GroupBy(value => value.InstanceId))
        {
            AssertThat(fixtureEvents.Select(value => value.Stage))
                .ContainsExactly("Before", "BeforeTest", "Test", "AfterTest", "After", "Dispose");
        }
    }

    [TestCase]
    public async Task ExecutesSynchronousCasesConcurrently()
    {
        SynchronousFixture.Reset();
        var tests = new List<TestCaseNode>
        {
            TestNode(nameof(SynchronousFixture.First)),
            TestNode(nameof(SynchronousFixture.Second))
        };
        using var suite = new GdUnit4.Core.Execution.TestSuite(typeof(SynchronousFixture), tests, "SynchronousFixture.cs");
        var listener = new EventListener();
        using var context = new GdUnit4.Core.Execution.ExecutionContext(suite, [listener], false, false);

        await new TestSuiteExecutionStage(suite).Execute(context);

        AssertThat(SynchronousFixture.MaximumConcurrency).IsEqual(2);
        AssertThat(SynchronousFixture.InstanceCount).IsEqual(2);
        AssertProtocol(listener.Events, 2);
    }

    [TestCase]
    public async Task ReportsPerFixtureBeforeAndAfterFailuresOnTheCase()
    {
        var test = TestNode(nameof(FailingHooksFixture.Test));
        using var suite = new GdUnit4.Core.Execution.TestSuite(typeof(FailingHooksFixture), [test], "FailingHooksFixture.cs");
        var listener = new EventListener();
        using var context = new GdUnit4.Core.Execution.ExecutionContext(suite, [listener], false, false);

        await new TestSuiteExecutionStage(suite).Execute(context);

        var testAfter = listener.Events.Single(testEvent => testEvent.Type == EventType.TestAfter);
        AssertThat(testAfter.Reports.Any(report => report.Message.Contains("before failure", StringComparison.Ordinal))).IsTrue();
        AssertThat(testAfter.Reports.Any(report => report.Message.Contains("after failure", StringComparison.Ordinal))).IsTrue();
        AssertProtocol(listener.Events, 1);
    }

    private static void AssertProtocol(IEnumerable<ITestEvent> events, int testCount)
    {
        var publishedEvents = events.ToList();
        AssertThat(publishedEvents.First().Type).IsEqual(EventType.SuiteBefore);
        AssertThat(publishedEvents.Last().Type).IsEqual(EventType.SuiteAfter);
        AssertThat(publishedEvents.Count(testEvent => testEvent.Type == EventType.SuiteBefore)).IsEqual(1);
        AssertThat(publishedEvents.Count(testEvent => testEvent.Type == EventType.SuiteAfter)).IsEqual(1);
        AssertThat(publishedEvents.Count(testEvent => testEvent.Type == EventType.TestBefore)).IsEqual(testCount);
        AssertThat(publishedEvents.Count(testEvent => testEvent.Type == EventType.TestAfter)).IsEqual(testCount);
    }

    private static TestCaseNode TestNode(string method) => new()
    {
        Id = Guid.NewGuid(),
        ParentId = Guid.Empty,
        ManagedMethod = method,
        LineNumber = 1,
        AttributeIndex = 0,
        RequireRunningGodotEngine = false
    };

    private sealed class EventListener : ITestEventListener
    {
        private readonly ConcurrentQueue<ITestEvent> events = new();

        public IEnumerable<ITestEvent> Events => events;

        public bool IsFailed { get; set; }

        public int CompletedTests { get; set; }

        public void PublishEvent(ITestEvent testEvent)
        {
            events.Enqueue(testEvent);
        }
    }

    public sealed class FailingHooksFixture
    {
        [Before]
        public void Before() => throw new InvalidOperationException("before failure");

        [After]
        public void After() => throw new InvalidOperationException("after failure");

        [TestCase]
        public void Test()
        {
        }
    }

    public sealed class SynchronousFixture
    {
        private static Barrier startBarrier = new(2);
        private static int activeCount;
        private static int instanceCount;
        private static int maximumConcurrency;

        public SynchronousFixture() => _ = Interlocked.Increment(ref instanceCount);

        public static int InstanceCount => instanceCount;

        public static int MaximumConcurrency => maximumConcurrency;

        [TestCase]
        public void First() => RunTest();

        [TestCase]
        public void Second() => RunTest();

        public static void Reset()
        {
            startBarrier.Dispose();
            startBarrier = new Barrier(2);
            activeCount = 0;
            instanceCount = 0;
            maximumConcurrency = 0;
        }

        private static void RunTest()
        {
            var active = Interlocked.Increment(ref activeCount);
            SetMaximumConcurrency(active);
            _ = startBarrier.SignalAndWait(TimeSpan.FromSeconds(5));
            _ = Interlocked.Decrement(ref activeCount);
        }

        private static void SetMaximumConcurrency(int active)
        {
            var maximum = maximumConcurrency;
            while (active > maximum)
            {
                var observed = Interlocked.CompareExchange(ref maximumConcurrency, active, maximum);
                if (observed == maximum)
                    return;
                maximum = observed;
            }
        }
    }

    public sealed class ConcurrentFixture : IDisposable
    {
        private static readonly ConcurrentQueue<(int InstanceId, string Stage)> RecordedEvents = new();
        private static TaskCompletionSource AllStarted = NewCompletionSource();
        private static int activeCount;
        private static int instanceCount;
        private static int maximumConcurrency;
        private static int startedCount;
        private readonly int instanceId = Interlocked.Increment(ref instanceCount);

        public static IEnumerable<(int InstanceId, string Stage)> Events => RecordedEvents;

        public static int InstanceCount => instanceCount;

        public static int MaximumConcurrency => maximumConcurrency;

        [Before]
        public void Before() => Record("Before");

        [After]
        public void After() => Record("After");

        [BeforeTest]
        public void BeforeTest() => Record("BeforeTest");

        [AfterTest]
        public void AfterTest() => Record("AfterTest");

        [TestCase]
        public async Task First() => await RunTest();

        [TestCase]
        public async Task Second() => await RunTest();

        public void Dispose() => Record("Dispose");

        public static void Reset()
        {
            RecordedEvents.Clear();
            AllStarted = NewCompletionSource();
            activeCount = 0;
            instanceCount = 0;
            maximumConcurrency = 0;
            startedCount = 0;
        }

        private static TaskCompletionSource NewCompletionSource() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task RunTest()
        {
            Record("Test");
            Console.WriteLine($"fixture {instanceId}");
            var active = Interlocked.Increment(ref activeCount);
            SetMaximumConcurrency(active);
            if (Interlocked.Increment(ref startedCount) == 2)
                AllStarted.SetResult();

            await AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            _ = Interlocked.Decrement(ref activeCount);
        }

        private static void SetMaximumConcurrency(int active)
        {
            var maximum = maximumConcurrency;
            while (active > maximum)
            {
                var observed = Interlocked.CompareExchange(ref maximumConcurrency, active, maximum);
                if (observed == maximum)
                    return;
                maximum = observed;
            }
        }

        private void Record(string stage) => RecordedEvents.Enqueue((instanceId, stage));
    }
}
