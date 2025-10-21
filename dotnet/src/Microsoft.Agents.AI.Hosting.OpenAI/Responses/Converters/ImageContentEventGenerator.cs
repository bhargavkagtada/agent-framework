// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Common.Id;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Invocation;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Models;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses.Converters;

/// <summary>
/// A generator for streaming events from image content.
/// </summary>
internal sealed class ImageContentEventGenerator(
        IIdGenerator idGenerator,
        ISequenceNumber seq,
        int outputIndex) : StreamingEventGenerator
{
    private bool _isCompleted;

    public override bool IsSupported(AIContent content) =>
        (content is UriContent uriContent && uriContent.HasTopLevelMediaType("image")) ||
        (content is DataContent dataContent && dataContent.HasTopLevelMediaType("image"));

    public override IEnumerable<StreamingResponseEvent> ProcessContent(AIContent content)
    {
        if (this._isCompleted)
        {
            throw new InvalidOperationException("Cannot process content after the generator has been completed.");
        }

        ItemContentInputImage? itemContent = content switch
        {
            UriContent uriContent when uriContent.HasTopLevelMediaType("image") =>
                new ItemContentInputImage
                {
                    ImageUrl = uriContent.Uri?.ToString(),
                    Detail = GetImageDetail(content)
                },
            DataContent dataContent when dataContent.HasTopLevelMediaType("image") =>
                new ItemContentInputImage
                {
                    ImageUrl = dataContent.Uri,
                    Detail = GetImageDetail(content)
                },
            _ => null
        };

        if (itemContent == null)
        {
            throw new InvalidOperationException("ImageContentEventGenerator only supports image UriContent and DataContent.");
        }

        var itemId = idGenerator.GenerateMessageId();

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

    private static string? GetImageDetail(AIContent content)
    {
        if (content.AdditionalProperties?.TryGetValue("detail", out object? value) is true)
        {
            return value?.ToString();
        }

        return null;
    }
}
