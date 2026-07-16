// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System.Threading.Tasks;

internal class BeforeExecutionStage : ExecutionStage<BeforeAttribute>
{
    private readonly bool publishSuiteEvent;

    public BeforeExecutionStage(TestSuite testSuite, bool publishSuiteEvent = true)
        : base("Before", testSuite.FixtureType) => this.publishSuiteEvent = publishSuiteEvent;

    public override async Task Execute(ExecutionContext context)
    {
        context.MemoryPool.SetActive(StageName, true);
        await base
            .Execute(context)
            .ConfigureAwait(true);
        if (publishSuiteEvent)
        {
            context.FireBeforeEvent();
            context.ReportCollector.Clear();
        }

        context.MemoryPool.StopMonitoring();
    }
}
