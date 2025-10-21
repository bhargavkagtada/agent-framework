// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Common;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Common.Id;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Invocation;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Models;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses.Converters;

/// <summary>
/// Extension methods for converting agent responses to Response models.
/// </summary>
internal static class ResponseConverterExtensions
{
    /// <summary>
    /// Converts an AgentRunResponse to a Response model.
    /// </summary>
    /// <param name="agentRunResponse">The agent run response to convert.</param>
    /// <param name="request">The original create response request.</param>
    /// <param name="context">The agent invocation context.</param>
    /// <returns>A Response model.</returns>
    public static Response ToResponse(this AgentRunResponse agentRunResponse, CreateResponse request,
        AgentInvocationContext context)
    {
        var output = agentRunResponse.Messages
            .SelectMany(msg => msg.ToItemResource(context.IdGenerator, context.JsonSerializerOptions));

        return new Response
        {
            Id = context.ResponseId,
            CreatedAt = (agentRunResponse.CreatedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
            Model = request.Agent?.Name ?? request.Model,
            Status = ResponseStatus.Completed,
            Agent = request.Agent?.ToAgentId(),
            Conversation = request.Conversation ?? (context.ConversationId != null ? new ConversationReference { Id = context.ConversationId } : null),
            Metadata = request.Metadata is IReadOnlyDictionary<string, string> metadata ? new Dictionary<string, string>(metadata) : [],
            Instructions = request.Instructions,
            Temperature = request.Temperature ?? 1.0,
            TopP = request.TopP ?? 1.0,
            Output = output.ToList(),
            Usage = agentRunResponse.Usage.ToResponseUsage(),
            ParallelToolCalls = request.ParallelToolCalls ?? true,
            Tools = request.Tools?.Select(ProcessTool).ToList() ?? [],
            ToolChoice = request.ToolChoice,
            ServiceTier = "default",
            Store = request.Store ?? true,
            PreviousResponseId = request.PreviousResponseId,
            Reasoning = request.Reasoning,
            Text = request.Text,
            MaxOutputTokens = request.MaxOutputTokens
        };
    }

    /// <summary>
    /// Processes a tool definition to add strict mode flags required by OpenAI.
    /// </summary>
    /// <param name="tool">The tool definition from the request.</param>
    /// <returns>A processed tool definition with strict flags added.</returns>
    public static JsonElement ProcessTool(JsonElement tool)
    {
        // If the tool is not a function tool, return it as-is
        if (!tool.TryGetProperty("type", out var typeElement) || typeElement.GetString() != "function")
        {
            return tool;
        }

        // Clone the tool and add strict mode flags
        using var doc = JsonDocument.Parse(tool.GetRawText());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            // Copy existing properties
            foreach (var property in tool.EnumerateObject())
            {
                if (property.Name == "parameters")
                {
                    // Process parameters to add strict flags
                    writer.WritePropertyName("parameters");
                    ProcessParameters(property.Value, writer);
                }
                else if (property.Name != "strict")
                {
                    // Copy other properties except strict (we'll add it explicitly)
                    property.WriteTo(writer);
                }
            }

            // Add strict flag
            writer.WriteBoolean("strict", true);

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>
    /// Processes tool parameters to add additionalProperties flag and ensure all properties are required.
    /// </summary>
    private static void ProcessParameters(JsonElement parameters, Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        JsonElement? propertiesElement = null;
        var existingRequired = new HashSet<string>();

        // First pass: copy properties except required and additionalProperties, and track what we need
        foreach (var property in parameters.EnumerateObject())
        {
            if (property.Name == "properties")
            {
                propertiesElement = property.Value;
                property.WriteTo(writer);
            }
            else if (property.Name == "required" && property.Value.ValueKind == JsonValueKind.Array)
            {
                // Track existing required fields but don't write yet
                foreach (var item in property.Value.EnumerateArray())
                {
                    var str = item.GetString();
                    if (str != null)
                    {
                        existingRequired.Add(str);
                    }
                }
            }
            else if (property.Name != "additionalProperties")
            {
                // Copy other properties
                property.WriteTo(writer);
            }
        }

        // Write required array with all properties
        if (propertiesElement.HasValue && propertiesElement.Value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartArray("required");
            foreach (var prop in propertiesElement.Value.EnumerateObject())
            {
                writer.WriteStringValue(prop.Name);
            }
            writer.WriteEndArray();
        }

        // Add additionalProperties flag
        writer.WriteBoolean("additionalProperties", false);

        writer.WriteEndObject();
    }

    /// <summary>
    /// Converts a ChatMessage to ItemResource objects.
    /// </summary>
    /// <param name="message">The chat message to convert.</param>
    /// <param name="idGenerator">The ID generator to use for creating IDs.</param>
    /// <param name="jsonSerializerOptions">The JSON serializer options to use.</param>
    /// <returns>An enumerable of ItemResource objects.</returns>
    public static IEnumerable<ItemResource> ToItemResource(this ChatMessage message, IIdGenerator idGenerator, JsonSerializerOptions jsonSerializerOptions)
    {
        IList<ItemContent> contents = [];
        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case FunctionCallContent functionCallContent:
                    // message.Role == ChatRole.Assistant
                    yield return functionCallContent.ToFunctionToolCallItemResource(idGenerator.GenerateFunctionCallId(), jsonSerializerOptions);
                    break;
                case FunctionResultContent functionResultContent:
                    // message.Role == ChatRole.Tool
                    yield return functionResultContent.ToFunctionToolCallOutputItemResource(
                        idGenerator.GenerateFunctionOutputId());
                    break;
                default:
                    // message.Role == ChatRole.Assistant
                    var itemContent = content.ToOutputItemContent();
                    if (itemContent != null)
                    {
                        contents.Add(itemContent);
                    }

                    break;
            }
        }

        if (contents.Count > 0)
        {
            yield return new ResponsesAssistantMessageItemResource
            {
                Id = idGenerator.GenerateMessageId(),
                Status = ResponsesMessageItemResourceStatus.Completed,
                Content = contents
            };
        }
    }

