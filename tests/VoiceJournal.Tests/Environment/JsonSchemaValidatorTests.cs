using Corvus.Json;
using System.Text.Json;
using Corvus.Json.Validator;

namespace VoiceJournal.Tests.Environment;

/// <summary>
/// Phase A: verifies the JSON Schema validator honours Draft 2020-12
/// (detail spec 3: "Draft 2020-12 検証能力を確認して固定") and rejects
/// malformed LLM output. The schema mirrors scripts/Test-Environment.ps1.
/// </summary>
public sealed class JsonSchemaValidatorTests
{
    private const string StageASchema = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "https://voicejournal.invalid/schemas/stage-a.json",
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "tasks", "open_issues"],
          "properties": {
            "summary": { "type": "string", "minLength": 1 },
            "tasks": { "type": "array", "items": { "$ref": "#/$defs/task" } },
            "open_issues": { "type": "array", "items": { "type": "string" } }
          },
          "$defs": {
            "task": {
              "type": "object",
              "additionalProperties": false,
              "required": ["title", "assignee", "due_text"],
              "properties": {
                "title": { "type": "string", "minLength": 1 },
                "assignee": { "type": ["string", "null"] },
                "due_text": { "type": ["string", "null"] }
              }
            }
          }
        }
        """;

    // Actual Gemma 3 4B output from the Stage A run (2026-10-31).
    private const string GemmaOutput = """
        {
          "summary": "本日の定例会議にて、ログ収集方式に関する調査を来週の金曜日までに田中さんが担当することになりました。",
          "tasks": [ { "title": "ログ収集方式調査", "assignee": "田中さん", "due_text": "来週の金曜日" } ],
          "open_issues": [ "既存のパソコンの性能不足" ]
        }
        """;

    private static readonly JsonSchema Schema =
        JsonSchema.FromText(StageASchema, "https://voicejournal.invalid/schemas/stage-a.json");

    [Fact]
    public void GemmaOutput_IsValid()
    {
        var result = Validate(Schema, GemmaOutput, ValidationLevel.Detailed);
        Assert.True(result.IsValid, Describe(result));
    }

    [Fact]
    public void NullAssigneeAndDue_AreValid()
    {
        const string json = """{ "summary": "s", "tasks": [ { "title": "t", "assignee": null, "due_text": null } ], "open_issues": [] }""";
        Assert.True(Validate(Schema, json).IsValid);
    }

    [Theory]
    [InlineData("""{ "tasks": [], "open_issues": [] }""")] // missing required summary
    [InlineData("""{ "summary": "s", "tasks": [], "open_issues": [], "extra": 1 }""")] // additionalProperties
    [InlineData("""{ "summary": "s", "tasks": [ { "title": "t", "assignee": 3, "due_text": null } ], "open_issues": [] }""")] // wrong type via $ref
    [InlineData("""{ "summary": "s", "tasks": [ { "title": "t", "due_text": null } ], "open_issues": [] }""")] // missing required inside $defs
    [InlineData("""{ "summary": "", "tasks": [], "open_issues": [] }""")] // minLength
    [InlineData("""{ "summary": "s", "tasks": [], "open_issues": [ 1 ] }""")] // items type
    public void MalformedOutput_IsRejected(string json)
    {
        Assert.False(Validate(Schema, json).IsValid);
    }

    [Fact]
    public void Draft202012_PrefixItems_IsEnforced()
    {
        // prefixItems exists only in 2020-12; an older-draft validator would ignore it and accept.
        var schema = JsonSchema.FromText("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "array",
              "prefixItems": [ { "type": "string" }, { "type": "integer" } ],
              "items": false
            }
            """, "https://voicejournal.invalid/schemas/prefix.json");

        Assert.True(Validate(schema, """["a", 1]""").IsValid);
        Assert.False(Validate(schema, """[1, "a"]""").IsValid);
        Assert.False(Validate(schema, """["a", 1, 2]""").IsValid);
    }

    [Fact]
    public void Draft202012_UnevaluatedProperties_IsEnforced()
    {
        var schema = JsonSchema.FromText("""
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "allOf": [ { "properties": { "a": { "type": "string" } } } ],
              "unevaluatedProperties": false
            }
            """, "https://voicejournal.invalid/schemas/unevaluated.json");

        Assert.True(Validate(schema, """{ "a": "x" }""").IsValid);
        Assert.False(Validate(schema, """{ "a": "x", "b": 1 }""").IsValid);
    }

    private static ValidationContext Validate(JsonSchema schema, string json, ValidationLevel level = ValidationLevel.Flag)
    {
        using var doc = JsonDocument.Parse(json);
        return schema.Validate(doc.RootElement, level);
    }

    private static string Describe(ValidationContext result) =>
        string.Join(System.Environment.NewLine, result.Results.Select(r => $"{r.Location}: {r.Message}"));
}
