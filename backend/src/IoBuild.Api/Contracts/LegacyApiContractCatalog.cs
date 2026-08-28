namespace IoBuild.Api.Contracts;

public sealed record LegacyRouteContract(
    string Method,
    string Path,
    int SuccessStatusCode,
    bool AllowsAnonymous,
    string JsonShape);

public static class LegacyApiContractCatalog
{
    public static IReadOnlyList<LegacyRouteContract> All { get; } =
    [
        new("POST", "/api/v1/sessions", 201, true, "authenticated-user"),
        new("POST", "/api/v1/users", 201, true, "message"),
        new("DELETE", "/api/v1/sessions/current", 204, false, "empty"),
        new("GET", "/api/v1/users", 200, false, "array")
    ];
}
