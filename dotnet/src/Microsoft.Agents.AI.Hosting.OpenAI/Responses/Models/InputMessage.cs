// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Linq;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Converters;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses.Models;

/// <summary>
/// A message input to the model with a role indicating instruction following hierarchy.
/// Aligns with the OpenAI Responses API InputMessage/EasyInputMessage schema.
/// </summary>
internal sealed record InputMessage
{
    /// <summary>
    /// The role of the message input. One of user, assistant, system, or developer.
    /// </summary>
    [JsonPropertyName("role")]
    public required ChatRole Role { get; init; }

    /// <summary>
    /// Text, image, or audio input to the model, used to generate a response.
    /// Can be a simple string or a list of content items with different types.
    /// </summary>
    [JsonPropertyName("content")]
    public required InputMessageContent Content { get; init; }

    /// <summary>
    /// The type of the message input. Always "message".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type => "message";

    /// <summary>
    /// Converts this InputMessage to a ChatMessage.
    /// </summary>
    public ChatMessage ToChatMessage()
    {
        if (this.Content.IsText)
        {
            return new ChatMessage(this.Role, this.Content.Text!);
        }
        else if (this.Content.IsContents)
        {
            // Convert ItemContent to AIContent
            var aiContents = this.Content.Contents!.Select(ConvertToAIContent).Where(c => c is not null).ToList();
            return new ChatMessage(this.Role, aiContents!);
        }

        throw new InvalidOperationException("InputMessageContent has no value");
    }

    /// <summary>
    /// Creates an InputMessage from a ChatMessage.
    /// </summary>
    public static InputMessage FromChatMessage(ChatMessage message)
    {
        return new InputMessage
        {
            Role = message.Role,
            Content = !string.IsNullOrEmpty(message.Text)
                ? InputMessageContent.FromText(message.Text)
                : InputMessageContent.FromContents(message.Contents.Select(c => c.ToInputItemContent()).Where(c => c is not null).ToArray()!)
        };
    }

    private static AIContent? ConvertToAIContent(ItemContent itemContent)
    {
        // Check if we already have the raw representation to avoid unnecessary conversion
        if (itemContent.RawRepresentation is AIContent rawContent)
        {
            return rawContent;
        }

        AIContent? aiContent = itemContent switch
        {
            // Text content
            ItemContentInputText inputText => new TextContent(inputText.Text),
            ItemContentOutputText outputText => new TextContent(outputText.Text),

            // Error/refusal content
            ItemContentRefusal refusal => new ErrorContent(refusal.Refusal),

            // Image content
            ItemContentInputImage inputImage when !string.IsNullOrEmpty(inputImage.ImageUrl) =>
                inputImage.ImageUrl!.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? new DataContent(inputImage.ImageUrl, "image/*")
                    : new UriContent(inputImage.ImageUrl, "image/*"),
            ItemContentInputImage inputImage when !string.IsNullOrEmpty(inputImage.FileId) =>
                new HostedFileContent(inputImage.FileId!),

            // File content
            ItemContentInputFile inputFile when !string.IsNullOrEmpty(inputFile.FileId) =>
                new HostedFileContent(inputFile.FileId!),
            ItemContentInputFile inputFile when !string.IsNullOrEmpty(inputFile.FileData) =>
                new DataContent(inputFile.FileData!, "application/octet-stream"),

            // Audio content - map to DataContent with media type based on format
            ItemContentInputAudio inputAudio =>
                new DataContent(inputAudio.Data, inputAudio.Format?.ToUpperInvariant() switch
                {
                    "MP3" => "audio/mpeg",
                    "WAV" => "audio/wav",
                    "OPUS" => "audio/opus",
                    "AAC" => "audio/aac",
                    "FLAC" => "audio/flac",
                    "PCM16" => "audio/pcm",
                    _ => "audio/*"
                }),
            ItemContentOutputAudio outputAudio =>
                new DataContent(outputAudio.Data, "audio/*"),

            _ => null
        };

        if (aiContent is not null)
        {
            // Add image detail to additional properties if present
            if (itemContent is ItemContentInputImage { Detail: not null } image)
            {
                (aiContent.AdditionalProperties ??= [])["detail"] = image.Detail;
            }

            // Preserve the original ItemContent as raw representation for round-tripping
            aiContent.RawRepresentation = itemContent;
        }

        return aiContent;
    }
}
