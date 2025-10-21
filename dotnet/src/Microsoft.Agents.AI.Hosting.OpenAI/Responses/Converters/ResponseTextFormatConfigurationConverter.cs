// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Models;

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses.Converters;

/// <summary>
/// JSON converter for <see cref="ResponseTextFormatConfiguration"/> polymorphic types.
/// </summary>
internal sealed class ResponseTextFormatConfigurationConverter : JsonConverter<ResponseTextFormatConfiguration>
{
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Types are known and preserved in JsonSerializerContext")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Types are known and preserved in JsonSerializerContext")]
    public override ResponseTextFormatConfiguration? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of object");
        }

        using JsonDocument doc = JsonDocument.ParseValue(ref reader);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("type", out JsonElement typeElement))
        {
            throw new JsonException("Missing required 'type' property");
        }

        string? type = typeElement.GetString();

        return type switch
        {
            "text" => JsonSerializer.Deserialize<ResponseTextFormatConfigurationText>(root.GetRawText(), options),
            "json_object" => JsonSerializer.Deserialize<ResponseTextFormatConfigurationJsonObject>(root.GetRawText(), options),
            "json_schema" => JsonSerializer.Deserialize<ResponseTextFormatConfigurationJsonSchema>(root.GetRawText(), options),
            _ => throw new JsonException($"Unknown response text format type: {type}")
        };
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Types are known and preserved in JsonSerializerContext")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "Types are known and preserved in JsonSerializerContext")]
    public override void Write(Utf8JsonWriter writer, ResponseTextFormatConfiguration value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
