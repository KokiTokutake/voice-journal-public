namespace VoiceJournal.Core.Analysis;

/// <summary>The analysis schema of detail spec 16, copied verbatim (Draft 2020-12). Validated by Infrastructure.</summary>
public static class AnalysisSchema
{
    public const string Id = "urn:voicejournal:analysis:1.0";

    public const string Json = """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "$id": "urn:voicejournal:analysis:1.0",
          "type": "object",
          "additionalProperties": false,
          "required": ["schema_version", "session_id", "transcript_revision", "record_types", "title", "summary", "sections", "candidates", "warnings"],
          "properties": {
            "schema_version": {"const": "1.0"},
            "session_id": {"type": "string", "format": "uuid"},
            "transcript_revision": {"type": "integer", "minimum": 1},
            "record_types": {"type": "array", "minItems": 1, "maxItems": 4, "uniqueItems": true, "items": {"enum": ["meeting", "idea", "learning", "work", "reflection", "consultation", "mixed", "other"]}},
            "title": {"type": "string", "minLength": 1, "maxLength": 200},
            "summary": {"type": "array", "minItems": 1, "maxItems": 7, "items": {"$ref": "#/$defs/statement"}},
            "sections": {"type": "array", "maxItems": 30, "items": {"$ref": "#/$defs/section"}},
            "candidates": {"type": "array", "maxItems": 200, "items": {"$ref": "#/$defs/candidate"}},
            "warnings": {"type": "array", "maxItems": 100, "items": {"type": "string", "minLength": 1, "maxLength": 1000}}
          },
          "$defs": {
            "evidence": {
              "type": "object", "additionalProperties": false,
              "required": ["segment_id", "quote"],
              "properties": {
                "segment_id": {"type": "string", "format": "uuid"},
                "quote": {"type": "string", "minLength": 1, "maxLength": 2000}
              }
            },
            "statement": {
              "type": "object", "additionalProperties": false,
              "required": ["text", "certainty", "evidence"],
              "properties": {
                "text": {"type": "string", "minLength": 1, "maxLength": 4000},
                "certainty": {"enum": ["stated", "tentative", "unclear"]},
                "evidence": {"type": "array", "minItems": 1, "maxItems": 30, "items": {"$ref": "#/$defs/evidence"}}
              }
            },
            "section": {
              "type": "object", "additionalProperties": false,
              "required": ["section_kind", "heading", "statements"],
              "properties": {
                "section_kind": {"enum": ["context", "topic", "decision", "open_question", "idea", "explanation", "result", "reflection", "next_step", "custom"]},
                "heading": {"type": "string", "minLength": 1, "maxLength": 100},
                "statements": {"type": "array", "minItems": 1, "maxItems": 100, "items": {"$ref": "#/$defs/statement"}}
              }
            },
            "fields": {
              "type": "object", "additionalProperties": false,
              "required": ["title", "description", "status", "priority", "assignee", "due_date"],
              "properties": {
                "title": {"type": "string", "minLength": 1, "maxLength": 200},
                "description": {"type": "string", "maxLength": 8000},
                "status": {"type": ["string", "null"], "enum": ["open", "todo", "in_progress", "blocked", "resolved", "closed", "done", "cancelled", null]},
                "priority": {"type": ["string", "null"], "enum": ["low", "medium", "high", "critical", null]},
                "assignee": {"type": ["string", "null"], "minLength": 1, "maxLength": 200},
                "due_date": {"type": ["string", "null"], "format": "date"}
              }
            },
            "candidate": {
              "type": "object", "additionalProperties": false,
              "required": ["candidate_id", "kind", "action", "suggested_target_id", "parent_issue_candidate_id", "parent_issue_item_id", "fields", "changed_fields", "certainty", "due_expression", "evidence", "rationale"],
              "properties": {
                "candidate_id": {"type": "string", "pattern": "^C[0-9]{4}$"},
                "kind": {"enum": ["issue", "task"]},
                "action": {"enum": ["create", "update", "mention"]},
                "suggested_target_id": {"type": ["string", "null"], "format": "uuid"},
                "parent_issue_candidate_id": {"type": ["string", "null"], "pattern": "^C[0-9]{4}$"},
                "parent_issue_item_id": {"type": ["string", "null"], "format": "uuid"},
                "fields": {"$ref": "#/$defs/fields"},
                "changed_fields": {"type": "array", "uniqueItems": true, "maxItems": 6, "items": {"enum": ["title", "description", "status", "priority", "assignee", "due_date"]}},
                "certainty": {"enum": ["stated", "tentative", "unclear"]},
                "due_expression": {"type": ["string", "null"], "minLength": 1, "maxLength": 200},
                "evidence": {"type": "array", "minItems": 1, "maxItems": 30, "items": {"$ref": "#/$defs/evidence"}},
                "rationale": {"type": "string", "minLength": 1, "maxLength": 1000}
              }
            }
          }
        }
        """;
}
