﻿using System.Text.Json.Serialization;

namespace FikaShared.Requests;

/// <summary>
/// 使用 ProfileId 的请求
/// </summary>
public record ProfileIdRequest
{
    [JsonPropertyName("profileId")]
    public required string ProfileId { get; init; }
}
