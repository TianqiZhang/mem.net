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
        AddTest(nameof(FilesystemStoreContractsAreConsistentAsync), FilesystemStoreContractsAreConsistentAsync);

        AddTest(nameof(PatchDocumentHappyPathAsync), PatchDocumentHappyPathAsync);
        AddTest(nameof(PatchDocumentReturns412OnEtagMismatchAsync), PatchDocumentReturns412OnEtagMismatchAsync);
        AddTest(nameof(PatchDocumentReturns422OnPathPolicyViolationAsync), PatchDocumentReturns422OnPathPolicyViolationAsync);
        AddTest(nameof(AssembleContextIncludesDefaultDocsAndRespectsBudgetsAsync), AssembleContextIncludesDefaultDocsAndRespectsBudgetsAsync);
        AddTest(nameof(EventSearchReturnsRelevantResultsAsync), EventSearchReturnsRelevantResultsAsync);

        AddTest(nameof(HttpDocumentPatchFlowWorksEndToEndAsync), HttpDocumentPatchFlowWorksEndToEndAsync);
        AddTest(nameof(HttpContextAssembleReturnsDefaultDocsAsync), HttpContextAssembleReturnsDefaultDocsAsync);
        AddTest(nameof(HttpEventsWriteAndSearchFlowWorksAsync), HttpEventsWriteAndSearchFlowWorksAsync);
        AddTest(nameof(SearchIndexSchemaLoadsAndBuildsSearchIndexAsync), SearchIndexSchemaLoadsAndBuildsSearchIndexAsync);
        AddTest(nameof(BootstrapCliParsesArgumentsAsync), BootstrapCliParsesArgumentsAsync);

#if !MEMNET_ENABLE_AZURE_SDK
        AddTest(nameof(AzureProviderDisabledReturns501Async), AzureProviderDisabledReturns501Async);
#endif

        AddTest(nameof(RetentionSweepRemovesExpiredEventsAsync), RetentionSweepRemovesExpiredEventsAsync);
        AddTest(nameof(ForgetUserRemovesDocumentsAndEventsAsync), ForgetUserRemovesDocumentsAndEventsAsync);

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

    private sealed record TestCase(string Name, Func<Task> Execute);
}
