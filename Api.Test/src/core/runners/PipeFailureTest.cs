// Copyright (c) 2026 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Tests.Core.Runners;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GdUnit4.Api;
using GdUnit4.Core.Commands;
using GdUnit4.Core.Execution;
using GdUnit4.Core.Runners;
using Moq;
using Newtonsoft.Json;
using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

[TestSuite]
public class PipeFailureTest
{
    [TestCase(0)]
    [TestCase(2)]
    [TestCase(4)]
    [TestCase(6)]
    public async Task ClosedPipeRejectsMissingOrTruncatedFrame(int bytesWritten)
    {
        await using var pair = await PipePair.Connect();
        await using var reader = new TestProxy(pair.Server);
        var frame = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 4);
        await pair.Client.WriteAsync(frame.AsMemory(0, bytesWritten));
        await pair.Client.DisposeAsync();

        await Assert.ThrowsExceptionAsync<EndOfStreamException>(async () =>
            await reader.Read(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestCase]
    public async Task FragmentedFrameIsReassembled()
    {
        await using var pair = await PipePair.Connect();
        await using var reader = new TestProxy(pair.Server);
        var json = JsonConvert.SerializeObject(new Response { StatusCode = HttpStatusCode.OK, Payload = "complete" },
            new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All });
        var data = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        var read = reader.Read(CancellationToken.None);
        foreach (byte value in header.Concat(data))
            await pair.Client.WriteAsync(new[] { value });

        var response = (Response)(await read.WaitAsync(TimeSpan.FromSeconds(2)))!;
        Assert.AreEqual("complete", response.Payload);
    }

    [TestCase]
    public async Task CancellationInterruptsPartialRead()
    {
        await using var pair = await PipePair.Connect();
        await using var reader = new TestProxy(pair.Server);
        using var cancellation = new CancellationTokenSource();
        await pair.Client.WriteAsync(new byte[] { 1, 0 });
        var read = reader.Read(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            await read.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestCase(0)]
    [TestCase(-1)]
    public async Task EmptyOrNegativeFrameIsRejected(int length)
    {
        await using var pair = await PipePair.Connect();
        await using var reader = new TestProxy(pair.Server);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await pair.Client.WriteAsync(header);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(async () =>
            await reader.Read(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task EngineDisconnectFailsCommandEvenAfterSuiteEvent(bool activeTest)
    {
        string name = Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var peer = new TestProxy(server);
        await using var executor = new GodotRuntimeExecutor(Mock.Of<ITestEngineLogger>(), name);
        var connection = server.WaitForConnectionAsync();
        await executor.StartAsync();
        await connection.WaitAsync(TimeSpan.FromSeconds(2));
        var listener = new RecordingListener();
        var execution = executor.ExecuteCommand(new TerminateGodotInstanceCommand(), listener, CancellationToken.None);
        await peer.ReadRequest().WaitAsync(TimeSpan.FromSeconds(2));
        var id = Guid.NewGuid();
        if (activeTest)
            await peer.Send(TestEvent.BeforeTest(id, "test.cs", "Suite", "InFlight"));
        await peer.Send(TestEvent.Before("test.cs", "Suite", 1, new Dictionary<TestEvent.StatisticKey, object>(), []));
        await server.DisposeAsync();

        var response = await execution.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(HttpStatusCode.Gone, response.StatusCode);
        var interrupted = listener.Events.Where(e => e.Type == EventType.TestAfter).ToList();
        Assert.AreEqual(activeTest ? 1 : 0, interrupted.Count);
        if (activeTest)
        {
            Assert.AreEqual(id, interrupted[0].Id);
            Assert.IsTrue(interrupted[0].IsError);
        }
    }

    [TestCase(HttpStatusCode.Gone)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task RunnerPropagatesCommandFailure(HttpStatusCode status)
    {
        var executor = new Mock<ICommandExecutor>();
        executor.Setup(e => e.StartAsync()).Returns(Task.CompletedTask);
        executor.Setup(e => e.ExecuteCommand(It.IsAny<ExecuteTestSuiteCommand>(), It.IsAny<ITestEventListener>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Response { StatusCode = status });
        await using var runner = new TestRunner(executor.Object);
        var suite = new TestSuiteNode
        {
            Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), ManagedType = "Suite",
            Tests = [], AssemblyPath = "tests.dll", SourceFile = "test.cs"
        };
        Assert.ThrowsException<InvalidOperationException>(() => runner.RunAndWait([suite], new RecordingListener(), CancellationToken.None));
        executor.Verify(e => e.StopAsync(), Times.Never);
    }

    private sealed class TestRunner(ICommandExecutor executor)
        : BaseTestRunner(executor, Mock.Of<ITestEngineLogger>(), new TestEngineSettings());

    private sealed class TestProxy(NamedPipeServerStream pipe)
        : InOutPipeProxy<NamedPipeServerStream>(pipe, Mock.Of<ITestEngineLogger>())
    {
        public Task<object?> Read(CancellationToken token) => ReadInData(token);
        public Task<BaseCommand> ReadRequest() => ReadCommand<BaseCommand>(CancellationToken.None);
        public Task Send<T>(T value) => WriteAsync(value);
    }

    private sealed class RecordingListener : ITestEventListener
    {
        public bool IsFailed { get; set; }
        public int CompletedTests { get; set; }
        public List<ITestEvent> Events { get; } = [];
        public void PublishEvent(ITestEvent testEvent) => Events.Add(testEvent);
    }

    private sealed class PipePair : IAsyncDisposable
    {
        private PipePair(string name)
        {
            Server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            Client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        }
        public NamedPipeServerStream Server { get; }
        public NamedPipeClientStream Client { get; }
        public static async Task<PipePair> Connect()
        {
            var pair = new PipePair(Guid.NewGuid().ToString("N"));
            var connection = pair.Server.WaitForConnectionAsync();
            await pair.Client.ConnectAsync(2000);
            await connection.WaitAsync(TimeSpan.FromSeconds(2));
            return pair;
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
