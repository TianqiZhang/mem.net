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
        AddTest(nameof(PatchDocumentReturns422OnInvalidPatchPathAsync), PatchDocumentReturns422OnInvalidPatchPathAsync);
        AddTest(nameof(PatchFileTextEditsApplyDeterministicallyAsync), PatchFileTextEditsApplyDeterministicallyAsync);
        AddTest(nameof(PatchFileTextEditsRejectAmbiguousMatchAsync), PatchFileTextEditsRejectAmbiguousMatchAsync);
        AddTest(nameof(PatchFileTextEditsRejectMissingMatchAsync), PatchFileTextEditsRejectMissingMatchAsync);
        AddTest(nameof(AssembleContextIncludesRequestedFilesAndRespectsBudgetsAsync), AssembleContextIncludesRequestedFilesAndRespectsBudgetsAsync);
        AddTest(nameof(AssembleContextRejectsEmptyRequestAsync), AssembleContextRejectsEmptyRequestAsync);
        AddTest(nameof(EventSearchReturnsRelevantResultsAsync), EventSearchReturnsRelevantResultsAsync);

        AddTest(nameof(HttpFilePatchFlowWorksEndToEndAsync), HttpFilePatchFlowWorksEndToEndAsync);
        AddTest(nameof(HttpFilePatchAddOperationWorksEndToEndAsync), HttpFilePatchAddOperationWorksEndToEndAsync);
        AddTest(nameof(HttpContextAssembleRejectsEmptyFilesAsync), HttpContextAssembleRejectsEmptyFilesAsync);
        AddTest(nameof(HttpContextAssembleWithExplicitFilesWorksAsync), HttpContextAssembleWithExplicitFilesWorksAsync);
        AddTest(nameof(HttpEventsWriteAndSearchFlowWorksAsync), HttpEventsWriteAndSearchFlowWorksAsync);
        AddTest(nameof(SdkClientPatchAndGetFlowWorksAsync), SdkClientPatchAndGetFlowWorksAsync);
        AddTest(nameof(SdkClientMapsApiErrorsAsync), SdkClientMapsApiErrorsAsync);
        AddTest(nameof(SdkClientAssembleAndSearchFlowWorksAsync), SdkClientAssembleAndSearchFlowWorksAsync);
        AddTest(nameof(AgentMemoryPrepareTurnFlowWorksAsync), AgentMemoryPrepareTurnFlowWorksAsync);
        AddTest(nameof(SdkUpdateWithRetryResolvesEtagConflictsAsync), SdkUpdateWithRetryResolvesEtagConflictsAsync);
        AddTest(nameof(AgentMemoryFileToolFlowWorksAsync), AgentMemoryFileToolFlowWorksAsync);
        AddTest(nameof(SdkUpdateWithRetryResolvesEtagConflictsForTextPatchFlowAsync), SdkUpdateWithRetryResolvesEtagConflictsForTextPatchFlowAsync);
        AddTest(nameof(SdkUpdateWithRetryResolvesEtagConflictsForWriteFlowAsync), SdkUpdateWithRetryResolvesEtagConflictsForWriteFlowAsync);
        if (ShouldRunOptionalSdkTests())
        {
            AddTest(nameof(AgentMemoryPatchSlotRulesAreEnforcedClientSideAsync), AgentMemoryPatchSlotRulesAreEnforcedClientSideAsync);
        }
        AddTest(nameof(SearchIndexSchemaLoadsAndBuildsSearchIndexAsync), SearchIndexSchemaLoadsAndBuildsSearchIndexAsync);
        AddTest(nameof(BootstrapCliParsesArgumentsAsync), BootstrapCliParsesArgumentsAsync);

#if !MEMNET_ENABLE_AZURE_SDK
        AddTest(nameof(AzureProviderDisabledReturns501Async), AzureProviderDisabledReturns501Async);
#endif

        AddTest(nameof(RetentionSweepRemovesExpiredEventsAsync), RetentionSweepRemovesExpiredEventsAsync);
        AddTest(nameof(RetentionSweepRequestShapeWorksAsync), RetentionSweepRequestShapeWorksAsync);
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

    private static bool ShouldRunOptionalSdkTests()
    {
        var raw = Environment.GetEnvironmentVariable("MEMNET_RUN_OPTIONAL_SDK_TESTS");
        return string.Equals(raw, "1", StringComparison.Ordinal)
               || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TestCase(string Name, Func<Task> Execute);
}
