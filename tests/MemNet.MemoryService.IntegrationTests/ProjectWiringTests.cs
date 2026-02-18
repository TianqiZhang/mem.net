namespace MemNet.MemoryService.IntegrationTests;

public class ProjectWiringTests
{
    [Fact]
    public void ProgramType_IsAccessible_ToIntegrationTests()
    {
        var entryPoint = typeof(Program);
        Assert.Equal("Program", entryPoint.Name);
    }
}
