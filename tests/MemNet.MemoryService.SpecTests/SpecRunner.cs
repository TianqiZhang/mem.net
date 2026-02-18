internal sealed partial class SpecRunner
{
    private readonly List<TestCase> _tests = [];

    public SpecRunner()
    {
        RegisterTests();
    }

    public async Task RunAsync()
    {
        var passed = 0;
        foreach (var test in _tests)
        {
            try
            {
                await test.Execute();
                passed++;
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }

        Console.WriteLine($"{passed}/{_tests.Count} tests passed.");
        if (passed != _tests.Count)
        {
            Environment.ExitCode = 1;
        }
    }

    private void RegisterTests()
    {
        // Smoke-only scope: framework test projects hold primary behavioral coverage.
        AddTest(nameof(HttpFilePatchFlowWorksEndToEndAsync), HttpFilePatchFlowWorksEndToEndAsync);
        AddTest(nameof(HttpContextAssembleWithExplicitFilesWorksAsync), HttpContextAssembleWithExplicitFilesWorksAsync);
        AddTest(nameof(HttpEventsWriteAndSearchFlowWorksAsync), HttpEventsWriteAndSearchFlowWorksAsync);
        AddTest(nameof(AgentMemoryFileToolFlowWorksAsync), AgentMemoryFileToolFlowWorksAsync);
        AddTest(nameof(ForgetUserRemovesDocumentsAndEventsAsync), ForgetUserRemovesDocumentsAndEventsAsync);
        AddTest(nameof(RetentionSweepRemovesExpiredEventsAsync), RetentionSweepRemovesExpiredEventsAsync);
        AddTest(nameof(BootstrapCliParsesArgumentsAsync), BootstrapCliParsesArgumentsAsync);
        AddTest(nameof(SearchIndexSchemaLoadsAndBuildsSearchIndexAsync), SearchIndexSchemaLoadsAndBuildsSearchIndexAsync);
        if (ShouldRunOptionalSdkTests())
        {
            AddTest(nameof(AgentMemoryPatchSlotRulesAreEnforcedClientSideAsync), AgentMemoryPatchSlotRulesAreEnforcedClientSideAsync);
        }

#if !MEMNET_ENABLE_AZURE_SDK
        AddTest(nameof(AzureProviderDisabledReturns501Async), AzureProviderDisabledReturns501Async);
#endif

#if MEMNET_ENABLE_AZURE_SDK
        AddTest(nameof(AzureProviderOptionsMappingAndValidationAsync), AzureProviderOptionsMappingAndValidationAsync);
        AddTest(nameof(AzureSearchFilterRequestShapingAsync), AzureSearchFilterRequestShapingAsync);
#endif

        AddTest(nameof(AzureLiveSmokeTestIfConfiguredAsync), AzureLiveSmokeTestIfConfiguredAsync);
    }

    private void AddTest(string name, Func<Task> execute)
    {
        _tests.Add(new TestCase(name, execute));
    }

    private static bool ShouldRunOptionalSdkTests()
    {
        var raw = Environment.GetEnvironmentVariable("MEMNET_RUN_OPTIONAL_SDK_TESTS");
        return string.Equals(raw, "1", StringComparison.Ordinal)
               || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TestCase(string Name, Func<Task> Execute);
}