    /// <summary>
    /// Converts FunctionCallContent to a FunctionToolCallItemResource.
    /// </summary>
    /// <param name="functionCallContent">The function call content to convert.</param>
    /// <param name="id">The ID to assign to the resource.</param>
    /// <param name="jsonSerializerOptions">The JSON serializer options to use.</param>
    /// <returns>A FunctionToolCallItemResource.</returns>
    public static FunctionToolCallItemResource ToFunctionToolCallItemResource(
        this FunctionCallContent functionCallContent,
        string id,
        JsonSerializerOptions jsonSerializerOptions)
    {
        return new FunctionToolCallItemResource
        {
            Id = id,
            Status = FunctionToolCallItemResourceStatus.Completed,
            CallId = functionCallContent.CallId,
            Name = functionCallContent.Name,
            Arguments = JsonSerializer.Serialize(functionCallContent.Arguments, jsonSerializerOptions.GetTypeInfo(typeof(IDictionary<string, object?>)))
        };
    }

    /// <summary>
    /// Converts FunctionResultContent to a FunctionToolCallOutputItemResource.
    /// </summary>
    /// <param name="functionResultContent">The function result content to convert.</param>
    /// <param name="id">The ID to assign to the resource.</param>
    /// <returns>A FunctionToolCallOutputItemResource.</returns>
    public static FunctionToolCallOutputItemResource ToFunctionToolCallOutputItemResource(
        this FunctionResultContent functionResultContent,
        string id)
    {
        var output = functionResultContent.Exception is not null
            ? $"{functionResultContent.Exception.GetType().Name}(\"{functionResultContent.Exception.Message}\")"
            : $"{functionResultContent.Result?.ToString() ?? "(null)"}";
        return new FunctionToolCallOutputItemResource
        {
            Id = id,
            Status = FunctionToolCallOutputItemResourceStatus.Completed,
            CallId = functionResultContent.CallId,
            Output = output
        };
    }

    /// <summary>
    /// Converts UsageDetails to ResponseUsage.
    /// </summary>
    /// <param name="usage">The usage details to convert.</param>
    /// <returns>A ResponseUsage object with zeros if usage is null.</returns>
    public static ResponseUsage ToResponseUsage(this UsageDetails? usage)
    {
        if (usage == null)
        {
            return ResponseUsage.Zero;
        }

        var cachedTokens = usage.AdditionalCounts?.TryGetValue("InputTokenDetails.CachedTokenCount", out var cachedInputToken) ?? false
            ? (int)cachedInputToken
            : 0;
        var reasoningTokens =
            usage.AdditionalCounts?.TryGetValue("OutputTokenDetails.ReasoningTokenCount", out var reasoningToken) ?? false
                ? (int)reasoningToken
                : 0;

        return new ResponseUsage
        {
            InputTokens = (int)(usage.InputTokenCount ?? 0),
            InputTokensDetails = new InputTokensDetails { CachedTokens = cachedTokens },
            OutputTokens = (int)(usage.OutputTokenCount ?? 0),
            OutputTokensDetails = new OutputTokensDetails { ReasoningTokens = reasoningTokens },
            TotalTokens = (int)(usage.TotalTokenCount ?? 0)
        };
    }

