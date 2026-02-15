# AGENTS.md

## Mission
Build and maintain `mem.net` as a robust, configurable memory service that is easy to evolve and safe to operate.

## Working Principles
- Keep service logic generic; memory categories belong in profile config.
- Favor deterministic behavior over implicit magic.
- Keep document writes auditable and conflict-safe.
- Enforce strict validation and clear error contracts.
- Keep documents bounded; move history/detail to events.

## Engineering Rules
- Use .NET 8 and keep external dependencies minimal.
- Prefer pure domain services over framework-coupled code.
- Keep APIs small and explicit; avoid hidden side effects.
- Every mutating endpoint must enforce ETag + idempotency semantics.
- Every mutating endpoint must emit audit data.

## Testing Rules
- Add or update executable spec tests for every feature and regression.
- Include at least one negative test for validation/concurrency error paths.
- Keep tests deterministic and environment-independent.

## Documentation Rules
- Update `TASK_BOARD.md` when status changes.
- Keep `README.md` run instructions accurate.
- Keep implementation aligned with `MEMORY_SERVICE_SPEC.md`; document intentional deviations.

## Delivery Standard
A task is done only when:
1. Feature behavior is implemented.
2. Relevant tests pass.
3. Docs are updated.
4. Known risks are called out.
