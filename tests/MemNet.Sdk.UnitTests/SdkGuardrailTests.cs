using MemNet.AgentMemory;
using MemNet.Client;

namespace MemNet.Sdk.UnitTests;

public class SdkGuardrailTests
{
    [Fact]
    public void FileUpdateFactories_RejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => FileUpdate.FromPatch(null!));
        Assert.Throws<ArgumentNullException>(() => FileUpdate.FromWrite(null!));
    }

    [Fact]
    public async Task UpdateWithRetryAsync_ValidatesArguments()
    {
        MemNetClient? client = null;
        var scope = new MemNetScope("tenant", "user");
        var file = new FileRef("user/profile.json");

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client!.UpdateWithRetryAsync(scope, file, _ => FileUpdate.FromPatch(new PatchDocumentRequest([], "test"))));
    }

    [Fact]
    public void AgentMemoryPolicy_CanLoadFromFile()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"memnet-policy-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempFile, """
            {
              "policy_id": "test-policy",
              "slots": [
                {
                  "slot_id": "profile",
                  "path": "user/profile.md",
                  "path_template": null,
                  "load_by_default": true
                }
              ]
            }
            """);

            var policy = AgentMemoryPolicy.LoadFromFile(tempFile);
            Assert.Equal("test-policy", policy.PolicyId);
            Assert.Single(policy.Slots);
            Assert.Equal("profile", policy.Slots[0].SlotId);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
