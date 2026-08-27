using System.Text.Json.Serialization;
using Mame4droid.Lobby.Contracts;

namespace Mame4droid.Lobby.Serialization;

/// Source-generated serialisation: no reflection at runtime, which is the one
/// CPU cost worth removing on a plan with a 60 min/day budget.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConfigResponse))]
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
