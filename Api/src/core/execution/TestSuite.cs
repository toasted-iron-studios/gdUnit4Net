// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System.Reflection;

using Api;

internal sealed class TestSuite : IDisposable
{
    private readonly Lazy<object> instance;
    private readonly Lazy<IEnumerable<TestCase>> testCases;

    public TestSuite(TestSuiteNode suite)
        : this(
            FindTypeOnAssembly(suite.AssemblyPath, suite.ManagedType),
            suite.Tests,
            suite.SourceFile)
    {
    }

    internal TestSuite(Type type, List<TestCaseNode> tests, string sourceFile)
    {
        FixtureType = type;
        instance = new Lazy<object>(() => CreateInstance(type));
        Name = type.Name;
        ResourcePath = sourceFile;

        // we do lazy loading to only load test case one times
        testCases = new Lazy<IEnumerable<TestCase>>(() => LoadTestCases(type, tests));
    }

    private TestSuite(TestSuite source, TestCase testCase)
    {
        FixtureType = source.FixtureType;
        instance = new Lazy<object>(() => CreateInstance(FixtureType));
        Name = source.Name;
        ResourcePath = source.ResourcePath;
        FilterDisabled = source.FilterDisabled;
        testCases = new Lazy<IEnumerable<TestCase>>(() => [testCase]);
    }

    public int TestCaseCount => TestCases.Count();

    public IEnumerable<TestCase> TestCases => testCases.Value;

    public string ResourcePath { get; set; }

    public string Name { get; set; }

    public string? FullName => FixtureType.FullName;

    public Type FixtureType { get; }

    public object Instance => instance.Value;

    public bool FilterDisabled { get; set; }

    public TestSuite CreateFixture(TestCase testCase) => new(this, testCase);

    public void Dispose()
    {
        if (instance.IsValueCreated && Instance is IDisposable disposable)
            disposable.Dispose();
    }

    private static object CreateInstance(Type type) =>
        Activator.CreateInstance(type)
        ?? throw new InvalidOperationException($"Cannot create an instance of '{type.FullName}' because it does not have a public parameterless constructor.");

    private static List<TestCase> LoadTestCases(Type type, List<TestCaseNode> includedTests)
        =>
        [
            .. type.GetMethods()
                .Where(m => m.IsDefined(typeof(TestCaseAttribute)))
                .Join(
                    includedTests,
                    m => m.Name,
                    test => test.ManagedMethod,
                    (mi, test) => new TestCase(test.Id, mi, test.LineNumber, test.AttributeIndex))
        ];

    private static Type FindTypeOnAssembly(string assemblyPath, string clazz)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            // if (assembly.Location != assemblyName)
            //    continue;
            var type = assembly.GetType(clazz);
            if (type != null)
                return type;
        }

        try
        {
            var assembly = Assembly.Load(AssemblyName.GetAssemblyName(assemblyPath));
            return assembly.GetType(clazz)!;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to resolve type '{clazz}': {ex.Message}");
            throw new InvalidOperationException($"Could not find type {clazz} on assembly {assemblyPath}");
        }
    }
}
