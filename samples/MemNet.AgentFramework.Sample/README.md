# MemNet.AgentFramework.Sample

Minimal Microsoft Agent Framework console sample using `mem.net` as long-term memory.

## What it shows

- Registers the official file-like memory tools:
  - `memory_recall(query, topK)`
  - `memory_load_file(path)`
  - `memory_patch_file(path, old_text, new_text, occurrence)`
  - `memory_write_file(path, content)`
- Streams assistant output token-by-token in the console.
- Prints memory tool calls/results in the console (for transparency during runs).
- Primes `user/profile.md` and `user/long_term_memory.md` once per session.
- Re-injects the memory snapshot only after `memory_write_file` / `memory_patch_file` updates one of those preloaded files.
- Uses concise default response behavior (short replies unless user asks for detail).
- Uses `MemNet.AgentMemory` directly for tool behavior.
- Writes one event digest per turn so `memory_recall` has data to search.
- Returns file content (not ETag text) from `memory_patch_file` and `memory_write_file`.

## Prerequisites

- `mem.net` service running (default: `http://localhost:5071`)
- One model provider:
  - OpenAI (`OPENAI_API_KEY`; optional `OPENAI_MODEL`, default `gpt-5.1`)
  - Azure OpenAI (`AZURE_OPENAI_ENDPOINT`; optional `AZURE_OPENAI_DEPLOYMENT_NAME`, default `gpt-5.1`; auth via `AZURE_OPENAI_API_KEY` or Azure Identity)

## Run with OpenAI

```bash
export OPENAI_API_KEY="<your_key>"
export OPENAI_MODEL="gpt-5.1"
export MEMNET_BASE_URL="http://localhost:5071"
export MEMNET_TENANT_ID="tenant-demo"
export MEMNET_USER_ID="user-demo"

dotnet run --project samples/MemNet.AgentFramework.Sample
```

## Run with Azure OpenAI

```bash
export AZURE_OPENAI_ENDPOINT="https://<resource>.openai.azure.com"
export AZURE_OPENAI_DEPLOYMENT_NAME="gpt-5.1"
# optional if not using Azure Identity:
# export AZURE_OPENAI_API_KEY="<your_key>"

export MEMNET_BASE_URL="http://localhost:5071"
export MEMNET_TENANT_ID="tenant-demo"
export MEMNET_USER_ID="user-demo"

dotnet run --project samples/MemNet.AgentFramework.Sample
```
