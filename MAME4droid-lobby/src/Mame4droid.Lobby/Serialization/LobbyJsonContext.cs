/*
 * This file is part of MAME4droid (NetPlay lobby server).
 *
 * Copyright (C) 2026 David Valdeita (Seleuco)
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, see <http://www.gnu.org/licenses>.
 */

using System.Text.Json.Serialization;
using Mame4droid.Lobby.Contracts;

namespace Mame4droid.Lobby.Serialization;

/// Source-generated serialisation: no reflection at runtime, which is the one
/// CPU cost worth removing on a plan with a 60 min/day budget.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(StatsDto))]
[JsonSerializable(typeof(CreateRoomRequest))]
[JsonSerializable(typeof(CreateRoomResponse))]
[JsonSerializable(typeof(RoomListResponse))]
[JsonSerializable(typeof(RoomSummary))]
[JsonSerializable(typeof(JoinRequest))]
[JsonSerializable(typeof(JoinResponse))]
[JsonSerializable(typeof(PollRequest))]
[JsonSerializable(typeof(PollResponse))]
[JsonSerializable(typeof(TelemetryRequest))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(NatDto))]
public partial class LobbyJsonContext : JsonSerializerContext
{
}
