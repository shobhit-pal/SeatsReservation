# Project rules: seat reservation API (C# / ASP.NET Core 8, Dapper, Npgsql, Postgres)

Read `docs/design.md` before any task. It is the spec.

## Hard constraints
- Postgres is the only source of truth. Memory is a shortcut, never the authority.
- Seat decisions use conditional SQL (UPDATE ... WHERE reservation_id IS NULL), never read-then-write.
- Raw SQL with Dapper over Npgsql. No EF Core (it hides the atomic statements).
- JSON uses System.Text.Json with snake_case through the serializer policy. Never rename properties by hand.
- Money is long paise. Never float or decimal.
- Identity comes from the JWT only, never from the request body.
- No queue, no Redis. One app instance.
- Never return 5xx for domain outcomes. Declines are 4xx. Retry transient DB errors.
- No secrets in code or git. Config comes from environment variables.

## Naming
- Classes, interfaces, records, enums, methods, properties, constants: UpperCamelCase. Interfaces start with I.
- Locals and parameters: camelCase. Private fields: _camelCase.
- Async methods end with Async.
- DB tables and columns are snake_case.
- **One public class (or interface, or record, or enum) per file. The filename must match the type name exactly** (e.g. `AuthRequest` lives in `AuthRequest.cs`). Never bundle multiple public types in one file.

## Design
- SOLID: one responsibility per class. Depend on interfaces, wire them with DI.
- DRY: no duplicated SQL, validation or error shapes. One place for each.
- Layers: Controller (HTTP only), then Service (business rules), then Repository (SQL only).
- Domain outcomes use a Result type (Success or a Decline with a reason), not exceptions.
- Patterns allowed: Repository, Options, Decorator (cache over repository), Result. Do not add others without a reason.
- Use LINQ for validation, mapping and filtering in memory. Avoid it in tight hot-path loops.
- Keep it small. Prefer the simplest code that is correct. No speculative abstractions.

## Process
- One endpoint per commit, with a conventional message (feat:, fix:, test:, chore:).
- Every SQL statement has a one-line comment saying what it decides.
- Comment any non-obvious line briefly. The owner must understand all of it.
- Do only what the current prompt asks. Do not start the next commit.
