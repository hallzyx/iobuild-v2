namespace IoBuild.Api.Persistence;

/// <summary>
/// Profiles BC aggregate: Profile.
/// Location: Profiles/Domain/Model/Aggregates/Profile.cs
/// Namespace preserved for compatibility.
/// </summary>
public sealed class Profile
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string? PhotoReference { get; set; }
    public string? CloudinaryReference { get; set; }
}
