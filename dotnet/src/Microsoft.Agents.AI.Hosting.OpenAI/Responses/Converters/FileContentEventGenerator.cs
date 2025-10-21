// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Common.Id;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Invocation;
using Microsoft.Agents.AI.Hosting.OpenAI.Responses.Models;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.Hosting.OpenAI.Responses.Converters;

/// <summary>
/// A generator for streaming events from file content (non-image, non-audio DataContent).
/// </summary>
internal sealed class FileContentEventGenerator(
        IIdGenerator idGenerator,
        ISequenceNumber seq,
        int outputIndex) : StreamingEventGenerator
{
    private bool _isCompleted;

    public override bool IsSupported(AIContent content) =>
        content is DataContent dataContent &&
        !dataContent.HasTopLevelMediaType("image") &&
        !dataContent.HasTopLevelMediaType("audio");

    public override IEnumerable<StreamingResponseEvent> ProcessContent(AIContent content)
    {
        if (this._isCompleted)
        {
            throw new InvalidOperationException("Cannot process content after the generator has been completed.");
        }

        if (content is not DataContent fileData ||
            fileData.HasTopLevelMediaType("image") ||
            fileData.HasTopLevelMediaType("audio"))
        {
            throw new InvalidOperationException("FileContentEventGenerator only supports non-image, non-audio DataContent.");
        }

        var itemId = idGenerator.GenerateMessageId();
        var itemContent = new ItemContentInputFile
        {
            FileData = fileData.Uri,
            Filename = fileData.Name
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
