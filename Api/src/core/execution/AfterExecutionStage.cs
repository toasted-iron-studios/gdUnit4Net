// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Asserts;

using Reporting;

using static Api.ReportType;

internal class AfterExecutionStage : ExecutionStage<AfterAttribute>
{
    private readonly bool publishSuiteEvent;

    public AfterExecutionStage(TestSuite testSuite, bool publishSuiteEvent = true)
        : base("After", testSuite.FixtureType) => this.publishSuiteEvent = publishSuiteEvent;

    public override async Task Execute(ExecutionContext context)
    {
        context.MemoryPool.SetActive(StageName);
        await base
            .Execute(context)
            .ConfigureAwait(true);
        if (publishSuiteEvent)
            Utils.ClearTempDir();
        await context.MemoryPool
            .Gc()
            .ConfigureAwait(true);
        if (context.MemoryPool.OrphanCount > 0)
            context.ReportCollector.PushFront(new TestReport(Warning, 0, ReportOrphans(context)));
        if (publishSuiteEvent)
            context.FireAfterEvent();
    }

    private static AfterAttribute? AfterAttribute(ExecutionContext context) => context.TestSuite.FixtureType
        .GetMethods()
        .FirstOrDefault(m => m.IsDefined(typeof(AfterAttribute)))
        ?.GetCustomAttribute<AfterAttribute>();

    private static BeforeAttribute? BeforeAttribute(ExecutionContext context) => context.TestSuite.FixtureType
        .GetMethods()
        .FirstOrDefault(m => m.IsDefined(typeof(BeforeAttribute)))
        ?.GetCustomAttribute<BeforeAttribute>();

    private static string ReportOrphans(ExecutionContext context)
    {
        var beforeAttribute = BeforeAttribute(context);
        var afterAttributes = AfterAttribute(context);

        if (beforeAttribute != null && afterAttributes != null)
        {
            return $"""
                    {AssertFailures.FormatValue("WARNING:", AssertFailures.WARN_COLOR, false)}
                        Detected <{context.MemoryPool.OrphanCount}> orphan nodes during test suite setup stage!
                        Check [b]{beforeAttribute.Name + ":" + beforeAttribute.Line}[/b] and [b]{afterAttributes.Name + ":" + afterAttributes.Line}[/b] for unfreed instances!
                    """;
        }

        return $"""
                {AssertFailures.FormatValue("WARNING:", AssertFailures.WARN_COLOR, false)}
                    Detected <{context.MemoryPool.OrphanCount}> orphan nodes during test suite setup stage!
                    Check [b]{(beforeAttribute != null ? beforeAttribute.Name + ":" + beforeAttribute.Line : afterAttributes?.Name + ":" + afterAttributes?.Line)}[/b] for unfreed instances!
                """;
    }
}
