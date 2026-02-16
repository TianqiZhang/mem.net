using MemNet.Bootstrap;

internal sealed partial class SpecRunner
{
    private static Task SearchIndexSchemaLoadsAndBuildsSearchIndexAsync()
    {
        var schemaPath = Path.Combine(Directory.GetCurrentDirectory(), "infra", "search", "events-index.schema.json");
        Assert.True(File.Exists(schemaPath), $"Expected schema file at {schemaPath}");

        var schema = SearchIndexSchemaDocument.Load(schemaPath);
        var index = schema.ToSearchIndex("events-index");

        Assert.Equal("events-index", index.Name);
        Assert.True(index.Fields.Count >= 10, "Expected event index schema to define multiple fields.");
        Assert.True(index.Fields.Any(f => string.Equals(f.Name, "id", StringComparison.Ordinal) && f.IsKey == true), "Expected 'id' key field.");
        Assert.True(index.Fields.Any(f => string.Equals(f.Name, "tenant_id", StringComparison.Ordinal) && f.IsFilterable == true), "Expected filterable tenant_id field.");
        Assert.True(index.Fields.Any(f => string.Equals(f.Name, "digest", StringComparison.Ordinal) && f.IsSearchable == true), "Expected searchable digest field.");

        var validationErrors = schema.ValidateExistingIndex(index);
        Assert.Equal(0, validationErrors.Count);

        return Task.CompletedTask;
    }

    private static Task BootstrapCliParsesArgumentsAsync()
    {
        var parsed = CliOptions.TryParse(["azure", "--apply", "--schema", "infra/search/events-index.schema.json"], out var options, out var error);
        Assert.True(parsed, "Expected valid CLI parse result.");
        Assert.True(string.IsNullOrWhiteSpace(error), "Expected no CLI parse error.");
        Assert.Equal("azure", options.Provider);
        Assert.Equal(BootstrapMode.Apply, options.Mode);
        Assert.Equal("infra/search/events-index.schema.json", options.SearchSchemaPath);

        var failed = CliOptions.TryParse(["--check", "--apply"], out _, out var invalidError);
        Assert.True(!failed, "Expected mutually exclusive flags to fail parsing.");
        Assert.True(!string.IsNullOrWhiteSpace(invalidError), "Expected parse error for invalid CLI options.");

        return Task.CompletedTask;
    }
}
