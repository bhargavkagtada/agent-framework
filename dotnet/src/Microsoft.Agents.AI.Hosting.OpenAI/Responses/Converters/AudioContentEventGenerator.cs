// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Common.Id;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Invocation;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Models;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses.Converters;

/// <summary>
/// A generator for streaming events from audio content.
/// </summary>
internal sealed class AudioContentEventGenerator(
        IIdGenerator idGenerator,
        ISequenceNumber seq,
        int outputIndex) : StreamingEventGenerator
{
    private bool _isCompleted;

    public override bool IsSupported(AIContent content) =>
        content is DataContent dataContent && dataContent.HasTopLevelMediaType("audio");

    public override IEnumerable<StreamingResponseEvent> ProcessContent(AIContent content)
    {
        if (this._isCompleted)
        {
            throw new InvalidOperationException("Cannot process content after the generator has been completed.");
        }

        if (content is not DataContent audioData || !audioData.HasTopLevelMediaType("audio"))
        {
            throw new InvalidOperationException("AudioContentEventGenerator only supports audio DataContent.");
        }

        var itemId = idGenerator.GenerateMessageId();
        var format = audioData.MediaType switch
        {
            string s when s.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase) => "mp3",
            string s when s.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) => "wav",
            string s when s.Equals("audio/opus", StringComparison.OrdinalIgnoreCase) => "opus",
            string s when s.Equals("audio/aac", StringComparison.OrdinalIgnoreCase) => "aac",
            string s when s.Equals("audio/flac", StringComparison.OrdinalIgnoreCase) => "flac",
            string s when s.Equals("audio/pcm", StringComparison.OrdinalIgnoreCase) => "pcm16",
            _ => "mp3" // Default to mp3
        };

        var itemContent = new ItemContentInputAudio
        {
            Data = audioData.Uri,
            Format = format
        };

        var item = new ResponsesAssistantMessageItemResource
        {
            Id = itemId,
            Status = ResponsesMessageItemResourceStatus.Completed,
            Content = [itemContent]
        };

        yield return new StreamingOutputItemAdded
        {
            SequenceNumber = seq.GetNext(),
            OutputIndex = outputIndex,
            Item = item
        };

        yield return new StreamingContentPartAdded
        {
            SequenceNumber = seq.GetNext(),
            ItemId = itemId,
            OutputIndex = outputIndex,
            ContentIndex = 0,
            Part = itemContent
        };

        yield return new StreamingContentPartDone
        {
            SequenceNumber = seq.GetNext(),
            ItemId = itemId,
            OutputIndex = outputIndex,
            ContentIndex = 0,
            Part = itemContent
        };

        yield return new StreamingOutputItemDone
        {
            SequenceNumber = seq.GetNext(),
            OutputIndex = outputIndex,
            Item = item
        };

        this._isCompleted = true;
    }

    public override IEnumerable<StreamingResponseEvent> Complete()
    {
        this._isCompleted = true;
        return [];
    }
}
