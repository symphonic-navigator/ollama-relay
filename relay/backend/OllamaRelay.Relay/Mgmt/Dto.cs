using MongoDB.Bson;
using OllamaRelay.Relay.Data;

namespace OllamaRelay.Relay.Mgmt;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record UserView(string Id, string Username, bool IsAdmin, bool MustChangePassword);

public sealed record SessionResponse(string AccessToken, UserView User);

public sealed record TokenRequest(string? Token);

public sealed record RedeemInviteRequest(string? Token, string? Username, string? Password);

public sealed record RedeemResetRequest(string? Token, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record CreateKeyRequest(string? Name);

public sealed record CreateNodeRequest(string? Name, int? MaxConcurrency);

public sealed record UpdateNodeRequest(string? Name, int? MaxConcurrency);

public sealed record CreatePublicationRequest(string? NodeId, string? UpstreamModel, string? Alias);

public sealed record UpdatePublicationRequest(string? Alias);

public sealed record UpdateUserRequest(bool? Disabled, bool? IsAdmin);

public sealed record CreateInviteRequest(bool? Admin);

public sealed record SetGrantRequest(string? UserId, string? Alias, string? PublicationId, bool Granted);

public static class Views
{
    public static UserView Of(UserDoc u) => new(u.Id.ToString(), u.Username, u.IsAdmin, u.MustChangePassword);

    public static bool TryId(string? value, out ObjectId id) => ObjectId.TryParse(value, out id);
}
