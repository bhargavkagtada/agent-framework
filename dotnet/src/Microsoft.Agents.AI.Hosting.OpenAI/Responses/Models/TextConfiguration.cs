// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses.Models;

/// <summary>
/// Configuration options for a text response from the model.
/// </summary>
internal sealed record TextConfiguration
{
    /// <summary>
    /// The format configuration for the text response.
    /// Can specify plain text, JSON object, or JSON schema for structured outputs.
    /// </summary>
    [JsonPropertyName("format")]
    public TextFormatConfiguration? Format { get; init; }
}

/// <summary>
/// Configuration for the format of a text response.
/// </summary>
internal sealed record TextFormatConfiguration
{
    /// <summary>
    /// The type of response format. One of "text", "json_object", or "json_schema".
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// The name of the response format (used with json_schema).
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Whether to enable strict schema adherence (used with json_schema).
    /// </summary>
    [JsonPropertyName("strict")]
    public bool? Strict { get; init; }

    /// <summary>
    /// The JSON schema for structured outputs (used with json_schema).
    /// </summary>
    [JsonPropertyName("schema")]
    public JsonElement? Schema { get; init; }

    /// <summary>
    /// A description of what the response format is for (used with json_schema).
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}