    /// <summary>
    /// Converts AIContent to ItemContent for input messages.
    /// </summary>
    /// <param name="content">The AI content to convert.</param>
    /// <returns>An ItemContent object, or null if the content cannot be converted.</returns>
    public static ItemContent? ToInputItemContent(this AIContent content)
    {
        return content.ToItemContentCore(isInput: true);
    }

    /// <summary>
    /// Converts AIContent to ItemContent for output messages.
    /// </summary>
    /// <param name="content">The AI content to convert.</param>
    /// <returns>An ItemContent object, or null if the content cannot be converted.</returns>
    public static ItemContent? ToOutputItemContent(this AIContent content)
    {
        return content.ToItemContentCore(isInput: false);
    }

    private static ItemContent? ToItemContentCore(this AIContent content, bool isInput)
    {
        // Check if we already have the raw representation to avoid unnecessary conversion
        if (content.RawRepresentation is ItemContent itemContent)
        {
            return itemContent;
        }

        ItemContent? result = content switch
        {
            TextContent textContent => isInput
                ? new ItemContentInputText { Text = textContent.Text ?? string.Empty }
                : new ItemContentOutputText { Text = textContent.Text ?? string.Empty, Annotations = [], Logprobs = [] },
            TextReasoningContent reasoningContent => isInput
                ? throw new InvalidOperationException("TextReasoningContent cannot be used as input content. TextReasoningContent represents reasoning output from the model, not user input.")
                : new ItemContentOutputText { Text = reasoningContent.Text ?? string.Empty, Annotations = [], Logprobs = [] },
            ErrorContent errorContent => isInput
                ? throw new InvalidOperationException("ErrorContent cannot be used as input content. ErrorContent represents errors or refusals from the model, not user input.")
                : new ItemContentRefusal { Refusal = errorContent.Message ?? string.Empty },
            UriContent uriContent when uriContent.HasTopLevelMediaType("image") =>
                new ItemContentInputImage
                {
                    ImageUrl = uriContent.Uri?.ToString(),
                    Detail = GetImageDetail(uriContent)
                },
            DataContent dataContent when dataContent.HasTopLevelMediaType("image") =>
                new ItemContentInputImage
                {
                    ImageUrl = dataContent.Uri,
                    Detail = GetImageDetail(dataContent)
                },
            HostedFileContent hostedFile =>
                new ItemContentInputFile
                {
                    FileId = hostedFile.FileId
                },
            DataContent fileData when !fileData.HasTopLevelMediaType("image") && !fileData.HasTopLevelMediaType("audio") =>
                new ItemContentInputFile
                {
                    FileData = fileData.Uri,
                    Filename = fileData.Name
                },
            DataContent audioData when audioData.HasTopLevelMediaType("audio") =>
                new ItemContentInputAudio
                {
                    Data = audioData.Uri,
                    Format = audioData.MediaType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase) ? "mp3" :
                        audioData.MediaType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) ? "wav" :
                        audioData.MediaType.Equals("audio/opus", StringComparison.OrdinalIgnoreCase) ? "opus" :
                        audioData.MediaType.Equals("audio/aac", StringComparison.OrdinalIgnoreCase) ? "aac" :
                        audioData.MediaType.Equals("audio/flac", StringComparison.OrdinalIgnoreCase) ? "flac" :
                        audioData.MediaType.Equals("audio/pcm", StringComparison.OrdinalIgnoreCase) ? "pcm16" :
                        "mp3" // Default to mp3
                },

            // Other AIContent types (FunctionCallContent, FunctionResultContent, etc.)
            // are handled separately in the Responses API as different ItemResource types, not ItemContent
            _ => null
        };

        if (result is not null)
        {
            result.RawRepresentation = content;
        }

        return result;
    }

    /// <summary>
    /// Extracts the image detail level from AIContent's additional properties.
    /// </summary>
    /// <param name="content">The AIContent to extract detail from.</param>
    /// <returns>The detail level as a string, or null if not present.</returns>
    private static string? GetImageDetail(AIContent content)
    {
        if (content.AdditionalProperties?.TryGetValue("detail", out object? value) is true)
        {
            return value?.ToString();
        }

        return null;
    }
}
