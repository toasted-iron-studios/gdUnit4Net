// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System.Diagnostics.CodeAnalysis;

using Data;

using Reporting;

using Signals;

using static Api.ReportType;

internal sealed class TestSuiteExecutionStage : IExecutionStage
{
    public TestSuiteExecutionStage(TestSuite testSuite) => TestSuite = testSuite;

    private TestSuite TestSuite { get; }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Fixture contexts own and dispose their child execution contexts")]
    public async Task Execute(ExecutionContext testSuiteContext)
    {
        testSuiteContext.FireBeforeEvent();
        try
        {
            if (TestSuite.FixtureType.IsDefined(typeof(SequentialAttribute), true))
            {
                foreach (var testCase in TestSuite.TestCases)
                {
                    await ExecuteTestCase(testSuiteContext, testCase)
                        .ConfigureAwait(true);
                }
            }
            else
            {
                var tasks = TestSuite.TestCases
                    .Select(testCase => ScheduleTestCase(testSuiteContext, testCase))
                    .ToArray();
                await Task.WhenAll(tasks)
                    .ConfigureAwait(true);
            }
        }
        finally
        {
            try
            {
                if (testSuiteContext.IsEngineMode)
                    GodotSignalCollector.Instance.Clean();
                Utils.ClearTempDir();
            }
            finally
            {
                testSuiteContext.FireAfterEvent();
            }
        }
    }

    private static async Task RunTestCase(
        BeforeTestExecutionStage beforeTestStage,
        AfterTestExecutionStage afterTestStage,
        ExecutionContext executionContext,
        TestCase testCase,
        TestCaseAttribute stageAttribute,
        params object?[] methodArguments)
    {
        try
        {
            await beforeTestStage
                .Execute(executionContext)
                .ConfigureAwait(true);

            if (!executionContext.IsSkipped)
            {
                using ExecutionContext context = new(executionContext, methodArguments);
                await new TestCaseExecutionStage(context.TestCaseName, testCase, stageAttribute)
                    .Execute(context)
                    .ConfigureAwait(true);
            }
        }
        finally
        {
            await afterTestStage
                .Execute(executionContext)
                .ConfigureAwait(true);
        }
    }

    private async Task ExecuteTestCase(ExecutionContext testSuiteContext, TestCase testCase)
    {
        using var fixture = TestSuite.CreateFixture(testCase);
        using var fixtureContext = testSuiteContext.CreateFixtureContext(fixture, testCase);
        var beforeStage = new BeforeExecutionStage(fixture, false);
        var afterStage = new AfterExecutionStage(fixture, false);
        var beforeTestStage = new BeforeTestExecutionStage(fixture);
        var afterTestStage = new AfterTestExecutionStage(fixture, testCase.HasDataPoint, false);

        await beforeStage
            .Execute(fixtureContext)
            .ConfigureAwait(true);
        var testCaseContext = new ExecutionContext(fixtureContext, testCase);
        try
        {
            if (testCase.HasDataPoint)
            {
                await RunTestCaseWithDataPoint(beforeTestStage, afterTestStage, testCaseContext, testCase)
                    .ConfigureAwait(true);
            }
            else
            {
                await RunTestCase(beforeTestStage, afterTestStage, testCaseContext, testCase, testCase.TestCaseAttribute, testCase.Arguments)
                    .ConfigureAwait(true);
            }
        }
        finally
        {
            testCaseContext.Dispose();
            try
            {
                await afterStage
                    .Execute(fixtureContext)
                    .ConfigureAwait(true);
            }
            finally
            {
                if (!testCase.HasDataPoint)
                    testCaseContext.FireAfterTestEvent();
            }
        }
    }

    private Task ScheduleTestCase(ExecutionContext testSuiteContext, TestCase testCase) =>
        testSuiteContext.IsEngineMode
            ? ExecuteTestCaseDeferred(testSuiteContext, testCase)
            : Task.Run(() => ExecuteTestCase(testSuiteContext, testCase));

    private async Task ExecuteTestCaseDeferred(ExecutionContext testSuiteContext, TestCase testCase)
    {
        await Task.Yield();
        await ExecuteTestCase(testSuiteContext, testCase)
            .ConfigureAwait(true);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Child execution contexts are disposed in the scope that creates them")]
    private async Task RunTestCaseWithDataPoint(
        BeforeTestExecutionStage beforeTestStage,
        AfterTestExecutionStage afterTestStage,
        ExecutionContext executionContext,
        TestCase testCase)
    {
        executionContext.FireBeforeTestEvent();

        try
        {
            var testAttribute = testCase.TestCaseAttributes.First();
            if (DataPointValueProvider.IsAsyncDataPoint(testCase))
            {
                try
                {
                    var timeout = executionContext.GetExecutionTimeout(testAttribute);
                    await foreach (var dataPointValues in DataPointValueProvider.GetDataAsync(testCase, timeout).ConfigureAwait(false))
                    {
                        var displayName = TestCase.BuildDisplayName(testCase.Name, new TestCaseAttribute(dataPointValues));
                        using ExecutionContext testCaseContext = new(executionContext, displayName);
                        await RunTestCase(beforeTestStage, afterTestStage, testCaseContext, testCase, testAttribute, dataPointValues)
                            .ConfigureAwait(true);
                    }
                }
                catch (AsyncDataPointCanceledException e)
                {
                    if (!executionContext.IsExpectingToFailWithException(e, testCase.MethodInfo))
                    {
                        executionContext.ReportCollector.Consume(
                            new TestReport(
                                Interrupted,
                                executionContext.CurrentTestCase?.Line ?? -1,
                                e.Message,
                                e.StackTrace));
                    }
                }
            }
            else
            {
                foreach (var dataPointValues in DataPointValueProvider.GetData(testCase))
                {
                    var displayName = TestCase.BuildDisplayName(testCase.Name, new TestCaseAttribute(dataPointValues));
                    using ExecutionContext testCaseContext = new(executionContext, displayName);
                    await RunTestCase(beforeTestStage, afterTestStage, testCaseContext, testCase, testAttribute, dataPointValues)
                        .ConfigureAwait(true);
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            executionContext.ReportCollector.Consume(new TestReport(Failure, executionContext.CurrentTestCase?.Line ?? -1, e.Message, e.StackTrace));
        }

        executionContext.FireAfterTestEvent();
    }
}
