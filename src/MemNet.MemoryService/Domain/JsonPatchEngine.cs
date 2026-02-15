using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace MemNet.MemoryService.Core;

public static class JsonPatchEngine
{
    public static JsonObject Apply(JsonObject source, IReadOnlyList<PatchOperation> operations)
    {
        var clone = (JsonObject?)source.DeepClone() ?? new JsonObject();

        foreach (var operation in operations)
        {
            var op = operation.Op.Trim().ToLowerInvariant();
            switch (op)
            {
                case "add":
                    Add(clone, operation.Path, operation.Value);
                    break;
                case "replace":
                    Replace(clone, operation.Path, operation.Value);
                    break;
                case "remove":
                    Remove(clone, operation.Path);
                    break;
                default:
                    throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_OP", $"Unsupported patch op '{operation.Op}'.");
            }
        }

        return clone;
    }

    private static void Add(JsonObject root, string path, JsonNode? value)
    {
        var (container, token) = ResolveParent(root, path, createMissing: true);
        switch (container)
        {
            case JsonObject obj:
                obj[token] = value?.DeepClone();
                break;
            case JsonArray array:
                if (token == "-")
                {
                    array.Add(value?.DeepClone());
                    return;
                }

                if (!int.TryParse(token, out var addIndex) || addIndex < 0 || addIndex > array.Count)
                {
                    throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Invalid array index '{token}' for path '{path}'.");
                }

                array.Insert(addIndex, value?.DeepClone());
                break;
            default:
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Cannot add at path '{path}'.");
        }
    }

    private static void Replace(JsonObject root, string path, JsonNode? value)
    {
        var (container, token) = ResolveParent(root, path, createMissing: false);
        switch (container)
        {
            case JsonObject obj:
                Guard.True(obj.ContainsKey(token), "INVALID_PATCH_PATH", $"Path '{path}' does not exist.", StatusCodes.Status422UnprocessableEntity);
                obj[token] = value?.DeepClone();
                break;
            case JsonArray array:
                if (!int.TryParse(token, out var replaceIndex) || replaceIndex < 0 || replaceIndex >= array.Count)
                {
                    throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Invalid array index '{token}' for path '{path}'.");
                }

                array[replaceIndex] = value?.DeepClone();
                break;
            default:
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Cannot replace at path '{path}'.");
        }
    }

    private static void Remove(JsonObject root, string path)
    {
        var (container, token) = ResolveParent(root, path, createMissing: false);
        switch (container)
        {
            case JsonObject obj:
                Guard.True(obj.Remove(token), "INVALID_PATCH_PATH", $"Path '{path}' does not exist.", StatusCodes.Status422UnprocessableEntity);
                break;
            case JsonArray array:
                if (!int.TryParse(token, out var removeIndex) || removeIndex < 0 || removeIndex >= array.Count)
                {
                    throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Invalid array index '{token}' for path '{path}'.");
                }

                array.RemoveAt(removeIndex);
                break;
            default:
                throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Cannot remove at path '{path}'.");
        }
    }

    private static (JsonNode Container, string Token) ResolveParent(JsonObject root, string path, bool createMissing)
    {
        var tokens = Tokenize(path);
        Guard.True(tokens.Count > 0, "INVALID_PATCH_PATH", "Patch path must not be empty.", StatusCodes.Status422UnprocessableEntity);

        JsonNode current = root;
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            var token = tokens[i];
            current = current switch
            {
                JsonObject obj => ResolveObjectChild(obj, token, createMissing),
                JsonArray array => ResolveArrayChild(array, token, createMissing),
                _ => throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Path segment '{token}' is not traversable.")
            };
        }

        return (current, tokens[^1]);
    }

    private static JsonNode ResolveObjectChild(JsonObject obj, string token, bool createMissing)
    {
        if (obj[token] is JsonNode node)
        {
            return node;
        }

        if (!createMissing)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Path segment '{token}' does not exist.");
        }

        var created = new JsonObject();
        obj[token] = created;
        return created;
    }

    private static JsonNode ResolveArrayChild(JsonArray array, string token, bool createMissing)
    {
        if (!int.TryParse(token, out var index) || index < 0 || index >= array.Count)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Invalid array index '{token}'.");
        }

        if (array[index] is JsonNode node)
        {
            return node;
        }

        if (!createMissing)
        {
            throw new ApiException(StatusCodes.Status422UnprocessableEntity, "INVALID_PATCH_PATH", $"Null node at array index '{token}'.");
        }

        var created = new JsonObject();
        array[index] = created;
        return created;
    }

    private static List<string> Tokenize(string path)
    {
        Guard.True(path.StartsWith('/'), "INVALID_PATCH_PATH", "Patch path must start with '/'.", StatusCodes.Status422UnprocessableEntity);
        return path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(DecodeToken)
            .ToList();
    }

    private static string DecodeToken(string token)
    {
        return token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }
}
