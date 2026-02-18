using System.Text.Json.Nodes;
using MemNet.MemoryService.Core;

namespace MemNet.MemoryService.UnitTests;

public class JsonPatchEngineTests
{
    [Fact]
    public void Apply_AddReplaceRemove_Works()
    {
        var source = new JsonObject
        {
            ["content"] = new JsonObject
            {
                ["preferences"] = new JsonArray("short")
            }
        };

        var result = JsonPatchEngine.Apply(
            source,
            new[]
            {
                new PatchOperation("add", "/content/profile", new JsonObject { ["name"] = "alex" }),
                new PatchOperation("replace", "/content/preferences/0", JsonValue.Create("concise")),
                new PatchOperation("remove", "/content/profile/name", null)
            });

        Assert.Equal("concise", result["content"]?["preferences"]?[0]?.GetValue<string>());
        Assert.Null(result["content"]?["profile"]?["name"]);
    }

    [Fact]
    public void Apply_UnsupportedOp_ThrowsApiException()
    {
        var ex = Assert.Throws<ApiException>(
            () => JsonPatchEngine.Apply(
                new JsonObject(),
                new[]
                {
                    new PatchOperation("move", "/a", JsonValue.Create("b"))
                }));

        Assert.Equal(422, ex.StatusCode);
        Assert.Equal("INVALID_PATCH_OP", ex.Code);
    }
}
