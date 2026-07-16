// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Asserts;

using Reporting;

using Signals;

using static Api.ReportType;

internal class AfterTestExecutionStage : ExecutionStage<AfterTestAttribute>
{
    private readonly bool cleanGodotSignals;
    private readonly bool publishTestEvent;

    public AfterTestExecutionStage(TestSuite testSuite, bool publishTestEvent = true, bool cleanGodotSignals = true)
        : base("AfterTest", testSuite.FixtureType)
    {
        this.publishTestEvent = publishTestEvent;
        this.cleanGodotSignals = cleanGodotSignals;
    }

    public override async Task Execute(ExecutionContext context)
    {
        if (!context.IsSkipped)
        {
            if (context.IsEngineMode && cleanGodotSignals)
                GodotSignalCollector.Instance.Clean();
            context.MemoryPool.SetActive(StageName);
            await base
                .Execute(context)
                .ConfigureAwait(true);
            await context.MemoryPool
                .Gc()
                .ConfigureAwait(true);
            if (context.MemoryPool.OrphanCount > 0)
                context.ReportCollector.PushFront(new TestReport(Warning, 0, ReportOrphans(context)));
        }

        if (publishTestEvent)
            context.FireAfterTestEvent();
    }

    private static AfterTestAttribute? AfterTestAttribute(ExecutionContext context) => context.TestSuite.FixtureType
        .GetMethods()
        .FirstOrDefault(m => m.IsDefined(typeof(AfterTestAttribute)))
        ?.GetCustomAttribute<AfterTestAttribute>();

    private static BeforeTestAttribute? BeforeTestAttribute(ExecutionContext context) => context.TestSuite.FixtureType
        .GetMethods()
        .FirstOrDefault(m => m.IsDefined(typeof(BeforeTestAttribute)))
        ?.GetCustomAttribute<BeforeTestAttribute>();

    private static string ReportOrphans(ExecutionContext context)
    {
        var beforeAttribute = BeforeTestAttribute(context);
        var afterAttributes = AfterTestAttribute(context);
        if (beforeAttribute != null && afterAttributes != null)
        {
            return $"""
                    {AssertFailures.FormatValue("WARNING:", AssertFailures.WARN_COLOR, false)}
                        Detected <{context.MemoryPool.OrphanCount}> orphan nodes during test setup stage!
                        Check [b]{beforeAttribute.Name + ":" + beforeAttribute.Line}[/b] and [b]{afterAttributes.Name + ":" + afterAttributes.Line}[/b] for unfreed instances!
                    """;
        }

        return $"""
                {AssertFailures.FormatValue("WARNING:", AssertFailures.WARN_COLOR, false)}
                    Detected <{context.MemoryPool.OrphanCount}> orphan nodes during test setup stage!
                    Check [b]{(beforeAttribute != null ? beforeAttribute.Name + ":" + beforeAttribute.Line : afterAttributes?.Name + ":" + afterAttributes?.Line)}[/b] for unfreed instances!
                """;
    }
}
